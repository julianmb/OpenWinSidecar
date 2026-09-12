using System;
using System.IO;

namespace OpenWinSidecar.Service;

/// <summary>
/// Persistent log file sink. Subscribes to the console log forwarder and mirrors every
/// line to <c>%LOCALAPPDATA%\OpenWinSidecar\logs\sidecar.log</c> with wall-clock
/// timestamps, rolling at 5MB (keeps one <c>.1</c> generation, ~10MB total). The console
/// stays the primary log; the file exists so freezes and crashes can be diagnosed after
/// the fact, when nobody had a console redirect attached.
/// </summary>
public static class FileLogSink
{
    private static readonly object _lock = new();
    private static bool _enabled;
    private static string _path = DefaultPath();
    private static long _maxBytes = 5L * 1024 * 1024;
    private static int _writesSinceSizeCheck;
    private static StreamWriter? _writer;

    public static string CurrentPath
    {
        get { lock (_lock) return _path; }
    }

    public static void EnsureInitialized()
    {
        lock (_lock)
        {
            if (_enabled) return;
            _enabled = true;
        }
        ConsoleLogForwarder.OnLogLine += WriteLine;
    }

    internal static void ConfigureForTest(string path, long maxBytes)
    {
        lock (_lock)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _path = path;
            _maxBytes = maxBytes;
            _writesSinceSizeCheck = 0;
            _enabled = true;
        }
    }

    public static void WriteLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_lock)
        {
            try
            {
                if (_writer == null) OpenLocked();
                if (_writer == null) return;
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                if (++_writesSinceSizeCheck >= 128)
                {
                    _writesSinceSizeCheck = 0;
                    if (_writer.BaseStream.Length > _maxBytes)
                        RollLocked();
                }
            }
            catch { }
        }
    }

    private static void OpenLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(
                new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true
            };
        }
        catch { _writer = null; }
    }

    private static void RollLocked()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try
        {
            var backup = _path + ".1";
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            if (File.Exists(_path)) File.Move(_path, backup);
        }
        catch { }
        OpenLocked();
    }

    private static string DefaultPath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenWinSidecar", "logs", "sidecar.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "openwinsidecar.log");
        }
    }
}
