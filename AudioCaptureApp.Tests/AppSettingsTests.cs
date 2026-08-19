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
            VoicedPaddingSeconds = 0.3
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
    }
}