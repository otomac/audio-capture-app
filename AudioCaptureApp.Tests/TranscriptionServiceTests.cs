using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

public class TranscriptionServiceTests
{
    [Fact]
    public void IsSilent_AllZeros_ReturnsTrue()
    {
        var samples = new float[1000];

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_VerySmallSignal_ReturnsTrue()
    {
        // RMS < 0.01 threshold
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.005f; // constant 0.005 → RMS = 0.005 < 0.01

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_LoudSignal_ReturnsFalse()
    {
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.5f; // RMS = 0.5 >> 0.01

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_SingleLargeSampleAmongSilence_DependsOnRms()
    {
        // 1000 samples, only 1 is loud (0.5)
        // RMS = sqrt(0.25 / 1000) = sqrt(0.00025) ≈ 0.0158 > 0.01 → not silent
        var samples = new float[1000];
        samples[500] = 0.5f;

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_BelowThresholdRms_ReturnsTrue()
    {
        // Choose amplitude so RMS is just below 0.01
        // For constant signal: RMS = amplitude, so amplitude < 0.01
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.009f;

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_AboveThreshold_ReturnsFalse()
    {
        // RMS clearly above 0.01
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.02f;

        Assert.False(TranscriptionService.IsSilent(samples));
    }
}
