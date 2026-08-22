using System.Text.Json;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new AppSettings();

        Assert.Contains("AudioCapture", settings.OutputFolder, StringComparison.Ordinal);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.Null(settings.LastSelectedLoopbackDeviceId);
        Assert.False(settings.TranscriptionEnabled);
        Assert.Contains("ggml-small.bin", settings.WhisperModelPath, StringComparison.Ordinal);
        Assert.True(settings.UseGpuForTranscription);
        Assert.Equal(0.01, settings.SilenceRmsThreshold);
        Assert.Equal(2.0, settings.SilenceMergeGapSeconds);
        Assert.Equal(0.2, settings.VoicedPaddingSeconds);
        // 話者ダイアライゼーションは既定で無効（REQ-TRX-DIA-03）。
        Assert.False(settings.SpeakerDiarizationEnabled);
        Assert.Contains("diarization", settings.SpeakerSegmentationModelPath, StringComparison.Ordinal);
        Assert.Contains("diarization", settings.SpeakerEmbeddingModelPath, StringComparison.Ordinal);
        Assert.Equal(0.5, settings.SpeakerClusteringThreshold);
        // 話者数は既定で未指定。固定値を既定にしてはならない（REQ-TRX-DIA-07）。
        Assert.Null(settings.KnownSpeakerCount);
        Assert.Equal(1, settings.SpeakerDiarizationThreads);
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var original = new AppSettings
        {
            OutputFolder = @"C:\test\output",
            LastSelectedDeviceId = "device-123",
            LastSelectedLoopbackDeviceId = "loopback-456",
            TranscriptionEnabled = true,
            WhisperModelPath = @"C:\models\test.bin",
            UseGpuForTranscription = false,
            SilenceRmsThreshold = 0.02,
            SilenceMergeGapSeconds = 1.5,
            VoicedPaddingSeconds = 0.3,
            SpeakerDiarizationEnabled = true,
            SpeakerSegmentationModelPath = @"C:\models\seg.onnx",
            SpeakerEmbeddingModelPath = @"C:\models\emb.onnx",
            SpeakerClusteringThreshold = 0.7,
            KnownSpeakerCount = 3,
            SpeakerDiarizationThreads = 4
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.OutputFolder, deserialized.OutputFolder);
        Assert.Equal(original.LastSelectedDeviceId, deserialized.LastSelectedDeviceId);
        Assert.Equal(original.LastSelectedLoopbackDeviceId, deserialized.LastSelectedLoopbackDeviceId);
        Assert.Equal(original.TranscriptionEnabled, deserialized.TranscriptionEnabled);
        Assert.Equal(original.WhisperModelPath, deserialized.WhisperModelPath);
        Assert.Equal(original.UseGpuForTranscription, deserialized.UseGpuForTranscription);
        Assert.Equal(original.SilenceRmsThreshold, deserialized.SilenceRmsThreshold);
        Assert.Equal(original.SilenceMergeGapSeconds, deserialized.SilenceMergeGapSeconds);
        Assert.Equal(original.VoicedPaddingSeconds, deserialized.VoicedPaddingSeconds);
        Assert.Equal(original.SpeakerDiarizationEnabled, deserialized.SpeakerDiarizationEnabled);
        Assert.Equal(original.SpeakerSegmentationModelPath, deserialized.SpeakerSegmentationModelPath);
        Assert.Equal(original.SpeakerEmbeddingModelPath, deserialized.SpeakerEmbeddingModelPath);
        Assert.Equal(original.SpeakerClusteringThreshold, deserialized.SpeakerClusteringThreshold);
        Assert.Equal(original.KnownSpeakerCount, deserialized.KnownSpeakerCount);
        Assert.Equal(original.SpeakerDiarizationThreads, deserialized.SpeakerDiarizationThreads);
    }

    [Fact]
    public void JsonDeserialization_MissingFields_UsesDefaults()
    {
        var json = "{}";

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(settings);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.False(settings.TranscriptionEnabled);
        Assert.True(settings.UseGpuForTranscription);
        // 既存の settings.json には無音カットの 3 キーが無い。0.0 に束縛されると
        // 無音カットが全ユーザーで無効化されるため、既定値が残ることを固定する。
        Assert.Equal(0.01, settings.SilenceRmsThreshold);
        Assert.Equal(2.0, settings.SilenceMergeGapSeconds);
        Assert.Equal(0.2, settings.VoicedPaddingSeconds);
        // 既存の settings.json には話者ダイアライゼーションのキーも無い。
        // 既定が無効であること（勝手に有効化されないこと）を固定する。
        Assert.False(settings.SpeakerDiarizationEnabled);
        Assert.Equal(0.5, settings.SpeakerClusteringThreshold);
        Assert.Equal(1, settings.SpeakerDiarizationThreads);
    }
}