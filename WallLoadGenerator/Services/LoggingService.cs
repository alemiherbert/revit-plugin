using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WallLoadGenerator;

/// <summary>
/// Centralized logging service for the add-in.
/// Logs to file and optionally to Revit journal.
/// </summary>
public static class LoggingService
{
    private static string? _logFilePath;
    private static StreamWriter? _writer;
    private static readonly object _lock = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        try
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string logDir = Path.Combine(appDataPath, "WallLoadGenerator", "Logs");
            
            Directory.CreateDirectory(logDir);
            
            string dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
            _logFilePath = Path.Combine(logDir, $"WallLoadGenerator_{dateStamp}.log");
            
            _writer = new StreamWriter(_logFilePath, append: true)
            {
                AutoFlush = true
            };
            
            _initialized = true;
            Info("=== Wall Load Generator Started ===");
            Info($"Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            Info($"Log file: {_logFilePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize logging: {ex.Message}");
        }
    }

    public static void Dispose()
    {
        lock (_lock)
        {
            _writer?.WriteLine("=== Wall Load Generator Stopped ===");
            _writer?.Close();
            _writer?.Dispose();
            _initialized = false;
        }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warning(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message) => Log("DEBUG", message);

    private static void Log(string level, string message)
    {
        if (!_initialized) return;

        lock (_lock)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] [{level}] {message}";
            
            _writer?.WriteLine(logEntry);
            
            // Also write to debug output for development
            Debug.WriteLine(logEntry);
        }
    }

    public static string GetLogFilePath() => _logFilePath ?? "Not initialized";
}
