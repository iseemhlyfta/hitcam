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
    public static extern bool HitCam_ArtifactReductionAvailable();

    [DllImport(Library)]
    public static extern void HitCam_BridgeSetProcessing(IntPtr handle, in HitCamProcessing settings);

    /// <summary>A time of -1 ms means that stage did not run on the last frame.</summary>
    [DllImport(Library)]
    public static extern void HitCam_BridgeProcessingStats(IntPtr handle, out double gpuMilliseconds, out double artifactMilliseconds, out int artifactError);

    [DllImport(Library)]
    public static extern void HitCam_BridgePreviewInfo(IntPtr handle, out uint width, out uint height, out ulong frame);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HitCam_BridgeCopyPreview(IntPtr handle, IntPtr destination, uint stride, uint width, uint height);

    [DllImport(Library, CharSet = CharSet.Unicode)]
    public static extern int HitCam_VirtualCameraStart(string friendlyName, out IntPtr handle);

    [DllImport(Library)]
    public static extern void HitCam_VirtualCameraStop(IntPtr handle);

    [DllImport(Library)]
    public static extern int HitCam_DShowStart();

    [DllImport(Library)]
    public static extern void HitCam_DShowStop();
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
/// The "HitCam" camera other apps see.
/// Windows 11: a Media Foundation camera; its media source DLL is copied to Program Files and registered once with
/// admin rights, after that the camera is added without elevation for as long as the app runs.
/// Windows 10 (no Media Foundation virtual cameras): a DirectShow camera (HitCamDShow.dll for 64-bit apps,
/// HitCamDShow32.dll for 32-bit ones), installed the same way; it shows frames while the app runs.
/// </summary>
public sealed partial class VirtualCamera : IDisposable
{
    public const string FriendlyName = "HitCam";
    private const string Clsid = "{FB88D813-B99A-4C03-8C22-A6C972983FCD}";
    private const string DirectShowClsid = "{BCFB4029-0D53-4154-9649-2240739784B3}";
    private const int ErrorCancelled = 1223;

    private IntPtr _handle;
    private bool _directShowRunning;

    private static string BundledDll => Path.Combine(AppContext.BaseDirectory, "HitCamVCam.dll");
    private static string BundledDirectShowDll => Path.Combine(AppContext.BaseDirectory, "HitCamDShow.dll");
    private static string BundledDirectShowDll32 => Path.Combine(AppContext.BaseDirectory, "HitCamDShow32.dll");

