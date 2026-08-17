using Godot;

namespace UndoSync;

/// <summary>
/// Per-process file logger. Local multi-instance testing shares one Godot
/// user-data directory, so the pid keeps instances from clobbering each other.
/// </summary>
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", $"UndoSync-{System.Environment.ProcessId}.log");

    private static bool _initialized;

    internal static void Write(string message)
    {
        try
        {
            if (!_initialized)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                System.IO.File.WriteAllText(Path,
                    $"=== UndoSync {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={System.Environment.NewLine}");
                _initialized = true;
            }
            System.IO.File.AppendAllText(Path,
                $"[{System.DateTime.Now:HH:mm:ss.fff}] {message}{System.Environment.NewLine}");
        }
        catch
        {
            // logging must never break the game
        }
    }
}
