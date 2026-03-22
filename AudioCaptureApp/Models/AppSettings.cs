using System.IO;

namespace AudioCaptureApp.Models;

public class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioCapture");

    public string? LastSelectedDeviceId { get; set; }
    public string? LastSelectedLoopbackDeviceId { get; set; }

    public bool TranscriptionEnabled { get; set; }
    public string WhisperModelPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioCaptureApp", "models", "ggml-small.bin");
}