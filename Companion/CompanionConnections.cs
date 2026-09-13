using System.Net.Http.Json;
using System.Text.Json;

namespace FederationCompanion;

public sealed class CompanionConnectionRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Secret { get; set; } = CompanionSecrets.Create();
    public string? LocalPeerId { get; set; }
    public string Url { get; set; } = "";
    public bool Outgoing { get; set; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddDays(1);
    public ReturnShareOffer? Offer { get; set; }
}

public sealed record CompanionInvite(string Id, string Secret, ReturnShareOffer Offer);
public sealed record CompanionReply(string Status, ReturnShareOffer? Offer);

public sealed class CompanionConnections(CompanionState state, HttpClient http)
{
    private readonly SemaphoreSlim _operations = new(1, 1);

    private void Ready()
    {
        if (!PlexRemoteEndpoint.IsPublicHttpsUrl(state.PublicUrl) || string.IsNullOrWhiteSpace(state.ServerBaseUrl) || string.IsNullOrWhiteSpace(state.ServerAccessToken))
            throw new InvalidOperationException("Connect your own Plex and save a public Companion address first.");
    }

    private ReturnShareOffer OwnOffer(CompanionPeer peer)
        => new(state.PublicUrl!.TrimEnd('/'), peer.AccessToken, state.ClientIdentifier, state.ServerName ?? "Plex Companion");

