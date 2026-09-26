using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HitCam.Core.Security;

/// <summary>
/// The PC's TLS identity: a self-signed ECDSA P-256 certificate, made once and kept. Phones pin its
/// <see cref="Fingerprint"/> (SHA-256 of the certificate's DER bytes): from the QR code, or from a pairing whose PIN
/// commitments bind it (see <see cref="PinProof"/>). No certificate authority is involved.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    private readonly byte[] _pkcs12;

    private ServerIdentity(X509Certificate2 certificate, byte[] pkcs12)
    {
        _pkcs12 = pkcs12;
        Certificate = certificate;
        Fingerprint = FingerprintOf(certificate);
    }

    /// <summary>With its private key, loaded so that Windows TLS (SChannel) can use it.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>SHA-256 of the certificate (DER), 32 bytes.</summary>
    public byte[] Fingerprint { get; }

    /// <summary><see cref="Fingerprint"/> as lowercase hex, as in the QR code.</summary>
    public string FingerprintHex => Convert.ToHexStringLower(Fingerprint);

    public static byte[] FingerprintOf(X509Certificate certificate) => SHA256.HashData(certificate.GetRawCertData());

    /// <summary>A new identity; valid for decades, since nothing but the fingerprint is checked.</summary>
    public static ServerIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HitCam", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(50));
        // Round trip through PKCS#12: SChannel cannot use the ephemeral key CreateSelfSigned returns.
        return Load(certificate.Export(X509ContentType.Pkcs12));
    }

    /// <summary>From <see cref="Export"/>'s bytes.</summary>
    /// <exception cref="CryptographicException">Not a PKCS#12 with an ECDSA key.</exception>
    public static ServerIdentity Load(byte[] pkcs12)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet);
        if (!certificate.HasPrivateKey || certificate.GetECDsaPublicKey() is null)
        {
            certificate.Dispose();
            throw new CryptographicException("The identity has no ECDSA private key.");
        }
        return new ServerIdentity(certificate, pkcs12.ToArray());
    }

    /// <summary>Certificate and private key as PKCS#12, for storage.</summary>
    public byte[] Export() => _pkcs12.ToArray();

    public void Dispose() => Certificate.Dispose();
}
