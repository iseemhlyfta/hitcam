using HitCam.Desktop.Services;

namespace HitCam.Desktop.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HitCam.Tests." + Guid.NewGuid().ToString("N"));
    private readonly string _previous = DiagnosticLog.FilePath;

    public DiagnosticLogTests() => DiagnosticLog.FilePath = Path.Combine(_directory, "logs", "hitcam.log");

    public void Dispose()
    {
        DiagnosticLog.FilePath = _previous;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Lines_are_written_through_with_a_timestamp()
    {
        DiagnosticLog.Write("first");
        DiagnosticLog.Write("second");

        var lines = File.ReadAllLines(DiagnosticLog.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("first", lines[0]);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} \[\d+\] second$", lines[1]);
    }

    [Fact]
    public void A_full_log_is_kept_as_the_previous_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticLog.FilePath)!);
        File.WriteAllText(DiagnosticLog.FilePath, new string('x', (int)DiagnosticLog.MaxBytes + 10));

        DiagnosticLog.Write("fresh");

        Assert.EndsWith("fresh", File.ReadAllText(DiagnosticLog.FilePath).TrimEnd());
        Assert.True(File.Exists(Path.ChangeExtension(DiagnosticLog.FilePath, ".1.log")));
    }

    [Fact]
    public void A_log_that_cannot_be_written_is_ignored()
    {
        DiagnosticLog.FilePath = Path.Combine(_directory, "file-not-folder");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DiagnosticLog.FilePath, "");
        DiagnosticLog.FilePath = Path.Combine(DiagnosticLog.FilePath, "hitcam.log");   // a folder that is a file

        DiagnosticLog.Write("lost");
    }
}
