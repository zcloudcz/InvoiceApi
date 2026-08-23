using System.Text;
using Fakvio.MigrationTool;
using Microsoft.Extensions.Logging;

namespace Fakvio.Tests.MigrationTool;

/// <summary>
/// Collects everything a component logs so a failing assertion can quote it. The migration tool
/// reports its per-check verdicts through <see cref="ILogger"/> only — with a null logger, a
/// failure in CI says "expected True, was False" and nothing else.
/// </summary>
public sealed class RecordedLog : ILogger<DataIntegrityVerifier>
{
    private readonly StringBuilder _lines = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
            _lines.AppendLine($"[{logLevel}] {formatter(state, exception)}");
    }

    public override string ToString()
    {
        lock (_lines)
            return _lines.ToString();
    }
}
