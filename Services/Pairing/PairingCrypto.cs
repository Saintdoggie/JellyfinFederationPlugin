using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Federation.Services.Pairing
{
    /// <summary>Key agreement and derived keys for paired friends (docs/PAIRING.md).</summary>
    public static class PairingCrypto
    {
        /// <summary>Length of the pair key and every sub-key.</summary>
        public const int KeyLength = 32;

        private static readonly byte[] PairInfo = Encoding.ASCII.GetBytes("jf-fed-pair/1");
        private static readonly byte[] SafetyLabel = Encoding.ASCII.GetBytes("jf-fed-safety/1");

        /// <summary>
        /// The pair key both servers derive: HKDF over the ECDH secret of the two
        /// one-time keys and both codes' secrets, salted with the offer hash and bound
        /// to both certificates. The one-time private keys are dropped after pairing,
        /// so someone who saw both codes can never compute it.
        /// </summary>
        /// <returns>32 bytes.</returns>
        public static byte[] DerivePairKey(ECDiffieHellman ownKey, byte[] peerPublicKey, byte[] offerSecret, byte[] answerSecret, byte[] offerHash, byte[] offerFingerprint, byte[] answerFingerprint)
        {
            using var peer = ImportPublic(peerPublicKey);
            var shared = ownKey.DeriveRawSecretAgreement(peer.PublicKey);
            try
            {
                var ikm = Concat(shared, offerSecret, answerSecret);
                try
                {
                    return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, offerHash, Concat(PairInfo, offerFingerprint, answerFingerprint));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ikm);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }

        /// <summary>A purpose-specific key from the pair key (punch, proof, mail).</summary>
        /// <returns>32 bytes.</returns>
        public static byte[] SubKey(byte[] pairKey, string purpose)
            => HKDF.Expand(HashAlgorithmName.SHA256, pairKey, KeyLength, Encoding.ASCII.GetBytes("jf-fed-pair/1|" + purpose));

        /// <summary>
        /// Six groups of five digits, the same on both servers and different for every
        /// pairing: it covers the offer hash, both certificates and both one-time keys.
        /// When the admins' numbers match, nobody swapped the codes in between.
        /// </summary>
        /// <returns>Text like <c>01234 56789 ...</c>.</returns>
        public static string SafetyNumber(byte[] offerHash, byte[] offerFingerprint, byte[] answerFingerprint, byte[] offerPublicKey, byte[] answerPublicKey)
        {
            var hash = SHA256.HashData(Concat(SafetyLabel, offerHash, offerFingerprint, answerFingerprint, offerPublicKey, answerPublicKey));
            var groups = new string[6];
            for (var i = 0; i < groups.Length; i++)
            {
                // 5 bytes per group, reduced mod 100000: bias is under 1e-7.
                ulong value = 0;
                for (var j = 0; j < 5; j++)
                {
                    value = (value << 8) | hash[(i * 5) + j];
                }

                groups[i] = (value % 100000).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            }

            return string.Join(' ', groups);
        }

        /// <summary>The uncompressed public point of a key.</summary>
        /// <returns>65 bytes (0x04 | X | Y).</returns>
        public static byte[] PublicKeyBytes(ECDiffieHellman key)
        {
            var q = key.ExportParameters(false).Q;
            return Concat(new byte[] { 0x04 }, q.X!, q.Y!);
        }

        /// <summary>Imports a peer's uncompressed P-256 point (rejects points off the curve).</summary>
        /// <returns>A key holding only the public part.</returns>
        public static ECDiffieHellman ImportPublic(byte[] publicKey)
        {
            if (publicKey.Length != PairingCode.PublicKeyLength || publicKey[0] != 0x04)
            {
                throw new CryptographicException("Not an uncompressed P-256 point.");
            }

            var key = ECDiffieHellman.Create();
            try
            {
                key.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = publicKey.AsSpan(1, 32).ToArray(), Y = publicKey.AsSpan(33, 32).ToArray() }
                });
                return key;
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }

        internal static byte[] Concat(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts)
            {
                length += part.Length;
            }

            var result = new byte[length];
            var at = 0;
            foreach (var part in parts)
            {
                part.CopyTo(result, at);
                at += part.Length;
            }

            return result;
        }
    }
}
