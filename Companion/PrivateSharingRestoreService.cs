using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FederationCompanion;

/// <summary>Rebind only an explicitly enabled, owned private listener after startup.</summary>
public sealed class PrivateSharingRestoreService : BackgroundService
{
    private readonly CompanionState _state;
    private readonly TailscaleNetworkService _network;
    private readonly ILogger<PrivateSharingRestoreService> _logger;

    public PrivateSharingRestoreService(CompanionState state, TailscaleNetworkService network, ILogger<PrivateSharingRestoreService> logger)
        => (_state, _network, _logger) = (state, network, logger);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_state.PrivateSharingPort.HasValue) return;
        for (var attempt = 0; attempt < 120 && !CompanionListen.Port.HasValue; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
        if (!CompanionListen.Port.HasValue) return;
        await _network.StateGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            if (!_state.PrivateSharingPort.HasValue) return;
            var previousUrl = _state.PublicUrl;
            var result = await _network.SetUpPrivateAsync(CompanionListen.Port.Value,
                _state.PrivateSharingPort, _state.PrivateSharingLocalPort, stoppingToken).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogWarning("Private sharing could not be restored. Retry setup from the Companion dashboard.");
                return;
            }
            _state.PublicUrl = result.Url;
            foreach (var peer in _state.ImportPeers.Where(p => string.IsNullOrWhiteSpace(p.PlaybackBaseUrl)
                || string.Equals(p.PlaybackBaseUrl, previousUrl, StringComparison.OrdinalIgnoreCase)))
                peer.PlaybackBaseUrl = result.Url;
            _state.PrivateSharingPort = result.Port;
            _state.PrivateSharingLocalPort = CompanionListen.Port;
            await _state.SaveAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { _logger.LogWarning("Private sharing restore failed. Existing media and peer consent were retained."); }
        finally { _network.StateGate.Release(); }
    }
}
