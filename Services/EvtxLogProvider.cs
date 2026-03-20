using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using LogMan.Models;

namespace LogMan.Services
{
    [SupportedOSPlatform("windows")]
    public class EvtxLogProvider : ILogProvider
    {
        private const int BatchSize = 1000;

        public string Name => "Windows Event Log";
        public event Action<LogEntry>? NewEntryReceived;
        private readonly List<EventLogWatcher> _watchers = new List<EventLogWatcher>();
        private readonly Dictionary<string, EventLogSession> _sessions = new Dictionary<string, EventLogSession>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Threading.Lock _sessionLock = new();

        private EventLogSession GetSession(string machineName)
        {
            if (string.Equals(machineName, "Local", StringComparison.OrdinalIgnoreCase) || 
                string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                return EventLogSession.GlobalSession;
            }

            lock (_sessionLock)
            {
                if (!_sessions.TryGetValue(machineName, out var session))
                {
                    session = new EventLogSession(machineName);
                    _sessions[machineName] = session;
                }

                return session;
            }
        }

        public async IAsyncEnumerable<List<LogEntry>> LoadFromFileAsync(string filePath)
        {
            List<LogEntry> currentBatch = new List<LogEntry>(BatchSize);
            EventLogReader? reader;

            try
            {
                reader = new EventLogReader(filePath, PathType.FilePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Load Error: {ex}");
                throw new InvalidOperationException($"Could not open '{filePath}': {ex.Message}", ex);
            }

            using (reader)
            {
                while (true)
                {
                    EventRecord? record;
                    try
                    {
                        record = reader.ReadEvent();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Read Error: {ex}");
                        throw new InvalidOperationException($"Error reading '{filePath}': {ex.Message}", ex);
                    }

                    if (record == null)
                    {
                        break;
                    }

                    using (record)
                    {
                        try
                        {
                            currentBatch.Add(MapRecordToEntry(record, filePath, PathType.FilePath, "Local", includeDetails: false, includePreview: false));
                        }
                        catch
                        {
                            currentBatch.Add(new LogEntry
                            {
                                MachineName = "Local",
                                Message = "Error processing record",
                                MessagePreview = "Error processing record",
                                IsPreviewLoaded = true,
                                IsDetailsLoaded = true
                            });
                        }
                    }

                    if (currentBatch.Count >= BatchSize)
                    {
                        yield return currentBatch;
                        currentBatch = new List<LogEntry>(BatchSize);
                        await Task.Yield();
                    }
                }
            }

            if (currentBatch.Count > 0)
            {
                yield return currentBatch;
            }
        }

        public async Task<string?> GetMessageAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.DetailsRef is not EvtxRecordRef recordRef)
            {
                return null;
            }

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var session = GetSession(entry.MachineName);
                    using var reader = new EventLogReader(new EventLogQuery(recordRef.Path, recordRef.PathType) { Session = session });
                    reader.Seek(recordRef.Bookmark);
                    using var record = reader.ReadEvent();
                    if (record == null)
                    {
                        return "Record not found.";
                    }

                    return TryGetMessage(record);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Preview Error: {ex}");
                    return "Could not retrieve message.";
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string?> GetRawDataAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.DetailsRef is not EvtxRecordRef recordRef)
            {
                return null;
            }

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var session = GetSession(entry.MachineName);
                    using var reader = new EventLogReader(new EventLogQuery(recordRef.Path, recordRef.PathType) { Session = session });
                    reader.Seek(recordRef.Bookmark);
                    using var record = reader.ReadEvent();
                    if (record == null)
                    {
                        return string.Empty;
                    }

                    return TryGetRawData(record);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Raw Data Error: {ex}");
                    return string.Empty;
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<LogEntryDetails?> GetDetailsAsync(LogEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.DetailsRef is not EvtxRecordRef recordRef)
            {
                return null;
            }

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var session = GetSession(entry.MachineName);
                    using var reader = new EventLogReader(new EventLogQuery(recordRef.Path, recordRef.PathType) { Session = session });
                    reader.Seek(recordRef.Bookmark);
                    using var record = reader.ReadEvent();
                    if (record == null)
                    {
                        return new LogEntryDetails("Record not found.", string.Empty);
                    }

