using System.Text.Json;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new AppSettings();

        Assert.Contains("AudioCapture", settings.OutputFolder);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.Null(settings.LastSelectedLoopbackDeviceId);
        Assert.False(settings.TranscriptionEnabled);
        Assert.Contains("ggml-small.bin", settings.WhisperModelPath);
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
            WhisperModelPath = @"C:\models\test.bin"
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.OutputFolder, deserialized.OutputFolder);
        Assert.Equal(original.LastSelectedDeviceId, deserialized.LastSelectedDeviceId);
        Assert.Equal(original.LastSelectedLoopbackDeviceId, deserialized.LastSelectedLoopbackDeviceId);
        Assert.Equal(original.TranscriptionEnabled, deserialized.TranscriptionEnabled);
        Assert.Equal(original.WhisperModelPath, deserialized.WhisperModelPath);
    }

    [Fact]
    public void JsonDeserialization_MissingFields_UsesDefaults()
    {
        var json = "{}";

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(settings);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.False(settings.TranscriptionEnabled);
    }
}
