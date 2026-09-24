namespace HitCam.Vision.Hands;

/// <summary>
/// Where the hand models are: <c>models/hands</c> next to the app, or <c>%LOCALAPPDATA%/HitCam/models/hands</c>.
/// They are MediaPipe Hands (Google, Apache 2.0) as converted to ONNX by OpenCV Zoo; a subfolder keeps them out of
/// the object detection model list.
/// </summary>
public static class HandModelFiles
{
    public const string PalmFile = "palm_detection_mediapipe_2023feb.onnx";
    public const string LandmarkFile = "handpose_estimation_mediapipe_2023feb.onnx";
    public const string SubDirectory = "hands";

    public static IReadOnlyList<string> DefaultDirectories =>
        [.. ModelCatalog.DefaultDirectories.Select(d => Path.Combine(d, SubDirectory))];

    /// <summary>The first folder holding both files, or null.</summary>
    public static (string Palm, string Landmarks)? Find(IEnumerable<string>? directories = null)
    {
        foreach (var directory in directories ?? DefaultDirectories)
        {
            var palm = Path.Combine(directory, PalmFile);
            var landmarks = Path.Combine(directory, LandmarkFile);
            if (File.Exists(palm) && File.Exists(landmarks))
                return (palm, landmarks);
        }
        return null;
    }
}

/// <summary>The palm detector and the landmark model together, run on the frames of a <see cref="VisionFrame"/>.</summary>
public sealed class HandModels : IHandModels
{
    private readonly PalmDetector _palms;
    private readonly HandLandmarker _landmarks;

    private HandModels(PalmDetector palms, HandLandmarker landmarks)
    {
        _palms = palms;
        _landmarks = landmarks;
        // Both small models normally end up on the same provider; if not, the slower one is what matters.
        Provider = palms.Provider == landmarks.Provider ? palms.Provider : Detector.Cpu;
    }

    public string Provider { get; }

    /// <summary>Loads both models (DirectML first, see <see cref="Detector.Load"/>); throws if either cannot run.</summary>
    public static HandModels Load(string palmPath, string landmarkPath, ProviderPreference preference = ProviderPreference.Auto)
    {
        var palms = PalmDetector.Load(palmPath, preference);
        try
        {
            return new HandModels(palms, HandLandmarker.Load(landmarkPath, preference));
        }
        catch
        {
            palms.Dispose();
            throw;
        }
    }

    /// <summary>Loads the models from the default folders; <see cref="FileNotFoundException"/> if they are missing.</summary>
    public static HandModels LoadDefault() =>
        HandModelFiles.Find() is { } files
            ? Load(files.Palm, files.Landmarks)
            : throw new FileNotFoundException(
                $"{HandModelFiles.PalmFile} and {HandModelFiles.LandmarkFile} not found in {string.Join(", ", HandModelFiles.DefaultDirectories)}");

    public IReadOnlyList<Palm> DetectPalms(VisionFrame frame, float threshold) =>
        _palms.Detect(frame.Pixels, frame.Width, frame.Height, frame.Stride, threshold);

    public HandLandmarks Landmarks(VisionFrame frame, HandRoi roi) =>
        _landmarks.Run(frame.Pixels, frame.Width, frame.Height, frame.Stride, roi);

    public void Dispose()
    {
        _palms.Dispose();
        _landmarks.Dispose();
    }
}
