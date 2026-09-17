using System.Collections.Concurrent;

namespace AuctionFlipper.Core;

/// <summary>
/// Interns item ids and seller names to small integers.
///
/// The collectors see tens of thousands of listings a minute and nearly every one repeats an id
/// that has already been seen. Working with an int index instead of a string keeps the hot
/// structures compact, makes dictionary lookups cheap, and lets the on-disk sale log store a
/// 4-byte reference instead of a name.
/// </summary>
public sealed class ItemRegistry
{
    private readonly ConcurrentDictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _names = new(4096);
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _names.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public int GetOrAdd(string name)
    {
        if (_index.TryGetValue(name, out int existing))
            return existing;

        _lock.EnterWriteLock();
        try
        {
            if (_index.TryGetValue(name, out existing))
                return existing;

            int idx = _names.Count;
            _names.Add(name);
            _index[name] = idx;
            return idx;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool TryGetIndex(string name, out int index) => _index.TryGetValue(name, out index);

    public string GetName(int index)
    {
        _lock.EnterReadLock();
        try
        {
            return (uint)index < (uint)_names.Count ? _names[index] : "?";
        }
        finally { _lock.ExitReadLock(); }
    }

    public string[] Snapshot()
    {
        _lock.EnterReadLock();
        try { return _names.ToArray(); }
        finally { _lock.ExitReadLock(); }
    }
}
