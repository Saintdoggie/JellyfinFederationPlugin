using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Federation.Services.DirectLink
{
    /// <summary>
    /// This server's direct-link key: an ECDSA P-256 key in a self-signed
    /// certificate, kept in the plugin data folder (owner-only on Linux). Friends
    /// never trust it by a certificate authority; they pin its fingerprint, which
    /// they only learn over the token-authenticated friend channel.
    /// </summary>
    public sealed class DirectLinkIdentity
    {
        /// <summary>File name in the plugin data folder.</summary>
        public const string FileName = "directlink-identity.pfx";

        private DirectLinkIdentity(X509Certificate2 certificate)
        {
            Certificate = certificate;
            Fingerprint = FingerprintOf(certificate);
        }

        /// <summary>Gets the certificate with its private key.</summary>
        public X509Certificate2 Certificate { get; }

        /// <summary>Gets the SHA-256 of the certificate (DER), as lower-case hex.</summary>
        public string Fingerprint { get; }

        /// <summary>The fingerprint of any certificate, as lower-case hex.</summary>
        public static string FingerprintOf(X509Certificate certificate)
            => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();

        /// <summary>True when <paramref name="value"/> looks like a fingerprint (64 hex chars).</summary>
        public static bool IsFingerprint(string? value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Constant-time fingerprint comparison (case-insensitive hex).</summary>
        public static bool Matches(X509Certificate? certificate, string expected)
        {
            if (certificate == null || !IsFingerprint(expected))
            {
                return false;
            }

            var actual = SHA256.HashData(certificate.GetRawCertData());
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
        }

        /// <summary>
        /// Loads the identity from <paramref name="folder"/>, creating a new one
        /// when the file is missing, unreadable or about to expire. With no
        /// folder (tests, very early start-up) the identity lives in memory only.
        /// </summary>
        public static DirectLinkIdentity LoadOrCreate(string? folder, string commonName, ILogger? logger = null)
        {
            var path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
            if (path != null && File.Exists(path))
            {
                try
                {
                    var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
                    if (loaded.HasPrivateKey && loaded.NotAfter > DateTime.UtcNow.AddDays(30))
                    {
                        return new DirectLinkIdentity(loaded);
                    }

                    loaded.Dispose();
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[Federation] The direct link key file was unreadable; making a new one (friends re-learn it on their next connect)");
                }
            }

            var created = Create(commonName);
            if (path != null)
            {
                try
                {
                    Directory.CreateDirectory(folder!);
                    var temp = path + ".tmp";
                    File.WriteAllBytes(temp, Array.Empty<byte>());
                    RestrictToOwner(temp);
                    File.WriteAllBytes(temp, created.Export(X509ContentType.Pkcs12));
                    File.Move(temp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[Federation] Could not save the direct link key; using it for this run only");
                }
            }

            return new DirectLinkIdentity(created);
        }

        private static X509Certificate2 Create(string commonName)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var safeName = string.IsNullOrWhiteSpace(commonName) ? "jellyfin-federation" : commonName.Replace(",", string.Empty, StringComparison.Ordinal).Replace("=", string.Empty, StringComparison.Ordinal);
            var request = new CertificateRequest("CN=" + safeName, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") },
                false));
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            // Round-trip through PKCS#12 so the key is usable by the TLS stack on
            // every platform (an ephemeral key isn't, on Windows).
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }

        private static void RestrictToOwner(string path)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
