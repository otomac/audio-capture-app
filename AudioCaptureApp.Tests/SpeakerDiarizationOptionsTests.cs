using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>
/// settings.json は手書きされうるため、不正値でも壊れないことを固定する（REQ-TRX-DIA-07）。
/// 特に閾値は 0 以下だと sherpa-onnx の設定検証が失敗し、NULL ハンドル経由で
/// プロセスごと落ちるため、クランプはクラッシュの防壁を兼ねている。
/// </summary>
public class SpeakerDiarizationOptionsTests
{
    private static SpeakerDiarizationOptions Create(
        double threshold = 0.5, int? knownSpeakerCount = null, int numThreads = 1)
        => new("seg.onnx", "emb.onnx", threshold, knownSpeakerCount, numThreads);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1000.0)]
    public void ClusteringThreshold_InvalidValue_FallsBackToDefault(double value)
    {
        Assert.Equal(0.5, Create(threshold: value).ClusteringThreshold);
    }

    [Fact]
    public void ClusteringThreshold_IsAlwaysPositive()
    {
        // sherpa-onnx は「話者数未指定かつ閾値が正でない」設定で NULL ハンドルを返す。
        Assert.True(Create(threshold: 0.0).ClusteringThreshold > 0);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.7)]
    [InlineData(2.0)]
    public void ClusteringThreshold_ValidValue_IsKept(double value)
    {
        Assert.Equal(value, Create(threshold: value).ClusteringThreshold);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public void KnownSpeakerCount_NotPositive_BecomesNull(int? value)
    {
        // 未指定なら閾値による自動判定に倒す。固定の話者数を既定にしてはならない。
        Assert.Null(Create(knownSpeakerCount: value).KnownSpeakerCount);
    }

    [Fact]
    public void KnownSpeakerCount_Positive_IsKept()
    {
        Assert.Equal(4, Create(knownSpeakerCount: 4).KnownSpeakerCount);
    }

    [Fact]
    public void KnownSpeakerCount_AboveUpperBound_IsClamped()
    {
        Assert.Equal(100, Create(knownSpeakerCount: 100000).KnownSpeakerCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(4, 4)]
    [InlineData(999, 16)]
    public void NumThreads_IsClampedToUsableRange(int value, int expected)
    {
        Assert.Equal(expected, Create(numThreads: value).NumThreads);
    }
}