                    var message = TryGetMessage(record);
                    var rawData = TryGetRawData(record);
                    return new LogEntryDetails(message, rawData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Details Error: {ex}");
                    return new LogEntryDetails("Could not retrieve message.", string.Empty);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task LoadMessagesBatchAsync(IEnumerable<LogEntry> entries, Action? entriesUpdated = null, CancellationToken cancellationToken = default)
        {
            const int UpdateNotificationBatchSize = 50;

            var entriesBySource = entries
                .Where(e => !e.IsPreviewLoaded && !e.IsPreviewLoading && !e.IsDetailsLoaded && !e.IsDetailsLoading)
                .GroupBy(e => (e.MachineName, Ref: e.DetailsRef as EvtxRecordRef))
                .Where(g => g.Key.Ref != null)
                .GroupBy(g => (g.Key.MachineName, g.Key.Ref!.Path, g.Key.Ref.PathType))
                .ToList();

            foreach (var group in entriesBySource)
            {
                if (cancellationToken.IsCancellationRequested) break;

                await Task.Run(async () =>
                {
                    try
                    {
                        var updatedEntries = 0;
                        var session = GetSession(group.Key.MachineName);
                        var query = new EventLogQuery(group.Key.Path, group.Key.PathType) { Session = session };
                        using var reader = new EventLogReader(query);

                        foreach (var sourceGroup in group)
                        {
                            foreach (var entry in sourceGroup)
                            {
                                if (cancellationToken.IsCancellationRequested) break;

                                if (entry.DetailsRef is EvtxRecordRef entryRef)
                                {
                                    try
                                    {
                                        entry.IsPreviewLoading = true;
                                        reader.Seek(entryRef.Bookmark);
                                        using var record = reader.ReadEvent();
                                        if (record != null)
                                        {
                                            var message = TryGetMessage(record);
                                            entry.MessagePreview = LogEntry.BuildPreview(message);
                                            entry.IsPreviewLoaded = true;
                                            updatedEntries++;
                                            if (updatedEntries >= UpdateNotificationBatchSize)
                                            {
                                                updatedEntries = 0;
                                                entriesUpdated?.Invoke();
                                            }
                                        }
                                    }
                                    catch { }
                                    finally { entry.IsPreviewLoading = false; }
                                }
                            }
                        }

                        if (updatedEntries > 0)
                        {
                            entriesUpdated?.Invoke();
                        }
                    }
                    catch { }
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        public void StartLiveWatching(IEnumerable<(string machineName, string logName)> logSources)
        {
            StopLiveWatching();

            foreach (var (machineName, logName) in logSources)
            {
                try
                {
                    var session = GetSession(machineName);
                    var query = new EventLogQuery(logName, PathType.LogName) { Session = session };

                    EventBookmark? lastEventBookmark = null;
                    using (var reader = new EventLogReader(query))
                    {
                        reader.Seek(System.IO.SeekOrigin.End, 0);
                        using (var lastRecord = reader.ReadEvent())
                        {
                            if (lastRecord != null)
                            {
                                lastEventBookmark = lastRecord.Bookmark;
                            }
                        }
                    }

                    var watcher = lastEventBookmark != null 
                        ? new EventLogWatcher(query, lastEventBookmark, readExistingEvents: false)
                        : new EventLogWatcher(query);

                    watcher.EventRecordWritten += (s, e) =>
                    {
                        if (e.EventRecord != null)
                        {
                            using (e.EventRecord)
                            {
                                try
                                {
                                    var entry = MapRecordToEntry(e.EventRecord, logName, PathType.LogName, machineName, includeDetails: false, includePreview: true);
                                    NewEntryReceived?.Invoke(entry);
                                }
                                catch (Exception ex)
                                {
                                    NewEntryReceived?.Invoke(new LogEntry
                                    {
                                        MachineName = machineName,
                                        LogName = logName,
                                        Message = $"Watcher processing error: {ex.Message}",
                                        Level = "Error",
                                        LogLevel = 2
                                    });
                                }
                            }
                        }
                    };
                    watcher.Enabled = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Watcher Error ({machineName}/{logName}): {ex}");
                    
                    // Notify UI of startup failures
                    NewEntryReceived?.Invoke(new LogEntry
                    {
                        Timestamp = DateTime.Now,
                        MachineName = machineName,
                        LogName = logName,
                        Source = "LogMan",
                        Level = "Error",
                        LogLevel = 2,
                        Message = $"Failed to start watcher: {ex.Message} (Check if running as Admin)",
                        MessagePreview = "Watcher Startup Error"
                    });
                }
            }
        }

        public void StopLiveWatching()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Enabled = false;
                    watcher.Dispose();
                }
                catch { }
            }
            _watchers.Clear();

            foreach (var session in _sessions.Values)
            {
                if (session != EventLogSession.GlobalSession)
                {
                    session.Dispose();
                }
            }
            _sessions.Clear();
        }

        private LogEntry MapRecordToEntry(EventRecord record, string path, PathType pathType, string machineName, bool includeDetails, bool includePreview = false)
        {
            var shouldLoadPreview = includeDetails || includePreview;
            var message = shouldLoadPreview ? TryGetMessage(record) : string.Empty;
            var rawData = includeDetails ? TryGetRawData(record) : string.Empty;

            var entry = new LogEntry
            {
                Timestamp = record.TimeCreated ?? DateTime.Now,
                MachineName = machineName,
                LogName = record.LogName ?? "Unknown",
                Source = record.ProviderName ?? "Unknown",
                Level = record.LevelDisplayName ?? record.Level.ToString() ?? "Unknown",
                LogLevel = record.Level ?? 0,
                EventId = record.Id,
                RecordId = record.RecordId,
                Category = record.TaskDisplayName ?? record.Task.ToString() ?? "Unknown",
                Message = includeDetails ? message : string.Empty,
                RawData = rawData,
                MessagePreview = shouldLoadPreview ? LogEntry.BuildPreview(message) : LogEntry.LazyMessagePlaceholder,
                IsPreviewLoaded = shouldLoadPreview,
                IsDetailsLoaded = includeDetails
            };

            var bookmark = record.Bookmark;
            if (bookmark != null)
            {
                entry.DetailsRef = new EvtxRecordRef(path, pathType, bookmark);
            }
            else if (!includeDetails)
            {
                entry.Message = "Details unavailable.";
                entry.MessagePreview = LogEntry.BuildPreview(entry.Message);
                entry.IsPreviewLoaded = true;
                entry.IsDetailsLoaded = true;
            }

            return entry;
        }

        private string TryGetMessage(EventRecord record)
        {
            try
            {
                return record.FormatDescription();
            }
            catch (EventLogException ex)
            {
                return $"Error retrieving message (Code: {ex.HResult}). {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Could not retrieve message: {ex.Message}";
            }
        }

        private string TryGetRawData(EventRecord record)
        {
            try
            {
                var xml = record.ToXml();
                if (string.IsNullOrWhiteSpace(xml)) return string.Empty;

                try
                {
                    var doc = System.Xml.Linq.XDocument.Parse(xml);
                    return doc.ToString(); // Returns indented XML by default
                }
                catch
                {
                    return xml; // Fallback to raw if parsing fails
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"XML Extraction Error: {ex}");
                return string.Empty;
            }
        }

        private sealed class EvtxRecordRef : ILogRecordRef
        {
            public EvtxRecordRef(string path, PathType pathType, EventBookmark bookmark)
            {
                Path = path;
                PathType = pathType;
                Bookmark = bookmark;
            }

            public string Path { get; }
            public PathType PathType { get; }
            public EventBookmark Bookmark { get; }

            public string SourceIdentifier => $"{PathType}:{Path}";
        }
    }
}