    /// <summary>
    /// Whether this Windows gets the DirectShow camera: Windows 10 and older, which lack Media Foundation virtual
    /// cameras (Windows 11 is build 22000+). HITCAM_CAMERA=directshow forces it, to try it on Windows 11.
    /// </summary>
    public static bool UsesDirectShow { get; } =
        Environment.OSVersion.Version.Build < 22000
        || string.Equals(Environment.GetEnvironmentVariable("HITCAM_CAMERA"), "directshow", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// SHA-256 (hex) of the HitCamVCam.dll this build was made with, embedded at build time (see HitCam.Desktop.csproj);
    /// empty if it was built without the camera. Only a DLL with this hash is ever installed with admin rights.
    /// </summary>
    public static string ExpectedSha256 { get; } = EmbeddedHash("HitCamVCamSha256");

    /// <summary>The same for the DirectShow camera, 64- and 32-bit.</summary>
    public static string ExpectedDirectShowSha256 { get; } = EmbeddedHash("HitCamDShowSha256");
    public static string ExpectedDirectShow32Sha256 { get; } = EmbeddedHash("HitCamDShow32Sha256");

    public bool IsRunning => _handle != IntPtr.Zero || _directShowRunning;

    public static CameraSetup GetSetup() => UsesDirectShow ? GetDirectShowSetup() : GetMediaFoundationSetup();

    private static CameraSetup GetMediaFoundationSetup()
    {
        if (ExpectedSha256.Length == 0 || !File.Exists(BundledDll))
            return CameraSetup.Unavailable;
        if (!HasHash(BundledDll, ExpectedSha256))
            return CameraSetup.Tampered;

        var registered = RegisteredServer(Clsid, RegistryView.Registry64);
        if (registered is null)
            return CameraSetup.NotInstalled;
        return HasHash(registered, ExpectedSha256) ? CameraSetup.Installed : CameraSetup.Outdated;
    }

    private static CameraSetup GetDirectShowSetup()
    {
        if (ExpectedDirectShowSha256.Length == 0 || ExpectedDirectShow32Sha256.Length == 0
            || !File.Exists(BundledDirectShowDll) || !File.Exists(BundledDirectShowDll32) || !File.Exists(BundledDll))
            return CameraSetup.Unavailable;
        if (!HasHash(BundledDirectShowDll, ExpectedDirectShowSha256) || !HasHash(BundledDirectShowDll32, ExpectedDirectShow32Sha256))
            return CameraSetup.Tampered;

        var registered64 = RegisteredServer(DirectShowClsid, RegistryView.Registry64);
        var registered32 = RegisteredServer(DirectShowClsid, RegistryView.Registry32);
        if (registered64 is null || registered32 is null)
            return CameraSetup.NotInstalled;
        return HasHash(registered64, ExpectedDirectShowSha256) && HasHash(registered32, ExpectedDirectShow32Sha256)
            ? CameraSetup.Installed
            : CameraSetup.Outdated;
    }

    /// <summary>The DLL a COM class is registered with (machine-wide), if that file exists.</summary>
    private static string? RegisteredServer(string clsid, RegistryView view)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = root.OpenSubKey($@"SOFTWARE\Classes\CLSID\{clsid}\InprocServer32");
        return key?.GetValue(null) is string path && File.Exists(path) ? path : null;
    }

    /// <summary>Adds the camera to Windows until <see cref="Stop"/> or process exit. Returns an HRESULT.</summary>
    public int Start()
    {
        if (IsRunning)
            return 0;
        try
        {
            if (!UsesDirectShow)
                return NativeMethods.HitCam_VirtualCameraStart(FriendlyName, out _handle);
            var hr = NativeMethods.HitCam_DShowStart();
            _directShowRunning = hr >= 0;
            return hr;
        }
        catch (DllNotFoundException)
        {
            return unchecked((int)0x8007007E); // ERROR_MOD_NOT_FOUND
        }
    }

