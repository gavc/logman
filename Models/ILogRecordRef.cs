namespace LogMan.Models
{
    /// <summary>
    /// Represents a provider-specific pointer to a log record for lazy loading.
    /// </summary>
    public interface ILogRecordRef
    {
        string SourceIdentifier { get; }
    }
}
