using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FederationCompanion;

namespace FederationCompanion.Tests;

public sealed class RcloneBootstrapperTests
{
    [Fact]
    public void DownloadUrl_UsesPinnedVersionAndCurrentArchive()
    {
        Assert.Equal("1.75.1", RcloneBootstrapper.Version);
        Assert.StartsWith("https://downloads.rclone.org/v1.75.1/rclone-v1.75.1-", RcloneBootstrapper.DownloadUrl());
        Assert.EndsWith(".zip", RcloneBootstrapper.DownloadUrl());
        Assert.True(RcloneBootstrapper.ArchiveSha256.ContainsKey(RcloneBootstrapper.ArchiveOsArch()));
        Assert.All(RcloneBootstrapper.ArchiveSha256.Values, sha => Assert.Equal(64, sha.Length));
        Assert.True(RcloneBootstrapper.MaxBinaryBytes > 85_192_704);
    }

    [Theory]
    [InlineData("rclone-v1.75.1-windows-amd64/rclone.exe")]
    [InlineData("rclone")]
    [InlineData(@"nested\rclone.exe")]
    public void FindBinaryEntryName_AcceptsRcloneBinary(string name)
        => Assert.Equal(name, RcloneBootstrapper.FindBinaryEntryName(new[] { "README.txt", name, "git-log.txt" }));

    [Fact]
    public void FindBinaryEntryName_RejectsZipSlipAndOtherFiles()
    {
        Assert.Null(RcloneBootstrapper.FindBinaryEntryName(new[] { "../rclone.exe", "docs/rclone.md", "rclone.dll" }));
        Assert.Null(RcloneBootstrapper.FindBinaryEntryName(new[] { "rclone-v1.75.1-windows-amd64/../evil.exe" }));
    }

    [Fact]
    public async Task EnsureAsync_DownloadsVerifiesAndExtractsOnce()
    {
        var calls = 0;
        var payload = "federation-rclone"u8.ToArray();
        var zip = ZipWith("rclone-v1.75.1-test/rclone.exe", payload);
        var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "fed-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var http = new HttpClient(new Handler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            }));
            var bootstrapper = new RcloneBootstrapper(http, dir, new Dictionary<string, string>
            {
                [RcloneBootstrapper.ArchiveOsArch()] = sha
            }, minimumBinaryBytes: 4);

            var first = await bootstrapper.EnsureAsync(CancellationToken.None);
            Assert.True(first.Success, first.Message);
            Assert.Equal(1, calls);
            Assert.True(File.Exists(first.Executable));
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dir, RcloneBootstrapper.BinaryName())));

            var second = await bootstrapper.EnsureAsync(CancellationToken.None);
            Assert.True(second.Success);
            Assert.Equal(1, calls);
            Assert.Equal(first.Executable, second.Executable);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task EnsureAsync_RejectsWrongChecksum()
    {
        var zip = ZipWith("rclone", "nope"u8.ToArray());
        var dir = Path.Combine(Path.GetTempPath(), "fed-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
            var bootstrapper = new RcloneBootstrapper(http, dir, new Dictionary<string, string>
            {
                [RcloneBootstrapper.ArchiveOsArch()] = new string('a', 64)
            }, minimumBinaryBytes: 1);
            var result = await bootstrapper.EnsureAsync(CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("corrupted", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(bootstrapper.FindExisting());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task EnsureAsync_IgnoresZipSlipEntries()
    {
        var zip = ZipWith("../rclone.exe", "evil"u8.ToArray());
        var dir = Path.Combine(Path.GetTempPath(), "fed-rclone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
            var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) }));
            var bootstrapper = new RcloneBootstrapper(http, dir, new Dictionary<string, string>
            {
                [RcloneBootstrapper.ArchiveOsArch()] = sha
            }, minimumBinaryBytes: 1);
            var result = await bootstrapper.EnsureAsync(CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("incomplete", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClassifyFailure_HidesRawRcloneTextAndPointsAtTheDriver()
    {
        var message = FilesystemDriver.ClassifyFailure("Cannot find WinFsp, please install it from http://www.secfs.net/winfsp/");
        Assert.DoesNotContain("secfs.net", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("media driver", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rclone", FilesystemDriver.MissingMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_UsesResolvedHelperPath()
    {
        var start = LocalMediaMountService.CreateStartInfo("/tmp/rclone", "/tmp/media-mount.conf", "/tmp/plex-media");
        Assert.Equal("/tmp/rclone", start.FileName);
        Assert.Contains("mount", start.ArgumentList);
        Assert.Contains("--read-only", start.ArgumentList);
    }

    [Fact]
    public void ShouldManageMount_RequiresSavedOwnerOptIn()
    {
        Assert.False(LocalMediaMountService.ShouldManageMount(new CompanionState()));
        Assert.True(LocalMediaMountService.ShouldManageMount(new CompanionState { AutoStartMediaMount = true, MediaMountRoot = "/mnt" }));
        Assert.False(LocalMediaMountService.ShouldManageMount(new CompanionState { AutoStartMediaMount = false, MediaMountRoot = "/mnt" }));
    }

    [Fact]
    public void OwnedPidFile_WritesReadsAndClearsCompanionRclonePid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fed-pid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("media-mount.pid", LocalMediaMountService.OwnedPidFileName);
            Assert.False(LocalMediaMountService.TryReadOwnedPid(dir, out _));
            LocalMediaMountService.WriteOwnedPid(dir, 4242);
            Assert.Equal("4242", File.ReadAllText(LocalMediaMountService.OwnedPidPath(dir)).Trim());
            Assert.True(LocalMediaMountService.TryReadOwnedPid(dir, out var pid));
            Assert.Equal(4242, pid);
            File.WriteAllText(LocalMediaMountService.OwnedPidPath(dir), "not-a-pid");
            Assert.False(LocalMediaMountService.TryReadOwnedPid(dir, out _));
            LocalMediaMountService.ClearOwnedPid(dir);
            Assert.False(File.Exists(LocalMediaMountService.OwnedPidPath(dir)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WinFspInstaller_UsesPinnedChecksummedMsi()
    {
        Assert.Equal("2ecb5c89405488a95bbd8a01875e02c48534fd37bbdfd84488f7590464d65944", WinFspInstaller.Sha256);
        Assert.Contains("winfsp-2.2.26215.msi", WinFspInstaller.DownloadUrl);
        var start = WinFspInstaller.CreateInstallStartInfo(@"C:\Temp\winfsp.msi");
        Assert.Equal("msiexec.exe", start.FileName);
        Assert.Equal("runas", start.Verb);
        Assert.Contains("/qn", start.Arguments);
    }

    [Fact]
    public async Task WinFspInstaller_RejectsWrongChecksumAndOnlyAttemptsOnce()
    {
        var calls = 0;
        var dir = Path.Combine(Path.GetTempPath(), "fed-winfsp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var http = new HttpClient(new Handler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("not-an-msi"u8.ToArray()) };
            }));
            var installer = new WinFspInstaller(http, dir, _ => throw new Exception("must not launch"), driverInstalled: () => false, windows: true);
            Assert.False(await installer.EnsureAsync(CancellationToken.None));
            Assert.False(await installer.EnsureAsync(CancellationToken.None));
            Assert.Equal(1, calls);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static byte[] ZipWith(string entryName, byte[] content)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(entryName);
            using var output = entry.Open();
            output.Write(content);
        }

        return stream.ToArray();
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(action(request));
    }
}
