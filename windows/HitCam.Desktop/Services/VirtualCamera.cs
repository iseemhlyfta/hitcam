using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HitCam.Desktop.Services;

/// <summary>Exports of HitCamVCam.dll (windows/HitCam.VCam).</summary>
internal static class NativeMethods
{
    private const string Library = "HitCamVCam";

    [DllImport(Library)]
    public static extern int HitCam_BridgeCreate(out IntPtr handle);

    [DllImport(Library)]
    public static extern int HitCam_BridgeDecode(IntPtr handle, byte[] data, uint length, long timestamp);

    [DllImport(Library)]
    public static extern void HitCam_BridgeClearSignal(IntPtr handle);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HitCam_BridgeIsLinked(IntPtr handle);

    [DllImport(Library)]
    public static extern void HitCam_BridgeDestroy(IntPtr handle);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HitCam_DenoiseAvailable();

    [DllImport(Library)]
    public static extern void HitCam_BridgeSetDenoiseMode(IntPtr handle, int mode);

    [DllImport(Library)]
    public static extern void HitCam_BridgeDenoiseStats(IntPtr handle, out double milliseconds, out int error, out double noise, out float amount);

    [DllImport(Library)]
    public static extern void HitCam_BridgePreviewInfo(IntPtr handle, out uint width, out uint height, out ulong frame);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HitCam_BridgeCopyPreview(IntPtr handle, IntPtr destination, uint stride, uint width, uint height);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    public static extern int HitCam_VirtualCameraStart(string friendlyName, out IntPtr handle);

    [DllImport(Library)]
    public static extern void HitCam_VirtualCameraStop(IntPtr handle);
}

public enum CameraSetup
{
    /// <summary>HitCamVCam.dll is not next to the app (built without the C++ toolchain).</summary>
    Unavailable,
    /// <summary>HitCamVCam.dll next to the app is not the one this build was made with; it is never installed.</summary>
    Tampered,
    NotInstalled,
    /// <summary>Installed, but the installed DLL differs from the one shipped with this build.</summary>
    Outdated,
    Installed,
}

/// <summary>
/// The "HitCam" camera other apps see. The media source DLL is copied to Program Files and registered
/// once with admin rights; after that the camera is added without elevation for as long as the app runs.
/// </summary>
public sealed partial class VirtualCamera : IDisposable
{
    public const string FriendlyName = "HitCam";
    private const string Clsid = "{FB88D813-B99A-4C03-8C22-A6C972983FCD}";
    private const int ErrorCancelled = 1223;

    private IntPtr _handle;

    private static string BundledDll => Path.Combine(AppContext.BaseDirectory, "HitCamVCam.dll");

    /// <summary>
    /// SHA-256 (hex) of the HitCamVCam.dll this build was made with, embedded at build time (see HitCam.Desktop.csproj);
    /// empty if it was built without the camera. Only a DLL with this hash is ever installed with admin rights.
    /// </summary>
    public static string ExpectedSha256 { get; } =
        typeof(VirtualCamera).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "HitCamVCamSha256")?.Value is { } hash && Sha256Hex().IsMatch(hash)
            ? hash.ToUpperInvariant()
            : "";

    public bool IsRunning => _handle != IntPtr.Zero;

    public static CameraSetup GetSetup()
    {
        if (ExpectedSha256.Length == 0 || !File.Exists(BundledDll))
            return CameraSetup.Unavailable;
        if (!HasExpectedHash(BundledDll))
            return CameraSetup.Tampered;

        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
        if (key?.GetValue(null) is not string registered || !File.Exists(registered))
            return CameraSetup.NotInstalled;
        return HasExpectedHash(registered) ? CameraSetup.Installed : CameraSetup.Outdated;
    }

    /// <summary>Adds the camera to Windows until <see cref="Stop"/> or process exit. Returns an HRESULT.</summary>
    public int Start()
    {
        if (IsRunning)
            return 0;
        try
        {
            return NativeMethods.HitCam_VirtualCameraStart(FriendlyName, out _handle);
        }
        catch (DllNotFoundException)
        {
            return unchecked((int)0x8007007E); // ERROR_MOD_NOT_FOUND
        }
    }

    public void Stop()
    {
        if (!IsRunning)
            return;
        NativeMethods.HitCam_VirtualCameraStop(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Copies the DLL to Program Files and registers it (UAC prompt). False if cancelled or failed.</summary>
    public static async Task<bool> InstallAsync()
    {
        if (GetSetup() is CameraSetup.Unavailable or CameraSetup.Tampered)
            return false;

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(InstallScript(BundledDll, ExpectedSha256))),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && GetSetup() == CameraSetup.Installed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// The script run as administrator. Its text is fixed: the source path goes in as base64 and the hash as hex,
    /// neither of which can contain a quote, so no file name can change what it does. The copy in Program Files
    /// (which the user cannot write to) is checked against the hash before it replaces the camera and is registered.
    /// </summary>
    public static string InstallScript(string sourcePath, string expectedSha256)
    {
        if (!Sha256Hex().IsMatch(expectedSha256))
            throw new ArgumentException("Not a SHA-256 hash.", nameof(expectedSha256));
        return InstallScriptTemplate
            .Replace("@SOURCE@", Convert.ToBase64String(Encoding.UTF8.GetBytes(sourcePath)), StringComparison.Ordinal)
            .Replace("@SHA256@", expectedSha256, StringComparison.Ordinal);
    }

    // Exit codes: 0 registered, 1 failed, 2 the copy does not match the hash.
    // The camera service keeps the old DLL loaded; stopping it lets the file be replaced (it restarts on demand,
    // but every camera, the built-in one too, drops for a moment).
    private const string InstallScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@SOURCE@'))
        $expected = '@SHA256@'
        $dir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'HitCam'
        $target = Join-Path $dir 'HitCamVCam.dll'
        $staged = Join-Path $dir 'HitCamVCam.dll.new'
        try {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            Copy-Item -LiteralPath $source -Destination $staged -Force
            if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $expected) {
                Remove-Item -LiteralPath $staged -Force
                exit 2
            }
            Stop-Service -Name FrameServer, FrameServerMonitor -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $staged -Destination $target -Force
            $regsvr32 = Join-Path ([Environment]::SystemDirectory) 'regsvr32.exe'
            $p = Start-Process -FilePath $regsvr32 -ArgumentList '/s', ('"' + $target + '"') -Wait -PassThru
            exit $p.ExitCode
        } catch {
            Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
            exit 1
        }
        """;

    public void Dispose() => Stop();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256Hex();

    private static bool HasExpectedHash(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
