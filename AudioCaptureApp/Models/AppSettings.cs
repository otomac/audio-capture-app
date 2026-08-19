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

    public bool UseGpuForTranscription { get; set; } = true;

    /// <summary>有声とみなす 100ms 窓の RMS 下限（-40dB 相当）。UI からは変更できない。</summary>
    public double SilenceRmsThreshold { get; set; } = 0.01;

    /// <summary>これ未満の無音を挟む有声区間どうしは結合する（秒）。UI からは変更できない。</summary>
    public double SilenceMergeGapSeconds { get; set; } = 2.0;

    /// <summary>有声区間の前後に付ける余白（秒）。UI からは変更できない。</summary>
    public double VoicedPaddingSeconds { get; set; } = 0.2;
}