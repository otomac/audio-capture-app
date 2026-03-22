using AudioCaptureApp.Services;
using NAudio.Wave;

namespace AudioCaptureApp.Tests;

public class AudioCaptureServiceTests
{
    // --- BytesToFloats ---

    [Fact]
    public void BytesToFloats_IeeeFloat32_ConvertsCorrectly()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        float[] expected = [0.5f, -0.25f, 1.0f];
        var buffer = new byte[expected.Length * 4];
        Buffer.BlockCopy(expected, 0, buffer, 0, buffer.Length);

        var result = AudioCaptureService.BytesToFloats(buffer, buffer.Length, format);

        Assert.NotNull(result);
        Assert.Equal(expected.Length, result.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], result[i]);
    }

    [Fact]
    public void BytesToFloats_Pcm16bit_ConvertsToNormalizedRange()
    {
        var format = new WaveFormat(44100, 16, 1);
        // 32767 (max positive) → ~1.0f, -32768 (max negative) → -1.0f
        short[] samples = [32767, -32768, 0, 16384];
        var buffer = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var result = AudioCaptureService.BytesToFloats(buffer, buffer.Length, format);

        Assert.NotNull(result);
        Assert.Equal(samples.Length, result.Length);
        Assert.True(Math.Abs(result[0] - 1.0f) < 0.001f); // 32767/32768 ≈ 1.0
        Assert.Equal(-1.0f, result[1]);                      // -32768/32768 = -1.0
        Assert.Equal(0.0f, result[2]);                        // 0/32768 = 0.0
        Assert.True(Math.Abs(result[3] - 0.5f) < 0.001f);   // 16384/32768 = 0.5
    }

    [Fact]
    public void BytesToFloats_UnsupportedFormat_ReturnsNull()
    {
        var format = new WaveFormat(44100, 24, 1);
        var buffer = new byte[12];

        var result = AudioCaptureService.BytesToFloats(buffer, buffer.Length, format);

        Assert.Null(result);
    }

    [Fact]
    public void BytesToFloats_EmptyBuffer_ReturnsEmptyArray()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var buffer = new byte[0];

        var result = AudioCaptureService.BytesToFloats(buffer, 0, format);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // --- CalculatePeak ---

    [Fact]
    public void CalculatePeak_IeeeFloat32_FindsMaxAbsValue()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        float[] samples = [0.1f, -0.8f, 0.3f, 0.5f];
        var buffer = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var peak = AudioCaptureService.CalculatePeak(buffer, buffer.Length, format);

        Assert.Equal(0.8f, peak, precision: 5);
    }

    [Fact]
    public void CalculatePeak_IeeeFloat32_SilenceReturnsZero()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        var buffer = new byte[16]; // all zeros

        var peak = AudioCaptureService.CalculatePeak(buffer, buffer.Length, format);

        Assert.Equal(0.0f, peak);
    }

    [Fact]
    public void CalculatePeak_IeeeFloat32_NegativeSamplesDetected()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        float[] samples = [-0.9f, -0.1f];
        var buffer = new byte[samples.Length * 4];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var peak = AudioCaptureService.CalculatePeak(buffer, buffer.Length, format);

        Assert.Equal(0.9f, peak, precision: 5);
    }

    [Fact]
    public void CalculatePeak_Pcm16bit_FindsMaxAbsValue()
    {
        var format = new WaveFormat(44100, 16, 1);
        short[] samples = [100, -200, 150];
        var buffer = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var peak = AudioCaptureService.CalculatePeak(buffer, buffer.Length, format);

        // Expected: 200 / 32768 ≈ 0.006104
        Assert.Equal(200f / 32768f, peak, precision: 5);
    }

    [Fact]
    public void CalculatePeak_Pcm16bit_MaxValueApproachesOne()
    {
        var format = new WaveFormat(44100, 16, 1);
        short[] samples = [32767];
        var buffer = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

        var peak = AudioCaptureService.CalculatePeak(buffer, buffer.Length, format);

        Assert.True(peak > 0.999f && peak <= 1.0f);
    }
}
