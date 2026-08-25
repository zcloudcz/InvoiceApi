using Microsoft.Extensions.Logging;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that keeps every entry in memory, so a test can assert
/// on what an operator would actually read in the log — not just on the code path taken.
///
/// Junior note: <c>NullLogger</c> throws everything away, which is fine when the log is
/// incidental. It is not fine when a guide promises the operator a specific log line
/// (ADMINGUIDE §5 "v logu to poznáte podle varování ..."); then the log line is behaviour
/// and needs a test like any other.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>Warnings only — the level the fallback and latch messages are written at.</summary>
    public IReadOnlyList<LogEntry> Warnings =>
        _entries.Where(entry => entry.Level == LogLevel.Warning).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
}

/// <summary>One captured log entry.</summary>
/// <param name="Level">Severity it was written at.</param>
/// <param name="Message">Rendered message, placeholders already substituted.</param>
/// <param name="Exception">Attached exception, when the call site passed one.</param>
internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception)
{
    /// <summary>
    /// Everything a log sink would persist for this entry — message plus exception text.
    /// Used to assert that a piece of information is genuinely absent, not merely moved
    /// from the message into the exception.
    /// </summary>
    public string FullText => Exception is null ? Message : $"{Message} {Exception}";
}