    public async Task InviteAsync(string url, CancellationToken ct)
    {
        Ready();
        if (!PlexRemoteEndpoint.IsPublicHttpsUrl(url)) throw new ArgumentException("Enter their public HTTPS Companion address.");
        url = url.Trim().TrimEnd('/');
        if (string.Equals(url, state.PublicUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose your friend's Companion, not this app.");
        await _operations.WaitAsync(ct);
        try
        {
            CompanionConnectionRequest request;
            lock (state)
            {
                if (state.ImportPeers.Any(p => p.SourceKind == "Plex" && string.Equals(p.Url.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("This Companion is already connected. Manage it under your friends below.");
                request = state.CompanionRequests.FirstOrDefault(r => r.Outgoing && r.Url == url && r.Status == "pending" && r.ExpiresUtc > DateTime.UtcNow)!;
                if (request == null)
                {
                    if (state.CompanionRequests.Count(r => r.Status == "pending") >= 50) throw new InvalidOperationException("Resolve existing friend requests first.");
                    var peer = new CompanionPeer { Name = new Uri(url).Host, CompanionConnection = true, PendingConnection = true };
                    state.Peers.Add(peer);
                    request = new CompanionConnectionRequest { Outgoing = true, Url = url, LocalPeerId = peer.Id, Offer = OwnOffer(peer) };
                    state.CompanionRequests.Add(request);
                }
            }
            await state.SaveAsync();
            using var response = await http.PostAsJsonAsync(url + "/api/companion/requests", new CompanionInvite(request.Id, request.Secret, request.Offer!), ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("The Companion request was not accepted.");
            request.Error = null;
            await state.SaveAsync();
        }
        finally { _operations.Release(); }
    }

    public async Task ReceiveAsync(CompanionInvite invite)
    {
        if (!Guid.TryParse(invite.Id, out _) || !CompanionSecrets.IsValid(invite.Secret)
            || invite.Offer == null || !CompanionSecrets.IsValid(invite.Offer.Token) || !Guid.TryParse(invite.Offer.FederationId, out _)
            || !PlexRemoteEndpoint.IsPublicHttpsUrl(invite.Offer.Url) || invite.Offer.Name?.Length > 160)
            throw new ArgumentException("Invalid Companion request.");
        lock (state)
        {
            var existing = state.CompanionRequests.FirstOrDefault(r => !r.Outgoing && r.Id == invite.Id);
            if (existing != null)
            {
                if (!MediaMount.Authorized("Bearer " + invite.Secret, existing.Secret) || existing.Offer != invite.Offer)
                    throw new InvalidOperationException("Request identity changed.");
                return;
            }
            state.CompanionRequests.RemoveAll(r => !r.Outgoing && r.ExpiresUtc < DateTime.UtcNow && r.Status != "accepted");
            if (state.CompanionRequests.Count(r => !r.Outgoing) >= 50) throw new InvalidOperationException("Too many pending requests.");
            state.CompanionRequests.Add(new CompanionConnectionRequest { Id = invite.Id, Secret = invite.Secret, Url = invite.Offer.Url.TrimEnd('/'), Offer = invite.Offer });
        }
        await state.SaveAsync();
    }

    public async Task AcceptAsync(string id)
    {
        Ready();
        lock (state)
        {
            var request = state.CompanionRequests.FirstOrDefault(r => !r.Outgoing && r.Id == id) ?? throw new ArgumentException("Request not found.");
            if (request.Status == "accepted") return;
            if (request.Status != "pending" || request.ExpiresUtc < DateTime.UtcNow) throw new InvalidOperationException("Request expired or was declined. Ask for a new request.");
            if (request.Offer!.FederationId == state.ClientIdentifier) throw new InvalidOperationException("Cannot connect this app to itself.");
            var peer = new CompanionPeer { Name = request.Offer.Name ?? "Plex Companion", CompanionConnection = true };
            state.Peers.Add(peer);
            var imported = ReturnShareLink.Offer(state, peer, request.Offer);
            imported.SourceKind = "Plex";
            request.LocalPeerId = peer.Id;
            request.Status = "accepted";
        }
        await state.SaveAsync();
    }

    public async Task RejectAsync(string id)
    {
        lock (state)
        {
            var request = state.CompanionRequests.FirstOrDefault(r => !r.Outgoing && r.Id == id && r.Status == "pending") ?? throw new ArgumentException("Pending request not found.");
            request.Status = "declined";
        }
        await state.SaveAsync();
    }

    public async Task CancelAsync(string id)
    {
        lock (state)
        {
            var request = state.CompanionRequests.FirstOrDefault(r => r.Outgoing && r.Id == id && r.Status == "pending") ?? throw new ArgumentException("Pending request not found.");
            state.Peers.RemoveAll(p => p.Id == request.LocalPeerId && p.PendingConnection);
            request.Status = "cancelled";
        }
        await state.SaveAsync();
    }

    public CompanionReply? Result(string id, string secret)
    {
        lock (state)
        {
            var request = state.CompanionRequests.FirstOrDefault(r => !r.Outgoing && r.Id == id);
            if (request == null || !MediaMount.Authorized("Bearer " + secret, request.Secret)) return null;
            var peer = state.Peers.FirstOrDefault(p => p.Id == request.LocalPeerId);
            if (request.Status == "accepted") return peer == null ? new("revoked", null) : new("accepted", OwnOffer(peer));
            return new(request.ExpiresUtc < DateTime.UtcNow ? "expired" : request.Status, null);
        }
    }

    public async Task PollAsync(CancellationToken ct)
    {
        if (!await _operations.WaitAsync(0, ct)) return;
        try
        {
            CompanionConnectionRequest[] pending;
            lock (state) { pending = state.CompanionRequests.Where(r => r.Outgoing && r.Status == "pending").ToArray(); }
            if (pending.Length == 0) return;
            foreach (var request in pending)
            {
                CompanionPeer? peer;
                lock (state)
                {
                    if (request.Status != "pending") continue;
                    peer = state.Peers.FirstOrDefault(p => p.Id == request.LocalPeerId);
                    if (peer == null) { request.Status = "cancelled"; continue; }
                    if (request.ExpiresUtc < DateTime.UtcNow) { request.Status = "expired"; state.Peers.Remove(peer); continue; }
                }
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Get, request.Url + "/api/companion/requests/" + request.Id + "/result");
                    message.Headers.Add("X-Companion-Request", request.Secret);
                    using var response = await http.SendAsync(message, ct);
                    response.EnsureSuccessStatusCode();
                    var reply = await response.Content.ReadFromJsonAsync<CompanionReply>(ct) ?? throw new JsonException();
                    lock (state)
                    {
                        if (!state.Peers.Contains(peer)) continue;
                        if (reply.Status == "accepted")
                        {
                            if (reply.Offer == null || !string.Equals(reply.Offer.Url.TrimEnd('/'), request.Url, StringComparison.OrdinalIgnoreCase))
                                throw new JsonException("Companion source address changed.");
                            var imported = ReturnShareLink.Offer(state, peer, reply.Offer);
                            imported.SourceKind = "Plex";
                            peer.PendingConnection = false; peer.Name = imported.Name;
                            request.Status = "accepted";
                        }
                        else if (reply.Status is "declined" or "expired" or "revoked") { request.Status = reply.Status; state.Peers.Remove(peer); }
                        request.Error = null;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or TaskCanceledException)
                {
                    ct.ThrowIfCancellationRequested();
                    request.Error = "Could not check this request. Companion will retry when your friend is online.";
                }
            }
            await state.SaveAsync();
        }
        finally { _operations.Release(); }
    }
}

public sealed class CompanionConnectionPoller(CompanionConnections connections, ILogger<CompanionConnectionPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await connections.PollAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Companion friend requests could not refresh; retrying on the next check."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
