using HitCam.Vision.Hands;

namespace HitCam.Vision.Faces;

/// <summary>
/// Where the face models are: <c>models/faces</c> next to the app or in <c>%LOCALAPPDATA%/HitCam/models/faces</c>.
/// YuNet (MIT) and SFace (Apache 2.0) from OpenCV Zoo.
/// </summary>
public static class FaceModelFiles
{
    public const string DetectorFile = "face_detection_yunet_2023mar.onnx";
    public const string RecognizerFile = "face_recognition_sface_2021dec.onnx";
    public const string SubDirectory = "faces";

    public static IReadOnlyList<string> DefaultDirectories =>
        [.. ModelCatalog.DefaultDirectories.Select(d => Path.Combine(d, SubDirectory))];

    /// <summary>The first folder holding both files, or null.</summary>
    public static (string Detector, string Recognizer)? Find(IEnumerable<string>? directories = null)
    {
        foreach (var directory in directories ?? DefaultDirectories)
        {
            var detector = Path.Combine(directory, DetectorFile);
            var recognizer = Path.Combine(directory, RecognizerFile);
            if (File.Exists(detector) && File.Exists(recognizer))
                return (detector, recognizer);
        }
        return null;
    }
}

/// <summary>The face detector and the recognizer together, on the frames of a <see cref="VisionFrame"/>.</summary>
public sealed class FaceModels : IFaceModels
{
    private readonly FaceDetector _detector;
    private readonly FaceRecognizer _recognizer;

    private FaceModels(FaceDetector detector, FaceRecognizer recognizer)
    {
        _detector = detector;
        _recognizer = recognizer;
        Provider = detector.Provider == recognizer.Provider ? detector.Provider : Detector.Cpu;
    }

    public string Provider { get; }

    public static FaceModels Load(string detectorPath, string recognizerPath, ProviderPreference preference = ProviderPreference.Auto)
    {
        var detector = FaceDetector.Load(detectorPath, preference);
        try
        {
            return new FaceModels(detector, FaceRecognizer.Load(recognizerPath, preference));
        }
        catch
        {
            detector.Dispose();
            throw;
        }
    }

    /// <summary>Loads the models from the default folders; <see cref="FileNotFoundException"/> if they are missing.</summary>
    public static FaceModels LoadDefault() =>
        FaceModelFiles.Find() is { } files
            ? Load(files.Detector, files.Recognizer)
            : throw new FileNotFoundException(
                $"{FaceModelFiles.DetectorFile} and {FaceModelFiles.RecognizerFile} not found in {string.Join(", ", FaceModelFiles.DefaultDirectories)}");

    public IReadOnlyList<DetectedFace> Detect(VisionFrame frame) =>
        _detector.Detect(frame.Pixels, frame.Width, frame.Height, frame.Stride);

    public float[] Fingerprint(VisionFrame frame, DetectedFace face) =>
        _recognizer.Fingerprint(frame.Pixels, frame.Width, frame.Height, frame.Stride, face.Landmarks);

    public void Dispose()
    {
        _detector.Dispose();
        _recognizer.Dispose();
    }
}
