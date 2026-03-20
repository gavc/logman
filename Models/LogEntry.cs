using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LogMan.Models
{
    public class LogEntry : INotifyPropertyChanged
    {
        public static readonly string LazyMessagePlaceholder = "Loading...";

        public DateTime Timestamp { get; set; }
        public string MachineName { get; set; } = "Local";
        public string LogName { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public int LogLevel { get; set; } // 1: Critical, 2: Error, 3: Warning, 4: Info, 0: Unknown
        public int? EventId { get; set; }
        public long? RecordId { get; set; }
        public string Category { get; set; } = string.Empty;

        public string Message
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public string MessagePreview
        {
            get;
            set => SetProperty(ref field, value);
        } = LazyMessagePlaceholder;

        public string RawData
        {
            get;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public int? HighlightIndex
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsPreviewLoaded { get; set; }
        public bool IsPreviewLoading { get; set; }
        public bool IsDetailsLoaded { get; set; }
        public bool IsDetailsLoading { get; set; }
        public ILogRecordRef? DetailsRef { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public static string BuildPreview(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            ReadOnlySpan<char> span = message.AsSpan();
            var lineEnd = span.IndexOfAny('\r', '\n');
            if (lineEnd < 0)
            {
                return message;
            }

            return new string(span[..lineEnd]);
        }
    }

    public sealed class LogEntryDetails
    {
        public LogEntryDetails(string message, string rawData)
        {
            Message = message;
            RawData = rawData;
        }

        public string Message { get; }
        public string RawData { get; }
    }
}
