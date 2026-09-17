using System;
using System.IO;

namespace Atypik.Core;

/// <summary>
/// Append-only session log - %APPDATA%\Atypik\debug.log.
/// Enabled automatically when the file %APPDATA%\Atypik\debug_enabled exists.
/// Always-on during development; delete the flag file to silence in production.
/// </summary>
public static class DebugLog
{
    private static readonly string _dir  =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atypik");

    private static readonly string _path = Path.Combine(_dir, "debug.log");
    private static readonly string _flag = Path.Combine(_dir, "debug_enabled");

    public static bool Enabled { get; private set; }

    /// <summary>Call once at startup. Creates the flag file if absent (dev convenience).</summary>
    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // Auto-enable: create flag on first run so dev always has a log.
            if (!File.Exists(_flag)) File.WriteAllText(_flag, "1");
            Enabled = true;
            Append("=== session start ===");
        }
        catch { /* never crash on logging init */ }
    }

    public static void Write(string message)
    {
        if (!Enabled) return;
        Append(message);
    }

    public static void Write(string fmt, params object[] args)
        => Write(string.Format(fmt, args));

    private static void Append(string message)
    {
        try
        {
            File.AppendAllText(_path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
