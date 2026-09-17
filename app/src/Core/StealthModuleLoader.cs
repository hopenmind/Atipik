using System;
using System.IO;
using System.Reflection;

namespace Atypik.Core;

/// <summary>
/// Loads optional capability modules from external assemblies at runtime.
///
/// Modules that perform sensitive operations (browser detection, port scanning,
/// hardware fingerprinting) are NOT compiled into the main executable.
/// They live as separate DLLs that are:
///   - Absent by default (no install, no detection surface)
///   - Loaded on explicit user request only
///   - Isolated: if the DLL is missing, the capability is silently absent
///
/// This is the correct 2028-resilience pattern:
///   a tool that CAN'T do something is undetectable for doing it.
/// </summary>
public sealed class StealthModuleLoader
{
    private readonly ModuleRegistry _registry;
    private readonly string         _modulesDir;

    public StealthModuleLoader(ModuleRegistry registry, string modulesDir = "modules")
    {
        _registry   = registry;
        _modulesDir = modulesDir;
    }

    /// <summary>
    /// Attempt to load a named capability module from an external DLL.
    /// Silent no-op if the DLL is absent - no exception, no log entry.
    /// </summary>
    public bool TryLoad(string moduleName)
    {
        string dllPath = Path.Combine(_modulesDir, $"atypik.{moduleName}.dll");
        if (!File.Exists(dllPath)) return false;

        try
        {
            var asm   = Assembly.LoadFrom(dllPath);
            var types = asm.GetExportedTypes();

            foreach (var type in types)
            {
                if (!typeof(ITextProcessor).IsAssignableFrom(type)) continue;
                if (type.IsAbstract || type.IsInterface)            continue;

                var instance = (ITextProcessor?)Activator.CreateInstance(type);
                if (instance is null) continue;

                _registry.Replace(instance);  // Replace allows hot-swap
            }
            return true;
        }
        catch
        {
            // Any load failure = treat as absent
            return false;
        }
    }
}

/*
 * MODULE ISOLATION PRINCIPLE
 * --------------------------
 * Sensitive future capabilities ship as SEPARATE optional DLLs:
 *
 *   atypik.hwid.dll        - hardware identity fingerprinting
 *   atypik.browserctx.dll  - active browser context detection (HWND by title/class)
 *   atypik.portmap.dll     - local port mapping (for multi-instance coordination)
 *
 * None of these are present in a default install.
 * A user who doesn't need them has zero detection surface.
 * A user who installs them accepts the tradeoff knowingly.
 *
 * By 2028, kernel-mode attestation will flag anything that ENUMERATES.
 * The safest enumeration is the one that never happens.
 */
