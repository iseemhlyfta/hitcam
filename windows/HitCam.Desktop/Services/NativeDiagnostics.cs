namespace HitCam.Desktop.Services;

/// <summary>Turns a failure to load HitCamVCam.dll into something the user can act on.</summary>
internal static class NativeDiagnostics
{
    private const int ModuleNotFound = unchecked((int)0x8007007E);
    private const int ProcedureNotFound = unchecked((int)0x8007007F);
    private const int AlreadyExists = unchecked((int)0x800700B7);

    /// <summary>Why HitCamVCam.dll (or what it needs from Windows) could not be loaded.</summary>
    public static string ExplainLoadFailure(Exception error)
    {
        // Run straight from the zip, Explorer copies only HitCam.exe to a temporary folder.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "HitCamVCam.dll")))
            return Loc.NativeDllMissing;
        // Windows N editions ship without Media Foundation until the Media Feature Pack is installed.
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll")))
            return Loc.MediaFoundationMissing;
        return error.Message;
    }

    /// <summary>Text for a virtual camera start failure, or null when the HRESULT needs no explanation.</summary>
    public static string? ExplainCameraFailure(int hresult) => hresult switch
    {
        ModuleNotFound when !File.Exists(Path.Combine(AppContext.BaseDirectory, "HitCamVCam.dll")) => Loc.NativeDllMissing,
        ModuleNotFound when !File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll")) => Loc.MediaFoundationMissing,
        // mfsensorgroup.dll or its MFCreateVirtualCamera is missing: Windows 10 or older.
        ModuleNotFound or ProcedureNotFound => Loc.VirtualCameraNeedsWindows11,
        // Windows 10: another HitCam (another user's, or a second copy) already sends to the DirectShow camera.
        AlreadyExists => Loc.CameraBusy,
        _ => null,
    };
}
