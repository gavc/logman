using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using LogMan.Models;

namespace LogMan.Services
{
    [SupportedOSPlatform("windows")]
    public interface ILogProvider
    {
        string Name { get; }
        IAsyncEnumerable<List<LogEntry>> LoadFromFileAsync(string filePath);
        Task<string?> GetMessageAsync(LogEntry entry, CancellationToken cancellationToken = default);
        Task<string?> GetRawDataAsync(LogEntry entry, CancellationToken cancellationToken = default);
        Task<LogEntryDetails?> GetDetailsAsync(LogEntry entry, CancellationToken cancellationToken = default);
        Task LoadMessagesBatchAsync(IEnumerable<LogEntry> entries, Action? entriesUpdated = null, CancellationToken cancellationToken = default);
        void StartLiveWatching(IEnumerable<(string machineName, string logName)> logSources);
        void StopLiveWatching();
        event Action<LogEntry> NewEntryReceived;
    }
}
