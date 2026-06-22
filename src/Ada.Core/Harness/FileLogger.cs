using Microsoft.Extensions.Logging;

namespace Ada.Core;

/// <summary>
/// A minimal append-to-file <see cref="ILogger"/> so Ada keeps a central log under <c>%APPDATA%\Ada\logs</c>:
/// errors and warnings by default, and the full Voxa voice pipeline at <c>ADA_LOG=Debug</c>. The tray app has
/// no console, so without this, server logs and crashes vanish. Paired with the tray app's global exception
/// handlers (which call <see cref="Append"/>) so fatal crashes land in the same file.
/// </summary>
public sealed class FileLoggerProvider(string path, LogLevel minLevel) : ILoggerProvider
{
    private static readonly object Gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, path, minLevel);
    public void Dispose() { }

    /// <summary>Append a raw line to <paramref name="path"/> (thread-safe). Used by the crash handlers.</summary>
    public static void Append(string path, string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
    }

    private sealed class FileLogger(string category, string path, LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= min && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} {Tag(level)} {category} — {formatter(state, exception)}"
                     + (exception is null ? "" : Environment.NewLine + exception);
            Append(path, line);
        }

        private static string Tag(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "WRN", LogLevel.Error => "ERR", LogLevel.Critical => "CRT", _ => "???",
        };
    }
}
