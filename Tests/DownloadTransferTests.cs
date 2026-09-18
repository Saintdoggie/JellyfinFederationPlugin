using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Federation.Services;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

public sealed class DownloadTransferTests
{
    [Fact]
    public async Task CopyUrlToFileAsync_ResumesFromPartialViaRange()
    {
        var payload = new byte[8000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var client = new HttpClient(new RangeHandler(payload)) { BaseAddress = new Uri("http://peer.test/") };
        var path = Path.Combine(Path.GetTempPath(), "fed-dl-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(path, payload.AsMemory(0, 1500).ToArray());
            long? lastTotal = null;
            long lastRead = 0;
            var progress = new ImmediateProgress<(long BytesRead, long? TotalBytes)>(p =>
            {
                lastRead = p.BytesRead;
                lastTotal = p.TotalBytes;
            });

            await DownloadTransfer.CopyUrlToFileAsync(client, "http://peer.test/file", path, 1500, progress, CancellationToken.None);

            var onDisk = await File.ReadAllBytesAsync(path);
            Assert.Equal(payload, onDisk);
            Assert.Equal(payload.Length, lastRead);
            Assert.Equal(payload.Length, lastTotal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task CopyUrlToFileAsync_FullBodyWhenRangeIgnored_RewritesFile()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var client = new HttpClient(new IgnoreRangeHandler(payload)) { BaseAddress = new Uri("http://peer.test/") };
        var path = Path.Combine(Path.GetTempPath(), "fed-dl-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9, 9 });
            await DownloadTransfer.CopyUrlToFileAsync(client, "http://peer.test/file", path, 3, null, CancellationToken.None);
            Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public RangeHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            var slice = new byte[_payload.Length - (int)from];
            Buffer.BlockCopy(_payload, (int)from, slice, 0, slice.Length);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, _payload.Length - 1, _payload.Length);
            response.Headers.AcceptRanges.Add("bytes");
            return Task.FromResult(response);
        }
    }

    private sealed class IgnoreRangeHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public IgnoreRangeHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
        }
    }
}
