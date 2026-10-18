using System.Collections.Concurrent;
using LicensePlatform.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace LicensePlatform.Web.Tests;

internal sealed class TestClock : TimeProvider, IClock
{
    private long _utcTicks = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).Ticks;

    public DateTimeOffset UtcNow => GetUtcNow();

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _utcTicks, elapsed.Ticks);
}

internal sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception));
        }
    }
}
