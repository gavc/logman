using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LogMan.Models;
using LogMan.Services;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace LogMan.ViewModels
{
    [SupportedOSPlatform("windows")]
    public class MainViewModel : ViewModelBase
    {
        private readonly ILogProvider _logProvider;
        private readonly FastObservableCollection<LogEntry> _allEntries;
        private CancellationTokenSource? _messagePrefetchCts;
        private readonly Dictionary<int, int> _highlightIndexByEventId = new();
        private int _nextHighlightIndex;
        private readonly System.Windows.Threading.DispatcherTimer _searchDebounceTimer;
        private readonly System.Windows.Threading.DispatcherTimer _liveUpdateThrottleTimer;
        private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _liveEntryBuffer = new();
        private bool _needsFilteredCountUpdate;
        private bool _acceptLiveEntries;

        public ICollectionView LogEntriesView { get; }
        public ObservableCollection<LogSourceViewModel> LogSources { get; }

        private bool _showCritical = true;
        public bool ShowCritical
        {
            get => _showCritical;
            set { if (SetProperty(ref _showCritical, value)) RefreshFilteredView(); }
        }

        private bool _showError = true;
        public bool ShowError
        {
            get => _showError;
            set { if (SetProperty(ref _showError, value)) RefreshFilteredView(); }
        }

        private bool _showWarning = true;
        public bool ShowWarning
        {
            get => _showWarning;
            set { if (SetProperty(ref _showWarning, value)) RefreshFilteredView(); }
        }

        private bool _showInfo = true;
        public bool ShowInfo
        {
            get => _showInfo;
            set { if (SetProperty(ref _showInfo, value)) RefreshFilteredView(); }
        }

        private bool _showVerbose = true;
        public bool ShowVerbose
        {
            get => _showVerbose;
            set { if (SetProperty(ref _showVerbose, value)) RefreshFilteredView(); }
        }

        public LogEntry? SelectedEntry
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    _ = LoadDetailsForSelectionAsync(value);
                }
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (SetProperty(ref _isLoading, value))
                {
                    OnPropertyChanged(nameof(CanManageSources));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string SearchText
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    ScheduleFilteredRefresh();
                }
            }
        } = string.Empty;

        public int FilteredCount
        {
            get;
            private set => SetProperty(ref field, value);
        }

        public string NewMachineName
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool CanManageSources => !IsLoading;

        private bool _isLiveCaptureEnabled;
        public bool IsLiveCaptureEnabled
        {
            get => _isLiveCaptureEnabled;
            set
            {
                if (SetProperty(ref _isLiveCaptureEnabled, value))
                {
                    if (value) StartLiveCapture();
                    else StopLiveCapture();
                }
            }
        }

        public ICommand LoadFileCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand HighlightEventCommand { get; }
        public ICommand ClearHighlightsCommand { get; }
        public ICommand CopyEventCommand { get; }
        public ICommand SearchBingByIdCommand { get; }
        public ICommand SearchBingByMessageCommand { get; }
        public ICommand AddMachineCommand { get; }
        public ICommand ExportCommand { get; }

        public MainViewModel()
        {
            _logProvider = new EvtxLogProvider();
            _allEntries = new FastObservableCollection<LogEntry>();
            LogEntriesView = CollectionViewSource.GetDefaultView(_allEntries);
            LogEntriesView.Filter = FilterLogEntries;

            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                RefreshFilteredView();
            };

            _liveUpdateThrottleTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _liveUpdateThrottleTimer.Tick += (s, e) =>
            {
                UpdateLiveEntriesFromBuffer();
            };
            _liveUpdateThrottleTimer.Start();

            LogSources = new ObservableCollection<LogSourceViewModel>();
            AddLocalSources();

            _logProvider.NewEntryReceived += OnNewEntryReceived;

            LoadFileCommand = new RelayCommand(async _ => await LoadFile(), _ => !IsLoading);
            ClearCommand = new RelayCommand(_ => ClearEntries(), _ => !IsLoading && _allEntries.Count > 0);
            HighlightEventCommand = new RelayCommand(entry => HighlightEntriesFor(entry as LogEntry), entry => entry is LogEntry);
            ClearHighlightsCommand = new RelayCommand(_ => ClearHighlights());
            CopyEventCommand = new RelayCommand(async entry => await CopyEventAsync(entry as LogEntry), entry => entry is LogEntry);
            SearchBingByIdCommand = new RelayCommand(entry => SearchBingById(entry as LogEntry), entry => entry is LogEntry && (entry as LogEntry)?.EventId != null);
            SearchBingByMessageCommand = new RelayCommand(entry => SearchBingByMessage(entry as LogEntry), entry => entry is LogEntry && !string.IsNullOrEmpty((entry as LogEntry)?.MessagePreview));
            AddMachineCommand = new RelayCommand(_ => AddMachine(), _ => !IsLoading && !string.IsNullOrWhiteSpace(NewMachineName));
            ExportCommand = new RelayCommand(async _ => await ExportEntriesAsync(), _ => !IsLoading && _allEntries.Count > 0);
        }

        private void UpdateFilteredCount()
        {
            var count = 0;
            foreach (var _ in LogEntriesView)
            {
                count++;
            }
            FilteredCount = count;
        }

        private void RefreshFilteredView()
        {
            LogEntriesView.Refresh();
            UpdateFilteredCount();
        }

        private void ScheduleFilteredRefresh()
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                _searchDebounceTimer.Stop();
                _searchDebounceTimer.Start();
                return;
            }

            _ = dispatcher.BeginInvoke((Action)ScheduleFilteredRefresh);
        }

        private void FlushPendingFilteredRefresh()
        {
            if (_searchDebounceTimer.IsEnabled)
            {
                _searchDebounceTimer.Stop();
            }

            RefreshFilteredView();
        }

        private void RequestFilteredRefreshIfSearchActive()
        {
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                ScheduleFilteredRefresh();
            }
        }

        private void NotifyEntriesChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        private void AddLocalSources()
        {
            LogSources.Add(new LogSourceViewModel { Name = "Application", MachineName = "Local", IsSelected = true });
            LogSources.Add(new LogSourceViewModel { Name = "System", MachineName = "Local", IsSelected = true });
            LogSources.Add(new LogSourceViewModel { Name = "Security", MachineName = "Local", IsSelected = false });
            LogSources.Add(new LogSourceViewModel { Name = "Setup", MachineName = "Local", IsSelected = false });
        }

        private void AddMachine()
        {
            var machineName = NewMachineName.Trim();
            if (string.Equals(machineName, "Local", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                NewMachineName = string.Empty;
                return;
            }

            if (LogSources.Any(s => string.Equals(s.MachineName, machineName, StringComparison.OrdinalIgnoreCase)))
            {
                NewMachineName = string.Empty;
                return;
            }

            LogSources.Add(new LogSourceViewModel { Name = "Application", MachineName = machineName, IsSelected = true });
            LogSources.Add(new LogSourceViewModel { Name = "System", MachineName = machineName, IsSelected = true });
            LogSources.Add(new LogSourceViewModel { Name = "Security", MachineName = machineName, IsSelected = false });
            LogSources.Add(new LogSourceViewModel { Name = "Setup", MachineName = machineName, IsSelected = false });

            NewMachineName = string.Empty;
        }

        private bool FilterLogEntries(object obj)
        {
            if (obj is not LogEntry entry) return false;

            // Level Filtering
            var isVisible = entry.LogLevel switch
            {
                1 => ShowCritical,
                2 => ShowError,
                3 => ShowWarning,
                4 => ShowInfo,
                5 => ShowVerbose,
                _ => true
            };

            if (!isVisible) return false;

            if (string.IsNullOrWhiteSpace(SearchText)) return true;

            var messageText = entry.IsDetailsLoaded
                ? entry.Message
                : entry.IsPreviewLoaded ? entry.MessagePreview : string.Empty;

            var messageMatches = !string.IsNullOrWhiteSpace(messageText) &&
                                 !string.Equals(messageText, LogEntry.LazyMessagePlaceholder, StringComparison.OrdinalIgnoreCase) &&
                                 messageText.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

            return messageMatches ||
                   entry.Source.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   entry.MachineName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                   (entry.EventId.HasValue && entry.EventId.Value.ToString().Contains(SearchText));
        }

        private async Task LoadFile()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Event Log files (*.evtx)|*.evtx|All files (*.*)|*.*",
                Multiselect = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                IsLoading = true;
                CancelMessagePrefetch();
                DisableLiveCaptureForImport();
                try
                {
                    FlushPendingFilteredRefresh();
                    var existingEntries = _allEntries.ToList();
                    var result = await Task.Run(async () =>
                    {
                        var combined = new List<LogEntry>();
                        var failures = new List<string>();

                        foreach (var fileName in openFileDialog.FileNames)
                        {
                            var fileEntries = new List<LogEntry>();
                            try
                            {
                                await foreach (var batch in _logProvider.LoadFromFileAsync(fileName))
                                {
                                    if (batch.Count > 0)
                                    {
                                        fileEntries.AddRange(batch);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                failures.Add(ex.Message);
                                continue;
                            }

                            combined.AddRange(fileEntries);
                        }

                        combined.Sort(LogEntryTimestampComparer.Instance);

                        return new FileLoadResult(
                            combined.Count > 0 ? MergeSortedEntries(existingEntries, combined) : null,
                            failures);
                    });

                    if (result.MergedEntries != null)
                    {
                        _allEntries.Clear();
                        _allEntries.AddRange(result.MergedEntries);
                        ApplyExistingHighlights();
                        StartMessagePrefetch(result.MergedEntries);
                    }

                    RefreshFilteredView();
                    NotifyEntriesChanged();

                    if (result.Failures.Count > 0)
                    {
                        var title = result.MergedEntries == null ? "Load Failed" : "Load Completed with Warnings";
                        var summary = result.MergedEntries == null
                            ? "No entries were loaded."
                            : "Some files could not be loaded.";
                        System.Windows.MessageBox.Show(
                            $"{summary}{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, result.Failures)}",
                            title,
                            System.Windows.MessageBoxButton.OK,
                            result.MergedEntries == null ? System.Windows.MessageBoxImage.Error : System.Windows.MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error loading files: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                finally
                {
                    IsLoading = false;
                }
            }
        }

        private sealed class FileLoadResult
        {
            public FileLoadResult(List<LogEntry>? mergedEntries, List<string> failures)
            {
                MergedEntries = mergedEntries;
                Failures = failures;
            }

            public List<LogEntry>? MergedEntries { get; }
            public List<string> Failures { get; }
        }

        private void DisableLiveCaptureForImport()
        {
            if (!_isLiveCaptureEnabled)
            {
                UpdateLiveEntriesFromBuffer();
                return;
            }

            _isLiveCaptureEnabled = false;
            OnPropertyChanged(nameof(IsLiveCaptureEnabled));
            StopLiveCapture();
        }

        private void FlushLiveEntryBuffer()
        {
            var batch = new List<LogEntry>();
            while (_liveEntryBuffer.TryDequeue(out var entry))
            {
                if (entry.EventId.HasValue &&
                    _highlightIndexByEventId.TryGetValue(entry.EventId.Value, out var index))
                {
                    entry.HighlightIndex = index;
                }

                batch.Add(entry);
            }

            if (batch.Count == 0)
            {
                return;
            }

            _allEntries.InsertRange(0, batch);
            _needsFilteredCountUpdate = true;
            NotifyEntriesChanged();
        }

        private void UpdateLiveEntriesFromBuffer()
        {
            FlushLiveEntryBuffer();
            if (_needsFilteredCountUpdate)
            {
                _needsFilteredCountUpdate = false;
                UpdateFilteredCount();
            }
        }

        private async Task ExportEntriesAsync()
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
                Title = "Export Logs"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                IsLoading = true;
                try
                {
                    FlushPendingFilteredRefresh();
                    var fileName = saveFileDialog.FileName;
                    var isJson = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

                    // Snapshot the current filtered view to avoid changes while exporting
                    var entriesToExport = LogEntriesView.Cast<LogEntry>().ToList();
                    var count = entriesToExport.Count;

                    await Task.Run(async () =>
                    {
                        using var fileStream = new System.IO.FileStream(fileName, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 4096, true);

                        if (isJson)
                        {
                            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                            await System.Text.Json.JsonSerializer.SerializeAsync(fileStream, entriesToExport, options);
                        }
                        else
                        {
                            using var writer = new System.IO.StreamWriter(fileStream, Encoding.UTF8);
                            await writer.WriteLineAsync("Timestamp,Level,Machine,Log Name,Source,Event ID,Message");
                            foreach (var entry in entriesToExport)
                            {
                                var message = (entry.IsDetailsLoaded ? entry.Message : entry.MessagePreview).Replace("\"", "\"\"");
                                await writer.WriteLineAsync($"\"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\",\"{entry.Level}\",\"{entry.MachineName}\",\"{entry.LogName}\",\"{entry.Source}\",\"{entry.EventId}\",\"{message}\"");
                            }
                        }
                    });

                    System.Windows.MessageBox.Show($"Exported {count} entries successfully.", "Export Complete", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error exporting logs: {ex.Message}", "Export Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
                finally
                {
                    IsLoading = false;
                }
            }
        }

        private void StartLiveCapture()
        {
            var selectedLogs = LogSources
                .Where(l => l.IsSelected)
                .Select(l => (l.MachineName, l.Name))
                .ToList();

            if (selectedLogs.Any())
            {
                CancelMessagePrefetch();
                _acceptLiveEntries = true;
                _logProvider.StartLiveWatching(selectedLogs);
            }
            else
            {
                IsLiveCaptureEnabled = false; // Turn off if nothing selected
                System.Windows.MessageBox.Show("Please select at least one log source to monitor.", "No Sources Selected", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        private void StopLiveCapture()
        {
            _acceptLiveEntries = false;
            _logProvider.StopLiveWatching();
            UpdateLiveEntriesFromBuffer();
        }

        private void ClearEntries()
        {
            CancelMessagePrefetch();
            _allEntries.Clear();
            ClearHighlights();
            RefreshFilteredView();
            NotifyEntriesChanged();
        }

        private void OnNewEntryReceived(LogEntry entry)
        {
            if (!_acceptLiveEntries)
            {
                return;
            }

            _liveEntryBuffer.Enqueue(entry);
        }

        public void HighlightEntriesFor(LogEntry? entry)
        {
            if (entry?.EventId == null)
            {
                return;
            }

            var eventId = entry.EventId.Value;
            if (!_highlightIndexByEventId.TryGetValue(eventId, out var index))
            {
                index = _nextHighlightIndex % 10;
                _nextHighlightIndex++;
                _highlightIndexByEventId[eventId] = index;
            }
            else
            {
                // Toggle off if already highlighted
                _highlightIndexByEventId.Remove(eventId);
                foreach (var item in _allEntries.Where(i => i.EventId == eventId))
                {
                    item.HighlightIndex = null;
                }
                return;
            }

            foreach (var item in _allEntries)
            {
                if (item.EventId == eventId)
                {
                    item.HighlightIndex = index;
                }
            }
        }

        private void SearchBingById(LogEntry? entry)
        {
            if (entry == null || entry.EventId == null) return;

            // Targeted search: "Windows Event" + Source + EventID
            var query = $"Windows Event {entry.Source} {entry.EventId}";
            ExecuteSearch(query);
        }

        private void SearchBingByMessage(LogEntry? entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.MessagePreview)) return;

            // Specific search: just the message summary/preview
            var query = entry.MessagePreview;
            ExecuteSearch(query);
        }

        private void ExecuteSearch(string query)
        {
            var encodedQuery = System.Net.WebUtility.UrlEncode(query);
            var url = $"https://www.bing.com/search?q={encodedQuery}";

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Search Error: {ex.Message}");
            }
        }

        private async Task CopyEventAsync(LogEntry? entry)
        {
            if (entry == null)
            {
                return;
            }

            if (!entry.IsDetailsLoaded && !entry.IsDetailsLoading)
            {
                await LoadDetailsForSelectionAsync(entry);
            }

            var message = entry.IsDetailsLoaded ? entry.Message : entry.MessagePreview;
            var rawData = entry.IsDetailsLoaded ? entry.RawData : string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine($"Timestamp: {entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            builder.AppendLine($"Machine: {entry.MachineName}");
            builder.AppendLine($"Log Name: {entry.LogName}");
            builder.AppendLine($"Source: {entry.Source}");
            builder.AppendLine($"Level: {entry.Level}");
            builder.AppendLine($"Event ID: {entry.EventId}");
            builder.AppendLine($"Category: {entry.Category}");

            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.AppendLine("Message:");
                builder.AppendLine(message);
            }

            if (!string.IsNullOrWhiteSpace(rawData))
            {
                builder.AppendLine();
                builder.AppendLine("Details:");
                builder.AppendLine(rawData);
            }

            var text = builder.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                System.Windows.Clipboard.SetText(text);
            }
        }

        private void ClearHighlights()
        {
            _highlightIndexByEventId.Clear();
            _nextHighlightIndex = 0;

            foreach (var entry in _allEntries)
            {
                if (entry.HighlightIndex != null)
                {
                    entry.HighlightIndex = null;
                }
            }
        }

        private void ApplyExistingHighlights()
        {
            if (_highlightIndexByEventId.Count == 0)
            {
                return;
            }

            foreach (var entry in _allEntries)
            {
                if (entry.EventId.HasValue &&
                    _highlightIndexByEventId.TryGetValue(entry.EventId.Value, out var index))
                {
                    entry.HighlightIndex = index;
                }
            }
        }

        private void StartMessagePrefetch(IReadOnlyList<LogEntry> entries)
        {
            if (entries.Count == 0)
            {
                return;
            }

            if (_messagePrefetchCts == null)
            {
                _messagePrefetchCts = new CancellationTokenSource();
            }

            var token = _messagePrefetchCts.Token;

            // Process all entries in the background so that the UI populates as the user scrolls
            _ = Task.Run(() => PrefetchMessagesAsync(entries, token), token);
        }

        private void CancelMessagePrefetch()
        {
            if (_messagePrefetchCts == null)
            {
                return;
            }

            _messagePrefetchCts.Cancel();
            _messagePrefetchCts.Dispose();
            _messagePrefetchCts = null;
        }

        private async Task PrefetchMessagesAsync(IReadOnlyList<LogEntry> entries, CancellationToken token)
        {
            try
            {
                await _logProvider.LoadMessagesBatchAsync(entries, RequestFilteredRefreshIfSearchActive, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Prefetch canceled.
            }
        }


        private async Task LoadDetailsForSelectionAsync(LogEntry? entry)
        {
            if (entry == null)
            {
                return;
            }

            if (entry.IsDetailsLoaded)
            {
                return;
            }

            if (entry.IsDetailsLoading)
            {
                if (string.IsNullOrEmpty(entry.Message))
                {
                    entry.Message = "Loading...";
                }
                return;
            }

            entry.IsDetailsLoading = true;
            if (string.IsNullOrEmpty(entry.Message) && !entry.IsPreviewLoaded)
            {
                entry.Message = "Loading...";
            }

            try
            {
                if (!string.IsNullOrEmpty(entry.Message) &&
                    !string.Equals(entry.Message, "Loading...", StringComparison.OrdinalIgnoreCase))
                {
                    var rawData = await _logProvider.GetRawDataAsync(entry);
                    entry.RawData = rawData ?? string.Empty;
                    entry.IsDetailsLoaded = true;
                }
                else
                {
                    var details = await _logProvider.GetDetailsAsync(entry);
                    if (details != null)
                    {
                        entry.Message = details.Message;
                        entry.RawData = details.RawData;
                        entry.MessagePreview = LogEntry.BuildPreview(details.Message);
                        entry.IsPreviewLoaded = true;
                    }
                    else if (entry.Message == "Loading...")
                    {
                        entry.Message = "Details unavailable.";
                        entry.MessagePreview = LogEntry.BuildPreview(entry.Message);
                        entry.IsPreviewLoaded = true;
                    }

                    entry.IsDetailsLoaded = true;
                }
            }
            catch (Exception ex)
            {
                entry.Message = $"Could not retrieve message: {ex.Message}";
                entry.RawData = string.Empty;
                entry.MessagePreview = LogEntry.BuildPreview(entry.Message);
                entry.IsPreviewLoaded = true;
                entry.IsDetailsLoaded = true;
            }
            finally
            {
                entry.IsDetailsLoading = false;
                RequestFilteredRefreshIfSearchActive();
            }
        }

        private sealed class LogEntryTimestampComparer : IComparer<LogEntry>
        {
            public static LogEntryTimestampComparer Instance { get; } = new LogEntryTimestampComparer();

            public int Compare(LogEntry? x, LogEntry? y)
            {
                if (ReferenceEquals(x, y)) return 0;
                if (x is null) return 1;
                if (y is null) return -1;

                var timeCompare = y.Timestamp.CompareTo(x.Timestamp);
                if (timeCompare != 0) return timeCompare;

                var recordCompare = (y.RecordId ?? 0).CompareTo(x.RecordId ?? 0);
                if (recordCompare != 0) return recordCompare;

                return string.Compare(y.LogName, x.LogName, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static List<LogEntry> MergeSortedEntries(IList<LogEntry> existing, List<LogEntry> incoming)
        {
            if (existing.Count == 0)
            {
                return new List<LogEntry>(incoming);
            }

            if (incoming.Count == 0)
            {
                return new List<LogEntry>(existing);
            }

            var merged = new List<LogEntry>(existing.Count + incoming.Count);
            int i = 0;
            int j = 0;

            while (i < existing.Count && j < incoming.Count)
            {
                if (LogEntryTimestampComparer.Instance.Compare(existing[i], incoming[j]) <= 0)
                {
                    merged.Add(existing[i]);
                    i++;
                }
                else
                {
                    merged.Add(incoming[j]);
                    j++;
                }
            }

            for (; i < existing.Count; i++)
            {
                merged.Add(existing[i]);
            }

            for (; j < incoming.Count; j++)
            {
                merged.Add(incoming[j]);
            }

            return merged;
        }
    }
}