    public void Stop()
    {
        if (_directShowRunning)
        {
            NativeMethods.HitCam_DShowStop();
            _directShowRunning = false;
        }
        if (_handle == IntPtr.Zero)
            return;
        NativeMethods.HitCam_VirtualCameraStop(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>Copies the DLLs to Program Files and registers them (UAC prompt). False if cancelled or failed.</summary>
    public static async Task<bool> InstallAsync()
    {
        if (GetSetup() is CameraSetup.Unavailable or CameraSetup.Tampered)
            return false;

        var script = UsesDirectShow
            ? DirectShowInstallScript(BundledDirectShowDll, ExpectedDirectShowSha256, BundledDirectShowDll32, ExpectedDirectShow32Sha256)
            : InstallScript(BundledDll, ExpectedSha256);
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                        Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
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
        RequireHash(expectedSha256);
        return InstallScriptTemplate
            .Replace("@SOURCE@", Base64(sourcePath), StringComparison.Ordinal)
            .Replace("@SHA256@", expectedSha256, StringComparison.Ordinal);
    }

    /// <summary>The same for the DirectShow camera: the 64-bit DLL registered for 64-bit apps, the 32-bit one for 32-bit apps.</summary>
    public static string DirectShowInstallScript(string source64, string sha256For64, string source32, string sha256For32)
    {
        RequireHash(sha256For64);
        RequireHash(sha256For32);
        return DirectShowInstallScriptTemplate
            .Replace("@SOURCE64@", Base64(source64), StringComparison.Ordinal)
            .Replace("@SHA256_64@", sha256For64, StringComparison.Ordinal)
            .Replace("@SOURCE32@", Base64(source32), StringComparison.Ordinal)
            .Replace("@SHA256_32@", sha256For32, StringComparison.Ordinal);
    }

    // Exit codes: 0 registered, 1 failed, 2 the copy does not match the hash.
    // The installed DLL stays loaded wherever the camera was used, HitCam.exe itself included (adding the camera loads
    // it), so it cannot be overwritten: each version gets its own file name (with part of its hash), and old ones are
    // deleted once nothing holds them. Stopping the camera service makes it load the new version (it restarts on
    // demand, but every camera, the built-in one too, drops for a moment).
    private const string InstallScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@SOURCE@'))
        $expected = '@SHA256@'
        $dir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'HitCam'
        $target = Join-Path $dir ('HitCamVCam-' + $expected.Substring(0, 16) + '.dll')
        $staged = $target + '.new'
        try {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $current = (Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $expected
            if (-not $current) {
                Copy-Item -LiteralPath $source -Destination $staged -Force
                if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $expected) {
                    Remove-Item -LiteralPath $staged -Force
                    exit 2
                }
                Move-Item -LiteralPath $staged -Destination $target -Force
            }
            Stop-Service -Name FrameServer, FrameServerMonitor -Force -ErrorAction SilentlyContinue
            $regsvr32 = Join-Path ([Environment]::SystemDirectory) 'regsvr32.exe'
            $p = Start-Process -FilePath $regsvr32 -ArgumentList '/s', ('"' + $target + '"') -Wait -PassThru
            if ($p.ExitCode -ne 0) { exit $p.ExitCode }
            Get-ChildItem -LiteralPath $dir -Filter 'HitCamVCam*.dll' |
                Where-Object { $_.FullName -ne $target } |
                Remove-Item -Force -ErrorAction SilentlyContinue
            exit 0
        } catch {
            Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
            exit 1
        }
        """;

    // Same exit codes. Apps that have the camera open keep its DLL loaded, so each version gets its own file name
    // (with part of its hash) instead of replacing the old file; old versions are deleted once nothing holds them.
    private const string DirectShowInstallScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        $dir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'HitCam'
        $windows = [Environment]::GetFolderPath('Windows')
        $cameras = @(
            @{ Source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@SOURCE64@')); Hash = '@SHA256_64@';
               Name = 'HitCamDShow'; Regsvr32 = Join-Path $windows 'System32\regsvr32.exe' },
            @{ Source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@SOURCE32@')); Hash = '@SHA256_32@';
               Name = 'HitCamDShow32'; Regsvr32 = Join-Path $windows 'SysWOW64\regsvr32.exe' }
        )
        $staged = $null
        try {
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            foreach ($camera in $cameras) {
                $target = Join-Path $dir ($camera.Name + '-' + $camera.Hash.Substring(0, 16) + '.dll')
                $staged = $target + '.new'
                Copy-Item -LiteralPath $camera.Source -Destination $staged -Force
                if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $camera.Hash) {
                    Remove-Item -LiteralPath $staged -Force
                    exit 2
                }
                Move-Item -LiteralPath $staged -Destination $target -Force
                $staged = $null
                $p = Start-Process -FilePath $camera.Regsvr32 -ArgumentList '/s', ('"' + $target + '"') -Wait -PassThru
                if ($p.ExitCode -ne 0) { exit $p.ExitCode }
                Get-ChildItem -LiteralPath $dir -Filter ($camera.Name + '-*.dll') |
                    Where-Object { $_.FullName -ne $target } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            }
            exit 0
        } catch {
            if ($staged) { Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue }
            exit 1
        }
        """;

    public void Dispose() => Stop();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256Hex();

    private static string EmbeddedHash(string key) =>
        typeof(VirtualCamera).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value is { } hash && Sha256Hex().IsMatch(hash)
            ? hash.ToUpperInvariant()
            : "";

    private static void RequireHash(string hash)
    {
        if (!Sha256Hex().IsMatch(hash))
            throw new ArgumentException("Not a SHA-256 hash.", nameof(hash));
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static bool HasHash(string path, string expected)
    {
        try
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
