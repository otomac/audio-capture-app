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
        byte[] buffer = [];

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

    // --- ApplySilenceTimeout (REQ-LVL-05) ---
    //
    // WASAPI のループバックキャプチャは再生音が無いとコールバックが発火しないため、
    // 最後のピーク値がメーターに残り続ける。一定時間データが来なければ 0 とみなす。

    [Fact]
    public void ApplySilenceTimeout_WithinTimeout_ReturnsPeak()
    {
        var result = AudioCaptureService.ApplySilenceTimeout(
            peak: 0.7f, lastDataTicks: 1_000, nowTicks: 1_150, timeoutMs: 200);

        Assert.Equal(0.7f, result);
    }

    [Fact]
    public void ApplySilenceTimeout_ExactlyAtTimeout_ReturnsPeak()
    {
        // 経過 == しきい値 はまだ「無音」と判定しない（超過して初めて 0 にする）
        var result = AudioCaptureService.ApplySilenceTimeout(
            peak: 0.7f, lastDataTicks: 1_000, nowTicks: 1_200, timeoutMs: 200);

        Assert.Equal(0.7f, result);
    }

    [Fact]
    public void ApplySilenceTimeout_BeyondTimeout_ReturnsZero()
    {
        var result = AudioCaptureService.ApplySilenceTimeout(
            peak: 0.7f, lastDataTicks: 1_000, nowTicks: 1_201, timeoutMs: 200);

        Assert.Equal(0.0f, result);
    }

    [Fact]
    public void ApplySilenceTimeout_NeverReceivedData_ReturnsZero()
    {
        // モニター開始直後 (_loopbackLastDataTicks == 0) でまだ 1 度もデータが来ていない状態
        var result = AudioCaptureService.ApplySilenceTimeout(
            peak: 0.7f, lastDataTicks: 0, nowTicks: 5_000_000, timeoutMs: 200);

        Assert.Equal(0.0f, result);
    }

    [Fact]
    public void ApplySilenceTimeout_ZeroPeakStaysZero()
    {
        var result = AudioCaptureService.ApplySilenceTimeout(
            peak: 0.0f, lastDataTicks: 1_000, nowTicks: 1_050, timeoutMs: 200);

        Assert.Equal(0.0f, result);
    }

    [Fact]
    public void ApplySilenceTimeout_UsesConfiguredTimeoutConstant()
    {
        // 定数が仕様（REQ-LVL-05 の 200ms）から動いていないことを固定する
        Assert.Equal(200, AudioCaptureService.LoopbackSilenceTimeoutMs);
    }
}