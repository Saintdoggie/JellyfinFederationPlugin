using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Federation.Services
{
    internal static class HttpContentReader
    {
        internal const int ControlJsonLimit = 64 * 1024;
        internal const int CatalogJsonLimit = 64 * 1024 * 1024;

        public static async Task<string> ReadTextAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
            => System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(content, maximumBytes, cancellationToken).ConfigureAwait(false));

        public static async Task<byte[]> ReadBytesAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength > maximumBytes)
            {
                throw new InvalidDataException("The remote response exceeds the permitted size.");
            }

            using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            var chunk = new byte[32 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("The remote response exceeds the permitted size.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class BoundedHttpClient : IDisposable
    {
        private readonly HttpClient _inner;

        public BoundedHttpClient(HttpClient inner)
        {
            _inner = inner;
        }

        internal HttpClient InnerClient => _inner;

        public Task<HttpResponseMessage> GetAsync(string? requestUri, CancellationToken cancellationToken)
            => _inner.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public Task<HttpResponseMessage> GetAsync(string? requestUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
            => _inner.GetAsync(requestUri, completionOption, cancellationToken);

        public Task<HttpResponseMessage> PostAsync(string? requestUri, HttpContent content, CancellationToken cancellationToken)
            => _inner.SendAsync(new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _inner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
            => _inner.SendAsync(request, completionOption, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }

    internal static class BrowseImagePolicy
    {
        public const int MaximumBytes = 5 * 1024 * 1024;
        public const int GridPosterMaxWidth = 480;
        public const int DetailPosterMaxWidth = 800;

        public static bool TryGetContentType(string? value, out string contentType)
        {
            contentType = value?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
            return contentType is "image/jpeg" or "image/png" or "image/webp" or "image/gif" or "image/avif";
        }
    }
}
