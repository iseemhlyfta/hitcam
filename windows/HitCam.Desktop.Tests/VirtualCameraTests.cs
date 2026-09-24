using System.Text;
using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class VirtualCameraTests
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    // PowerShell treats typographic quotes as quotes too.
    [InlineData(@"C:\Users\x\HitCam‘; Start-Process calc; ’\HitCamVCam.dll")]
    [InlineData(@"C:\Users\x\It's $(calc) `n ""here""\HitCamVCam.dll")]
    public void The_elevated_script_never_contains_the_path_itself(string path)
    {
        var script = VirtualCamera.InstallScript(path, Hash);
        var template = VirtualCamera.InstallScript(@"C:\a.dll", Hash);

        Assert.DoesNotContain("calc", script, StringComparison.Ordinal);
        // Only the base64 token differs from any other install script.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        Assert.Equal(template.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(@"C:\a.dll")), encoded, StringComparison.Ordinal), script);
        Assert.Contains($"'{Hash}'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123' ; calc ; '")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE'")]
    public void Anything_but_a_sha256_hash_is_refused(string hash) =>
        Assert.Throws<ArgumentException>(() => VirtualCamera.InstallScript(@"C:\a.dll", hash));
}
