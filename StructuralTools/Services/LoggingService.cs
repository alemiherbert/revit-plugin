using System.IO;

namespace StructuralTools.Services;

/// <summary>
/// Service for logging operations and errors.
/// Writes to a file in the user's AppData folder.
/// </summary>
public static class LoggingService
{
    private static string _logFilePath = string.Empty;
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    /// <summary>
    /// Initializes the logging service.
    /// </summary>
    public static void Initialize()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string logDir = Path.Combine(appDataPath, "StructuralTools", "Logs");
        
        Directory.CreateDirectory(logDir);
        
        string dateStamp = DateTime.Now.ToString("yyyyMMdd");
        _logFilePath = Path.Combine(logDir, $"StructuralTools_{dateStamp}.log");
        
        _writer = new StreamWriter(_logFilePath, append: true)
        {
            AutoFlush = true
        };
        
        Info("=== Logging initialized ===");
        Info($"Log file: {_logFilePath}");
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string message)
    {
        Log("INFO", message);
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public static void Warning(string message)
    {
        Log("WARN", message);
    }

    /// <summary>
    /// Logs an error message.
    /// </summary>
    public static void Error(string message)
    {
        Log("ERROR", message);
    }

    /// <summary>
    /// Logs a debug message (only in debug builds).
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Debug(string message)
    {
        Log("DEBUG", message);
    }

    private static void Log(string level, string message)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _writer.WriteLine($"[{timestamp}] [{level}] {message}");
        }
    }

    /// <summary>
    /// Disposes the logging service.
    /// </summary>
    public static void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    public static string GetLogFilePath()
    {
        return _logFilePath;
    }

    /// <summary>
    /// Exports the current log to a specified location.
    /// </summary>
    public static void ExportLog(string destinationPath)
    {
        if (string.IsNullOrEmpty(_logFilePath) || !File.Exists(_logFilePath))
            return;
            
        File.Copy(_logFilePath, destinationPath, overwrite: true);
    }
}
