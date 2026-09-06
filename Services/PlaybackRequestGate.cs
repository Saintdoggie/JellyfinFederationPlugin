using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services;

// Coordinates cache misses only; entries disappear when the last caller leaves.
// Each waiter owns its cancellation, so leaving a player cannot cancel another viewer.
internal sealed class PlaybackRequestGate
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries.Add(key, entry = new Entry());
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            Leave(key, entry, false);
            throw;
        }
    }

    private void Leave(string key, Entry entry, bool acquired)
    {
        lock (_entries)
        {
            if (acquired) entry.Semaphore.Release();
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class Lease(PlaybackRequestGate owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave(key, entry, true);
        }
    }
}
