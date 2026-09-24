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
    [InlineData(@"C:\Users\x\HitCam‘; Start-Process calc; ’\HitCamDShow.dll")]
    [InlineData(@"C:\Users\x\It's $(calc) `n ""here""\HitCamDShow.dll")]
    public void The_directshow_script_never_contains_the_paths_themselves(string path)
    {
        const string other = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
        var script = VirtualCamera.DirectShowInstallScript(path, Hash, path + "32", other);

        Assert.DoesNotContain("calc", script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(path)), script, StringComparison.Ordinal);
        Assert.Contains($"'{Hash}'", script, StringComparison.Ordinal);
        Assert.Contains($"'{other}'", script, StringComparison.Ordinal);
        // The 32-bit camera is registered for 32-bit apps.
        Assert.Contains(@"SysWOW64\regsvr32.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("@", script.Replace("@(", "", StringComparison.Ordinal).Replace("@{", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_directshow_script_refuses_a_bad_hash() =>
        Assert.Throws<ArgumentException>(() => VirtualCamera.DirectShowInstallScript(@"C:\a.dll", Hash, @"C:\b.dll", "0123' ; calc ; '"));

    [Theory]
    [InlineData("")]
    [InlineData("0123' ; calc ; '")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE'")]
    public void Anything_but_a_sha256_hash_is_refused(string hash) =>
        Assert.Throws<ArgumentException>(() => VirtualCamera.InstallScript(@"C:\a.dll", hash));
}
