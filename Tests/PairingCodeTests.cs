using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using Jellyfin.Plugin.Federation.Services.Pairing;
using Xunit;

namespace Jellyfin.Plugin.Federation.Tests;

/// <summary>Pairing codes, the pair key and the safety number (docs/PAIRING.md).</summary>
public class PairingCodeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    private static PairingCode Offer(ECDiffieHellman key, string name = "Home A") => new()
    {
        Kind = PairingCodeKind.Offer,
        Created = Now,
        Expires = Now + PairingCode.Lifetime,
        CertificateFingerprint = RandomNumberGenerator.GetBytes(32),
        PublicKey = PairingCrypto.PublicKeyBytes(key),
        Secret = RandomNumberGenerator.GetBytes(32),
        Endpoints = new List<IPEndPoint> { new(IPAddress.Parse("203.0.113.5"), 47950), new(IPAddress.Parse("192.168.1.10"), 47950) },
        Name = name
    };

    private static PairingCode AnswerFor(byte[] offerBytes, ECDiffieHellman key) => new()
    {
        Kind = PairingCodeKind.Answer,
        Created = Now,
        Expires = Now + PairingCode.Lifetime,
        OfferHash = PairingCode.HashOffer(offerBytes),
        CertificateFingerprint = RandomNumberGenerator.GetBytes(32),
        PublicKey = PairingCrypto.PublicKeyBytes(key),
        Secret = RandomNumberGenerator.GetBytes(32),
        Endpoints = new List<IPEndPoint> { new(IPAddress.Parse("198.51.100.7"), 51000) },
        Name = "Home B"
    };

    [Fact]
    public void Offer_RoundTrips()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(key);
        var (bytes, text) = offer.Encode();
        var (read, readBytes, error) = PairingCode.TryDecode(text, PairingCodeKind.Offer, Now);
        Assert.Null(error);
        Assert.Equal(bytes, readBytes);
        Assert.Equal(offer.Secret, read!.Secret);
        Assert.Equal(offer.PublicKey, read.PublicKey);
        Assert.Equal(offer.CertificateFingerprint, read.CertificateFingerprint);
        Assert.Equal(offer.Endpoints, read.Endpoints);
        Assert.Equal("Home A", read.Name);
        Assert.Equal(Now, read.Created);
    }

    [Fact]
    public void Answer_RoundTrips_OnlyWithTheOffersSecret()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(a);
        var (offerBytes, _) = offer.Encode();
        var (_, answerText) = AnswerFor(offerBytes, b).Encode(offer.Secret);

        Assert.Equal(PairingCode.HashOffer(offerBytes), PairingCode.PeekOfferHash(answerText));
        var (read, _, error) = PairingCode.TryDecode(answerText, PairingCodeKind.Answer, Now, offer.Secret);
        Assert.Null(error);
        Assert.Equal(PairingCode.HashOffer(offerBytes), read!.OfferHash);

        var (wrong, _, wrongError) = PairingCode.TryDecode(answerText, PairingCodeKind.Answer, Now, RandomNumberGenerator.GetBytes(32));
        Assert.Null(wrong);
        Assert.Contains("doesn't match", wrongError);
        Assert.Null(PairingCode.TryDecode(answerText, PairingCodeKind.Answer, Now, null).Code);
    }

    [Fact]
    public void Reading_ForgivesCaseSpacesAndLookAlikes()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (_, text) = Offer(key).Encode();
        var messy = " " + text.ToLowerInvariant().Replace("-", " ").Replace('0', 'o').Replace('1', 'l') + "\n";
        Assert.Null(PairingCode.TryDecode(messy, PairingCodeKind.Offer, Now).Error);
    }

    [Fact]
    public void EverySingleCharacterChange_IsRefused()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (_, text) = Offer(key).Encode();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '-')
            {
                continue;
            }

            var swapped = text[i] == 'A' ? 'B' : 'A';
            var changed = text.Substring(0, i) + swapped + text.Substring(i + 1);
            Assert.Null(PairingCode.TryDecode(changed, PairingCodeKind.Offer, Now).Code);
        }
    }

    [Fact]
    public void TruncatedPaddedAndJunkCodes_AreRefused()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (_, text) = Offer(key).Encode();
        var compact = text.Replace("-", string.Empty);
        foreach (var bad in new[] { null, string.Empty, "   ", "hello world", compact.Substring(0, compact.Length - 1), compact.Substring(0, compact.Length - 8), compact + "0", compact + "00000000", new string('Z', 1999), new string('0', 2001), compact.Replace('A', 'U') })
        {
            Assert.Null(PairingCode.TryDecode(bad, PairingCodeKind.Offer, Now).Code);
        }
    }

    [Fact]
    public void WrongKind_IsRefusedWithAHint()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(key);
        var (offerBytes, offerText) = offer.Encode();
        var (_, answerText) = AnswerFor(offerBytes, key).Encode(offer.Secret);
        Assert.Contains("first code", PairingCode.TryDecode(offerText, PairingCodeKind.Answer, Now, offer.Secret).Error);
        Assert.Contains("answer code", PairingCode.TryDecode(answerText, PairingCodeKind.Offer, Now).Error);
        Assert.Null(PairingCode.PeekOfferHash(offerText));
    }

    [Fact]
    public void ExpiredAndFutureCodes_AreRefused()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (_, text) = Offer(key).Encode();
        Assert.Null(PairingCode.TryDecode(text, PairingCodeKind.Offer, Now + PairingCode.Lifetime + PairingCode.ClockSkew).Error);
        Assert.Contains("expired", PairingCode.TryDecode(text, PairingCodeKind.Offer, Now + PairingCode.Lifetime + PairingCode.ClockSkew + TimeSpan.FromSeconds(1)).Error);
        Assert.Contains("clock", PairingCode.TryDecode(text, PairingCodeKind.Offer, Now - PairingCode.ClockSkew - TimeSpan.FromSeconds(1)).Error);

        var longLived = Offer(key);
        longLived.Expires = Now + TimeSpan.FromDays(30);
        Assert.Contains("clock", PairingCode.TryDecode(longLived.Encode().Text, PairingCodeKind.Offer, Now).Error);
    }

    [Fact]
    public void PointsOffTheCurve_AreRefused()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var offer = Offer(key);
        offer.PublicKey[64] ^= 1;
        Assert.ThrowsAny<Exception>(() => offer.Encode());
        Assert.ThrowsAny<CryptographicException>(() => PairingCrypto.ImportPublic(offer.PublicKey).Dispose());
    }

    [Fact]
    public void CodesWithBadEndpoints_CantBeMade()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var none = Offer(key);
        none.Endpoints.Clear();
        Assert.Throws<ArgumentException>(() => none.Encode());
        var v6 = Offer(key);
        v6.Endpoints = new List<IPEndPoint> { new(IPAddress.IPv6Loopback, 47950) };
        Assert.Throws<ArgumentException>(() => v6.Encode());
        var many = Offer(key);
        many.Endpoints = new List<IPEndPoint> { new(IPAddress.Loopback, 1), new(IPAddress.Loopback, 2), new(IPAddress.Loopback, 3), new(IPAddress.Loopback, 4) };
        Assert.Throws<ArgumentException>(() => many.Encode());
    }

    [Fact]
    public void LongNames_AreCutOnACharacterBoundary()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var (_, text) = Offer(key, new string('é', 30)).Encode();
        var read = PairingCode.TryDecode(text, PairingCodeKind.Offer, Now).Code!;
        Assert.Equal(new string('é', 20), read.Name);
    }

    [Fact]
    public void PairKey_IsTheSameOnBothSides_AndNeedsAPrivateKey()
    {
        using var a = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var b = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var eve = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] sa = RandomNumberGenerator.GetBytes(32), sb = RandomNumberGenerator.GetBytes(32), hash = RandomNumberGenerator.GetBytes(16);
        byte[] fa = RandomNumberGenerator.GetBytes(32), fb = RandomNumberGenerator.GetBytes(32);
        var onA = PairingCrypto.DerivePairKey(a, PairingCrypto.PublicKeyBytes(b), sa, sb, hash, fa, fb);
        var onB = PairingCrypto.DerivePairKey(b, PairingCrypto.PublicKeyBytes(a), sa, sb, hash, fa, fb);
        Assert.Equal(onA, onB);

        // Everything in both codes, but no private key of either side.
        Assert.NotEqual(onA, PairingCrypto.DerivePairKey(eve, PairingCrypto.PublicKeyBytes(b), sa, sb, hash, fa, fb));
        Assert.NotEqual(onA, PairingCrypto.DerivePairKey(a, PairingCrypto.PublicKeyBytes(b), sb, sa, hash, fa, fb));
        Assert.NotEqual(onA, PairingCrypto.DerivePairKey(a, PairingCrypto.PublicKeyBytes(b), sa, sb, hash, fb, fa));

        var punch = PairingCrypto.SubKey(onA, "punch");
        Assert.NotEqual(punch, PairingCrypto.SubKey(onA, "proof"));
        Assert.Equal(32, punch.Length);
    }

    [Fact]
    public void SafetyNumber_DependsOnEveryPartOfThePairing()
    {
        byte[] h = RandomNumberGenerator.GetBytes(16), fa = RandomNumberGenerator.GetBytes(32), fb = RandomNumberGenerator.GetBytes(32);
        byte[] ea = RandomNumberGenerator.GetBytes(65), eb = RandomNumberGenerator.GetBytes(65);
        var n = PairingCrypto.SafetyNumber(h, fa, fb, ea, eb);
        Assert.Matches(@"^\d{5}( \d{5}){5}$", n);
        Assert.Equal(n, PairingCrypto.SafetyNumber(h, fa, fb, ea, eb));
        Assert.NotEqual(n, PairingCrypto.SafetyNumber(RandomNumberGenerator.GetBytes(16), fa, fb, ea, eb));
        Assert.NotEqual(n, PairingCrypto.SafetyNumber(h, fb, fa, ea, eb));
        Assert.NotEqual(n, PairingCrypto.SafetyNumber(h, fa, RandomNumberGenerator.GetBytes(32), ea, eb));
        Assert.NotEqual(n, PairingCrypto.SafetyNumber(h, fa, fb, ea, RandomNumberGenerator.GetBytes(65)));
    }

    [Fact]
    public void Base32_RoundTripsEveryLength()
    {
        for (var length = 0; length < 64; length++)
        {
            var bytes = RandomNumberGenerator.GetBytes(length);
            Assert.Equal(bytes, Base32.Decode(Base32.Encode(bytes)));
        }
    }
}
