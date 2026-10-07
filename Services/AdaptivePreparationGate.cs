using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Federation.Services;

/// <summary>Admission for speculative playback: four preparations per receiver, one per user.</summary>
public sealed class AdaptivePreparationGate
{
    private readonly Dictionary<Guid, (Guid User, DateTimeOffset Until)> _leases = new();
    private readonly TimeProvider _clock;
    public AdaptivePreparationGate() : this(TimeProvider.System) { }
    internal AdaptivePreparationGate(TimeProvider clock) => _clock = clock;
    public Guid? Acquire(Guid user)
    {
        lock (_leases)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _leases.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray()) _leases.Remove(key);
            if (_leases.Count >= 4 || _leases.Values.Any(v => v.User == user)) return null;
            var id = Guid.NewGuid();
            _leases[id] = (user, now.AddSeconds(25));
            return id;
        }
    }
    public void Release(Guid user, Guid lease)
    {
        lock (_leases)
        {
            if (_leases.TryGetValue(lease, out var value) && value.User == user) _leases.Remove(lease);
        }
    }
}
