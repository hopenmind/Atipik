using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Atypik.Core;

namespace Atypik.LLM;

/// <summary>
/// Explicit loader for the native bridge (atypik_llm.dll). Default P/Invoke
/// probing fails in packaged builds on some machines:
///  - the single-file self-extract puts the DLL under a temp folder that can sit
///    behind a reparse point the loader refuses to traverse (Win32 0x800701C0,
///    "untrusted mount point"), and
///  - a folder install under %LOCALAPPDATA%\Programs can hit the same wall, or
///    the DLL is simply not found.
///
/// This resolver tries several concrete locations, resolves each to its FINAL
/// path (collapsing any junction in a parent folder, which is what the loader
/// balks at), copies the DLL into a known-writable data dir as a last resort,
/// and logs every attempt so a failing machine's debug.log tells us exactly what
/// happened. It is a no-op for any library other than atypik_llm.
/// </summary>
internal static class NativeLoader
{
    private const string LibName = "atypik_llm";
    private static bool _registered;

    /// <summary>Install the resolver. Safe to call more than once.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
            DebugLog.Write("NativeLoader: resolver registered (base={0})", AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            DebugLog.Write("NativeLoader: register failed - {0}", ex.Message);
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsOurLib(libraryName)) return IntPtr.Zero;   // not ours: default probing

        foreach (string candidate in CandidatePaths())
        {
            IntPtr h = TryLoadResolved(candidate);
            if (h != IntPtr.Zero) return h;
        }

        // Last resort: copy the first DLL we can find into the data dir (a fresh
        // file under a path we control) and load it from there.
        try
        {
            foreach (string candidate in CandidatePaths())
            {
                if (!File.Exists(candidate)) continue;
                Directory.CreateDirectory(AppPrefs.DataDir);
                string safe = Path.Combine(AppPrefs.DataDir, LibName + ".dll");
                File.Copy(candidate, safe, overwrite: true);
                DebugLog.Write("NativeLoader: copied {0} -> {1}", candidate, safe);
                IntPtr h = TryLoadResolved(safe);
                if (h != IntPtr.Zero) return h;
                break;   // one source is enough
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("NativeLoader: copy fallback failed - {0}", ex.Message);
        }

        DebugLog.Write("NativeLoader: all candidates failed for {0}", libraryName);
        return IntPtr.Zero;   // runtime falls back to default probing (then throws)
    }

    private static bool IsOurLib(string name)
        => name.Equals(LibName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(LibName + ".dll", StringComparison.OrdinalIgnoreCase);

    private static IntPtr TryLoadResolved(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                DebugLog.Write("NativeLoader: missing {0}", path);
                return IntPtr.Zero;
            }
            string full = FinalPath(path);
            if (NativeLibrary.TryLoad(full, out IntPtr handle))
            {
                DebugLog.Write("NativeLoader: loaded {0}", full);
                return handle;
            }
            DebugLog.Write("NativeLoader: TryLoad returned false for {0}", full);
        }
        catch (Exception ex)
        {
            DebugLog.Write("NativeLoader: load error {0} - {1}", path, ex.Message);
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // 1. Next to the running assembly (also the single-file extraction dir).
        yield return Path.Combine(AppContext.BaseDirectory, LibName + ".dll");

        // 2. Next to the real executable (folder install).
        string? exe = null;
        try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { /* ignore */ }
        if (!string.IsNullOrEmpty(exe))
        {
            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir))
                yield return Path.Combine(dir, LibName + ".dll");
        }

        // 3. App data dir (same place as the model; known readable/writable).
        yield return Path.Combine(AppPrefs.DataDir, LibName + ".dll");
    }

    // Resolve a path to its final target, collapsing junctions/symlinks in any
    // parent folder. The untrusted-mount-point wall (0x800701C0) is about
    // traversing a reparse point, so loading the collapsed path avoids it.
    private static string FinalPath(string path)
    {
        try
        {
            using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sb = new StringBuilder(1024);
            int len = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
            if (len > 0 && len < sb.Capacity)
            {
                string p = sb.ToString();
                if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p.Substring(4);
                return p;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("NativeLoader: FinalPath failed {0} - {1}", path, ex.Message);
        }
        return Path.GetFullPath(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(
        SafeFileHandle hFile, StringBuilder lpszFilePath, int cchFilePath, int dwFlags);
}
