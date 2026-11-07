namespace OpenHomeRadio.Sdr;

/// <summary>
/// Remembers keys for a limited time, to collapse the repeated copies of one transmission (which may span several
/// receive buffers) into a single event. Not thread-safe; each receive enumeration owns one.
/// </summary>
internal sealed class RecentKeys<TKey>(TimeSpan window, TimeProvider timeProvider) where TKey : notnull
{
    private readonly Dictionary<TKey, long> _seenAt = [];

    /// <summary>Returns true, and records the key, unless the same key was recorded within the window.</summary>
    public bool TryAdd(TKey key)
    {
        var now = timeProvider.GetTimestamp();
        foreach (var stale in _seenAt.Where(e => timeProvider.GetElapsedTime(e.Value, now) >= window).Select(e => e.Key).ToList())
            _seenAt.Remove(stale);
        return _seenAt.TryAdd(key, now);
    }
}
