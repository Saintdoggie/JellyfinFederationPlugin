using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    /// <summary>
    /// Shared HTTP-to-file copy used by both Jellyfin-peer downloads and
    /// external catalog URLs. Supports Range resume so a crashed or paused
    /// transfer can continue from the bytes already on disk instead of
    /// starting over.
    /// </summary>
    /// <summary>
    /// <see cref="Progress{T}"/> posts through a sync context or the thread
    /// pool, so the last byte/speed sample can land after the transfer has
    /// already completed. Downloads need the callback on the copy loop itself.
    /// </summary>
    internal sealed class ImmediateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public ImmediateProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    internal static class DownloadTransfer
    {
        public static async Task CopyUrlToFileAsync(
            HttpClient client,
            string url,
            string destinationPath,
            long resumeFrom,
            IProgress<(long BytesRead, long? TotalBytes)>? progress,
            CancellationToken cancellationToken)
        {
            if (resumeFrom < 0)
            {
                resumeFrom = 0;
            }

            if (resumeFrom > 0 && File.Exists(destinationPath))
            {
                var onDisk = new FileInfo(destinationPath).Length;
                if (onDisk < resumeFrom)
                {
                    resumeFrom = onDisk;
                }
            }
            else if (resumeFrom > 0 && !File.Exists(destinationPath))
            {
                resumeFrom = 0;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0)
            {
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var partial = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!partial)
            {
                if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    // Remote ignored Range: rewrite from the start so we never
                    // splice a full body onto a prefix of itself.
                    resumeFrom = 0;
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                }
            }

            long? totalBytes = null;
            if (response.Content.Headers.ContentRange?.Length is long length && length > 0)
            {
                totalBytes = length;
            }
            else if (response.Content.Headers.ContentLength is long contentLength)
            {
                totalBytes = resumeFrom + contentLength;
            }

            var mode = resumeFrom > 0 ? FileMode.OpenOrCreate : FileMode.Create;
            await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, mode, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            if (resumeFrom > 0)
            {
                fileStream.Seek(resumeFrom, SeekOrigin.Begin);
                fileStream.SetLength(resumeFrom);
            }

            var buffer = new byte[81920];
            long totalRead = resumeFrom;
            progress?.Report((totalRead, totalBytes));
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress?.Report((totalRead, totalBytes));
            }

            progress?.Report((totalRead, totalBytes));
        }
    }
}
