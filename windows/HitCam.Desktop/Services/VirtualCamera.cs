using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
    NotInstalled,
    /// <summary>Installed, but the installed DLL differs from the one shipped with this build.</summary>
    Outdated,
    Installed,
}

/// <summary>
/// The "HitCam" camera other apps see. The media source DLL is copied to Program Files and registered
/// once with admin rights; after that the camera is added without elevation for as long as the app runs.
/// </summary>
public sealed class VirtualCamera : IDisposable
{
    public const string FriendlyName = "HitCam";
    private const string Clsid = "{FB88D813-B99A-4C03-8C22-A6C972983FCD}";
    private const int ErrorCancelled = 1223;

    private IntPtr _handle;

    private static string BundledDll => Path.Combine(AppContext.BaseDirectory, "HitCamVCam.dll");

    private static string InstalledDll =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HitCam", "HitCamVCam.dll");

    public bool IsRunning => _handle != IntPtr.Zero;

    public static CameraSetup GetSetup()
    {
        if (!File.Exists(BundledDll))
            return CameraSetup.Unavailable;

        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
        if (key?.GetValue(null) is not string registered || !File.Exists(registered))
            return CameraSetup.NotInstalled;
        return SameContent(registered, BundledDll) ? CameraSetup.Installed : CameraSetup.Outdated;
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
        var target = InstalledDll;
        // The camera service keeps the old DLL loaded; stopping it lets the file be replaced (it restarts on demand).
        var script = $"""
            $ErrorActionPreference = 'Stop'
            New-Item -ItemType Directory -Force -Path {Quote(Path.GetDirectoryName(target)!)} | Out-Null
            Stop-Service -Name FrameServer, FrameServerMonitor -Force -ErrorAction SilentlyContinue
            Copy-Item -LiteralPath {Quote(BundledDll)} -Destination {Quote(target)} -Force
            $p = Start-Process -FilePath regsvr32.exe -ArgumentList '/s', '"{target.Replace("'", "''")}"' -Wait -PassThru
            exit $p.ExitCode
            """;
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
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

    public void Dispose() => Stop();

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool SameContent(string first, string second)
    {
        try
        {
            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
