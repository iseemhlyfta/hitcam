using System.Security.Cryptography;
using System.Text;

namespace HitCam.Core.Security;

/// <summary>
/// Pairing over TLS (protocol v2) without letting a man in the middle learn the 6-digit PIN. Hashing a short PIN
/// is no protection by itself: whoever sees HMAC(PIN, ...) tries all million PINs in milliseconds. So each side
/// first commits to (PIN, the certificate fingerprint it sees) under a secret random nonce, and opens only after the
/// other side has committed:
/// <list type="number">
/// <item>PC → phone: c = H("hitcam-pc-v2", fp, PIN, r) in HelloAck.pinCommit.</item>
/// <item>phone → PC, once the user typed the PIN: d = H("hitcam-phone-v2", fp seen, PIN, s) in PairRequest.commit.</item>
/// <item>PC → phone: r (PairReveal). The phone checks c with the fingerprint it sees: a different certificate (a
/// middleman's) does not match, and the middleman had to commit before it could know the PIN.</item>
/// <item>phone → PC: s (PairConfirm). The PC checks d: the phone knew the PIN.</item>
/// </list>
/// An opened PIN is used up: the PC ends the pairing window whatever the outcome, and a phone that disconnects
/// after r counts as a wrong PIN.
/// </summary>
public static class PinProof
{
    public const string PcLabel = "hitcam-pc-v2";
    public const string PhoneLabel = "hitcam-phone-v2";
    public const int NonceSize = 32;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    /// <summary>SHA-256 of label, 0, fingerprint (32), PIN (6 ASCII digits), nonce (32): fixed sizes, so unambiguous.</summary>
    public static byte[] Commit(string label, ReadOnlySpan<byte> fingerprint, string pin, ReadOnlySpan<byte> nonce)
    {
        if (fingerprint.Length != 32 || nonce.Length != NonceSize || pin.Length != 6)
            throw new ArgumentException("Fingerprint and nonce are 32 bytes, the PIN 6 digits.");
        var label8 = Encoding.ASCII.GetBytes(label);
        var data = new byte[label8.Length + 1 + 32 + 6 + NonceSize];
        label8.CopyTo(data, 0);
        fingerprint.CopyTo(data.AsSpan(label8.Length + 1));
        Encoding.ASCII.GetBytes(pin, data.AsSpan(label8.Length + 33));
        nonce.CopyTo(data.AsSpan(label8.Length + 39));
        return SHA256.HashData(data);
    }

    /// <summary>Lowercase hex of exactly <paramref name="size"/> bytes, or null.</summary>
    public static byte[]? FromHex(string? hex, int size)
    {
        if (hex is null || hex.Length != size * 2)
            return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
