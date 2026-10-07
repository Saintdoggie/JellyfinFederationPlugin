using System.Text.Json;
using FederationCompanion;

namespace FederationCompanion.Tests;

public class TailscaleNetworkServiceTests
{
    private const string Dns = "companion.tail123456.ts.net";
    private const string Running = "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"companion.tail123456.ts.net.\"}}";
    private static TailscaleCommandResult Ok(string json = "{}") => new(true, json, string.Empty);
    private static string Config(int port, int localPort, bool funnel = false, bool extraHandler = false)
    {
        var handlers = new Dictionary<string, object> { ["/"] = new { Proxy = $"http://127.0.0.1:{localPort}" } };
        if (extraHandler) handlers["/unrelated"] = new { Text = "unrelated" };
        return JsonSerializer.Serialize(new {
            TCP = new Dictionary<string, object> { [port.ToString()] = new { HTTPS = true } },
            Web = new Dictionary<string, object> { [$"{Dns}:{port}"] = new { Handlers = handlers } },
            AllowFunnel = new Dictionary<string, bool> { [$"{Dns}:{port}"] = funnel }
        });
    }

    [Theory]
    [InlineData("Running", true)]
    [InlineData("NeedsLogin", false)]
    [InlineData("Stopped", false)]
    public async Task Status_RequiresRunningBackendEvenWhenCliExitsSuccessfully(string state, bool expected)
    {
        var runner = new Runner(Ok(Running.Replace("Running", state)));
        var status = await new TailscaleNetworkService(runner).CheckAsync(default);
        Assert.Equal(expected, status.SignedIn);
        Assert.Equal(expected ? Dns : null, status.DnsName);
    }

    [Fact]
    public async Task Setup_DiscoversRuntimePortAndAddress_AndPreservesOccupiedPort()
    {
        var runner = new Runner(Ok(Running), Ok(Config(8443, 1234)), Ok(), Ok(Config(10000, 54321)));
        var result = await new TailscaleNetworkService(runner).SetUpPrivateAsync(54321, null, null, default);
        Assert.True(result.Success);
        Assert.Equal($"https://{Dns}:10000", result.Url);
        Assert.Equal(new[] { "serve", "--bg", "--yes", "--https=10000", "http://127.0.0.1:54321" }, runner.Calls[2]);
        Assert.DoesNotContain(runner.Calls.SelectMany(c => c), c => c is "reset" or "funnel");
    }

    [Fact]
    public async Task Setup_CanConvertOnlyOurExistingFunnelToPrivate()
    {
        var runner = new Runner(Ok(Running), Ok(Config(443, 9876, funnel: true)), Ok(), Ok(Config(443, 9876)));
        var result = await new TailscaleNetworkService(runner).SetUpPrivateAsync(9876, null, null, default);
        Assert.True(result.Success);
        Assert.Equal($"https://{Dns}", result.Url);
        Assert.Contains("--https=443", runner.Calls[2]);
    }

    [Fact]
    public void Restart_RecognizesPreviousOwnedTarget_WithoutTakingOtherHandlers()
    {
        Assert.Equal(8443, TailscaleNetworkService.ChoosePrivatePort(Config(8443, 5000), 5001, 8443, 5000));
        Assert.Equal(10000, TailscaleNetworkService.ChoosePrivatePort(Config(8443, 5000, extraHandler: true), 5001, 8443, 5000));
    }

    [Theory]
    [InlineData("bad json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"TCP\":[]}")]
    [InlineData("{\"Foreground\":{\"session\":{}}}")]
    [InlineData("{\"TCP\":{\"invalid\":{}}}")]
    [InlineData("{\"TCP\":{\"8443\":null}}")]
    [InlineData("{\"TCP\":{\"443\":{},\"8443\":{},\"10000\":{}}}")]
    public async Task UnsafeOrOccupiedConfiguration_NeverRunsASetupCommand(string config)
    {
        var runner = new Runner(Ok(Running), Ok(config));
        var result = await new TailscaleNetworkService(runner).SetUpPrivateAsync(5000, null, null, default);
        Assert.False(result.Success);
        Assert.Null(result.Url);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task Setup_DoesNotAdvertiseAStillPublicOrWrongTargetListener()
    {
        foreach (var config in new[] { Config(8443, 5000, funnel: true), Config(8443, 6000), "[]" })
        {
            var runner = new Runner(Ok(Running), Ok(), Ok(), Ok(config));
            var result = await new TailscaleNetworkService(runner).SetUpPrivateAsync(5000, null, null, default);
            Assert.False(result.Success);
            Assert.Null(result.Url);
        }
    }

    [Fact]
    public async Task Stop_CannotRemoveAnUnrelatedOrPublicListener()
    {
        foreach (var config in new[] { Config(8443, 6000), Config(8443, 5000, funnel: true), Config(8443, 5000, extraHandler: true) })
        {
            var runner = new Runner(Ok(Running), Ok(config));
            var result = await new TailscaleNetworkService(runner).StopPrivateAsync(8443, 5000, default);
            Assert.False(result.Success);
            Assert.Equal(2, runner.Calls.Count);
        }
    }

    [Fact]
    public async Task Stop_IsScopedToTheOwnedHttpsPort()
    {
        var runner = new Runner(Ok(Running), Ok(Config(8443, 5000)), Ok(), Ok());
        var result = await new TailscaleNetworkService(runner).StopPrivateAsync(8443, 5000, default);
        Assert.True(result.Success);
        Assert.Equal(new[] { "serve", "--https=8443", "off" }, runner.Calls[2]);
    }

    [Fact]
    public async Task Stop_DoesNotClearStateWhenListenerIsStillPresent()
    {
        var runner = new Runner(Ok(Running), Ok(Config(8443, 5000)), Ok(), Ok(Config(8443, 5000)));
        Assert.False((await new TailscaleNetworkService(runner).StopPrivateAsync(8443, 5000, default)).Success);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"BackendState\":4}")]
    public async Task Status_MalformedOutputDoesNotCrashTheDashboard(string json)
    {
        Assert.False((await new TailscaleNetworkService(new Runner(Ok(json))).CheckAsync(default)).SignedIn);
    }

    [Theory]
    [InlineData("companion.tail123456.ts.net.", "companion.tail123456.ts.net")]
    [InlineData("bad.ts.net --flag", null)]
    [InlineData("localhost", null)]
    [InlineData("https://companion.tail123456.ts.net", null)]
    [InlineData("companion.example.com", null)]
    public void DiscoveredDns_CannotInjectArgumentsOrUseAnUnrelatedDomain(string value, string? expected)
    {
        Assert.Equal(expected, TailscaleHelper.ReadDnsName(JsonSerializer.Serialize(new { Self = new { DNSName = value } })));
    }

    private sealed class Runner(params TailscaleCommandResult[] results) : ITailscaleCommandRunner
    {
        private readonly Queue<TailscaleCommandResult> _results = new(results);
        public List<string[]> Calls { get; } = new();
        public Task<TailscaleCommandResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }
}
