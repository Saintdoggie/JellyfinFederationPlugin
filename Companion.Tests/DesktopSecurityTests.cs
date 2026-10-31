using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using FederationCompanion;

namespace FederationCompanion.Tests;

public class DesktopSecurityTests
{
    [Fact]
    public async Task CredentialFileIsPrivateBeforeWritingSecrets()
    {
        var path = Path.Combine(Path.GetTempPath(), "fed-private-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = PrivateFile.Create(path))
            {
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                await file.WriteAsync(new byte[] { 1, 2, 3 });
            }
            Assert.Equal(3, new FileInfo(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProcessIdentityRejectsReusedPidAndWrongExecutable()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new LocalMediaMountService.OwnedMountIdentity(process.Id,
            process.StartTime.ToUniversalTime().Ticks, process.MainModule!.FileName!);
        Assert.True(LocalMediaMountService.MatchesOwnedIdentity(process, identity));
        Assert.False(LocalMediaMountService.MatchesOwnedIdentity(process, identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks - 1 }));
        Assert.False(LocalMediaMountService.MatchesOwnedIdentity(process, identity with { Executable = "/unrelated/rclone" }));
    }

    [Fact]
    public void StartupEntryEscapesDesktopFieldCodesAndRejectsLineInjection()
    {
        Assert.Contains("%%f", LinuxAutostart.BuildDesktopEntry("/home/me/100%files/FederationCompanion"));
        Assert.Throws<ArgumentException>(() => LinuxAutostart.BuildDesktopEntry("/tmp/app\nHidden=true"));
    }

    [Fact]
    public void WindowsUpdaterRejectsBatchExpansionInPaths()
        => Assert.Throws<InvalidDataException>(() => CompanionUpdater.WindowsRestartCommands(
            @"C:\Users\%TEMP%\Companion", @"C:\stage", "FederationCompanion.exe", 42));

    [Fact]
    public void UnixUpdaterQuotesSubstitutionAndApostrophes()
        => Assert.Equal("'/tmp/$(id)/a'\"'\"'b'", CompanionUpdater.ShellQuote("/tmp/$(id)/a'b"));

    [Fact]
    public async Task UpdaterDownloadsChecksumAndKeepsMatchingArchive()
    {
        var archive = "signed-bytes-are-not-implied"u8.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{sha}  {CompanionVersion.AssetName()}\n") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }));
        var updater = new CompanionUpdater(http);
        var zipPath = TempArchivePath();
        try
        {
            var result = await updater.DownloadVerifiedArchiveAsync(zipPath, CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(archive, await File.ReadAllBytesAsync(zipPath));
            Assert.Equal(2, requests.Count);
            Assert.EndsWith("/SHA256SUMS", requests[0], StringComparison.Ordinal);
            Assert.EndsWith("/" + CompanionVersion.AssetName(), requests[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task UpdaterRejectsArchiveWhoseChecksumDoesNotMatch()
    {
        var archive = "corrupt-archive"u8.ToArray();
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            return request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('a', 64)}  {CompanionVersion.AssetName()}\n") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }));
        var updater = new CompanionUpdater(http);
        var zipPath = TempArchivePath();

        var result = await updater.DownloadVerifiedArchiveAsync(zipPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("did not match", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing was installed", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, requests);
        Assert.False(File.Exists(zipPath));
    }

    [Theory]
    [InlineData("not a checksum")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  unrelated.zip\n")]
    public async Task UpdaterRejectsMissingOrMalformedChecksumEntry(string checksums)
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal)
                    ? new StringContent(checksums)
                    : new ByteArrayContent("must-not-be-downloaded"u8.ToArray())
            };
        }));
        var updater = new CompanionUpdater(http);
        var zipPath = TempArchivePath();

        var result = await updater.DownloadVerifiedArchiveAsync(zipPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("missing or malformed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, requests);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public async Task UpdaterFailsClosedWhenChecksumFileIsMissing()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var updater = new CompanionUpdater(http);
        var zipPath = TempArchivePath();

        var result = await updater.DownloadVerifiedArchiveAsync(zipPath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SHA256SUMS", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, requests);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public void UpdaterChecksumParserRejectsAmbiguousOrNonstandardEntries()
    {
        var asset = CompanionVersion.AssetName();
        var sha = new string('a', 64);
        Assert.False(CompanionUpdater.TryParseChecksum($"{sha}  {asset}\n{sha}  {asset}\n", asset, out _));
        Assert.False(CompanionUpdater.TryParseChecksum($"{sha}\t{asset}\n", asset, out _));
        Assert.False(CompanionUpdater.TryParseChecksum($"{sha}  {asset} \n", asset, out _));
        Assert.False(CompanionUpdater.TryParseChecksum($"{sha[..63]}  {asset}\n", asset, out _));
    }

    private static string TempArchivePath()
        => Path.Combine(Path.GetTempPath(), "federation-companion-updater-test-" + Guid.NewGuid().ToString("N") + ".zip");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(action(request));
    }
}
