using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Atypik.Core;

/// <summary>
/// Runtime module registry.
/// Modules register themselves; the pipeline queries by capability.
///
/// Thread-safe - modules may register from any thread during startup.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly ConcurrentDictionary<string, ITextProcessor> _modules = new();

    // -- Registration ----------------------------------------------------------

    public ModuleRegistry Register(ITextProcessor module)
    {
        if (!_modules.TryAdd(module.Name, module))
            throw new InvalidOperationException(
                $"Module '{module.Name}' is already registered. Use Replace() to override.");
        return this;
    }

    public ModuleRegistry Replace(ITextProcessor module)
    {
        _modules[module.Name] = module;
        return this;
    }

    public bool Unregister(string name)
        => _modules.TryRemove(name, out _);

    // -- Queries ---------------------------------------------------------------

    /// <summary>
    /// Returns all available modules matching the given capability mask,
    /// ordered by priority (ascending - lower = runs first).
    /// </summary>
    public IReadOnlyList<ITextProcessor> GetByCapability(ProcessorCapability mask)
        => _modules.Values
            .Where(m => m.IsAvailable && (m.Capabilities & mask) != 0)
            .OrderBy(m => m.Priority)
            .ToList();

    /// <summary>
    /// Returns all available modules in pipeline order.
    /// </summary>
    public IReadOnlyList<ITextProcessor> GetAll()
        => _modules.Values
            .Where(m => m.IsAvailable)
            .OrderBy(m => m.Priority)
            .ToList();

    public bool TryGet(string name, out ITextProcessor? module)
        => _modules.TryGetValue(name, out module);

    /// <summary>
    /// Returns the first registered module of type <typeparamref name="T"/>,
    /// regardless of whether it is currently available.
    /// </summary>
    public T? GetFirst<T>() where T : class, ITextProcessor
        => _modules.Values.OfType<T>().FirstOrDefault();

    // -- Diagnostics -----------------------------------------------------------

    public IEnumerable<(string Name, bool Available, ProcessorCapability Caps)> Diagnostics()
        => _modules.Values.Select(m => (m.Name, m.IsAvailable, m.Capabilities));
}
