using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SendIt.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Security
}

public record LogEntry(DateTime Timestamp, LogLevel Level, string Category, string Message, Exception? Exception = null);

public static class AppLogger
{
    private static readonly ConcurrentQueue<LogEntry> _history = new();
    private const int MaxHistory = 1000;
    private static readonly object _fileLock = new();

    public static string LogFilePath { get; }
    public static string CrashDumpPath { get; }

    public static event Action<LogEntry>? LogEmitted;

    static AppLogger()
    {
        string baseDir;
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            baseDir = Path.Combine(appData, "SendIt", "logs");
            Directory.CreateDirectory(baseDir);
        }
        catch
        {
            baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            try { Directory.CreateDirectory(baseDir); } catch { }
        }

        LogFilePath = Path.Combine(baseDir, "sendit.log");
        CrashDumpPath = Path.Combine(baseDir, "crash_dump.log");

        // Log session start banner
        WriteRawToFile($"\n{"=".PadRight(80, '=')}\n  SENDIT LOG SESSION STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (Process ID: {Environment.ProcessId})\n{"=".PadRight(80, '=')}");

        // Route Debug and Trace into AppLogger
        Trace.Listeners.Add(new CoreTraceListener());
    }

    public static void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public static void Success(string category, string message) => Log(LogLevel.Success, category, message);
    public static void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    public static void Error(string category, string message, Exception? ex = null) => Log(LogLevel.Error, category, message, ex);
    public static void Security(string category, string message, Exception? ex = null) => Log(LogLevel.Security, category, message, ex);

    public static void Log(LogLevel level, string category, string message, Exception? ex = null)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message, ex);
        _history.Enqueue(entry);

        while (_history.Count > MaxHistory && _history.TryDequeue(out _)) { }

        // Write line to log file synchronously with lock to ensure preservation on abrupt crashes
        string levelStr = level switch
        {
            LogLevel.Success => "SUCCESS ",
            LogLevel.Warning => "WARN    ",
            LogLevel.Error => "ERROR   ",
            LogLevel.Security => "SECURITY",
            _ => "INFO    "
        };

        var sb = new StringBuilder();
        sb.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{levelStr}] [{category,-10}] {message}");
        if (ex != null)
        {
            sb.AppendLine();
            sb.Append($"    Exception: {ex.GetType().FullName}: {ex.Message}\n    StackTrace: {ex.StackTrace}");
            if (ex.InnerException != null)
            {
                sb.AppendLine();
                sb.Append($"    InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}\n    InnerStackTrace: {ex.InnerException.StackTrace}");
            }
        }

        WriteRawToFile(sb.ToString());

        try
        {
            LogEmitted?.Invoke(entry);
        }
        catch { }
    }

    public static void WriteCrashDump(Exception ex, string context)
    {
        var dump = new StringBuilder();
        dump.AppendLine("\n" + "!".PadRight(80, '!'));
        dump.AppendLine($"  CRITICAL APPLICATION CRASH DUMP: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        dump.AppendLine($"  Context: {context}");
        dump.AppendLine($"  Process ID: {Environment.ProcessId} | Thread: {Environment.CurrentManagedThreadId}");
        dump.AppendLine($"  OS: {Environment.OSVersion} | 64-bit OS: {Environment.Is64BitOperatingSystem}");
        dump.AppendLine($"  Runtime: .NET {Environment.Version}");
        dump.AppendLine("-".PadRight(80, '-'));
        dump.AppendLine($"Exception: {ex.GetType().FullName}");
        dump.AppendLine($"Message:   {ex.Message}");
        dump.AppendLine($"HResult:   0x{ex.HResult:X8}");
        dump.AppendLine($"Source:    {ex.Source}");
        dump.AppendLine("\nStack Trace:");
        dump.AppendLine(ex.StackTrace ?? "  (No stack trace available)");

        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null)
        {
            dump.AppendLine($"\n[Inner Exception #{depth}] {inner.GetType().FullName}: {inner.Message}");
            dump.AppendLine(inner.StackTrace ?? "  (No inner stack trace)");
            inner = inner.InnerException;
            depth++;
        }

        dump.AppendLine("!".PadRight(80, '!') + "\n");
        string dumpStr = dump.ToString();

        // Write to both crash_dump.log and main sendit.log
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(CrashDumpPath, dumpStr, Encoding.UTF8);
            }
        }
        catch { }

        WriteRawToFile(dumpStr);
    }

    private static void WriteRawToFile(string line)
    {
        try
        {
            lock (_fileLock)
            {
                using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
                writer.Flush();
            }
        }
        catch
        {
            // Do not crash the application if logging to disk fails
        }
    }

    public static IReadOnlyList<LogEntry> GetHistory() => _history.ToArray();

    private class CoreTraceListener : TraceListener
    {
        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                var level = LogLevel.Info;
                if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("exception", StringComparison.OrdinalIgnoreCase))
                {
                    level = LogLevel.Error;
                }
                else if (message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    level = LogLevel.Warning;
                }

                Log(level, "Trace", message);
            }
        }
    }
}
