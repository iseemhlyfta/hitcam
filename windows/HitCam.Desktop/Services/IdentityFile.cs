using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HitCam.Core.Security;

namespace HitCam.Desktop.Services;

/// <summary>
/// The PC's TLS identity in %APPDATA%\HitCam\identity.bin: PKCS#12 encrypted for this Windows user (DPAPI). Made on
/// first start and kept: phones pin its fingerprint, so a new one means pairing every phone again.
/// </summary>
public static class IdentityFile
{
    /// <summary>The stored identity, or a new one saved there; null if neither works (no TLS then).</summary>
    public static ServerIdentity? LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
                return ServerIdentity.Load(Unprotect(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Unreadable (another user's, damaged): kept aside, not overwritten, in case it can be recovered.
            Trace.TraceError($"HitCam: identity {path} unreadable, making a new one (phones must pair again): {ex.Message}");
            TryMoveAside(path);
        }

        try
        {
            var identity = ServerIdentity.Create();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, Protect(identity.Export()));
            File.Move(temp, path, overwrite: true);
            return identity;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Trace.TraceError($"HitCam: no TLS identity, only old phone apps can connect: {ex}");
            return null;
        }
    }

    private static void TryMoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Crypt(data, protect: false);

    private static unsafe byte[] Crypt(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        {
            var blob = new DataBlob { Size = data.Length, Data = (IntPtr)input };
            var ok = protect
                ? CryptProtectData(ref blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok)
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }

    private const int UiForbidden = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
