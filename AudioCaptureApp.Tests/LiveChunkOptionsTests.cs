using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>ライブの確定パスの区切りの調整値（REQ-CFG-15 / ADR-0009）。settings.json の手編集に耐えること。</summary>
public class LiveChunkOptionsTests
{
    [Fact]
    public void Default_IsTheConfigurationThatPassedT199()
    {
        Assert.Equal(6.0, LiveChunkOptions.Default.MaxChunkSeconds);
        Assert.Equal(0.8, LiveChunkOptions.Default.EndpointSilenceSeconds);
    }

    [Fact]
    public void InRangeValues_AreKept()
    {
        // ADR-0007 の構成（10 秒 / 2.0 秒）に戻せる
        var options = new LiveChunkOptions(10.0, 2.0);

        Assert.Equal(10.0, options.MaxChunkSeconds);
        Assert.Equal(2.0, options.EndpointSilenceSeconds);
    }

    [Theory]
    [InlineData(0.5, 2.0)]     // 短すぎる上限は 2 秒へ
    [InlineData(-3.0, 2.0)]
    [InlineData(60.0, 20.0)]   // 遅れているときの上限（20 秒）より長くしない
    public void MaxChunkSeconds_IsClamped(double input, double expected)
    {
        Assert.Equal(expected, new LiveChunkOptions(input, 0.8).MaxChunkSeconds);
    }

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(0.0, 0.0)]     // 0 は許す（有声のまま確定しないことは ChunkTakeCount が守る）
    [InlineData(50.0, 20.0)]
    public void EndpointSilenceSeconds_IsClamped(double input, double expected)
    {
        Assert.Equal(expected, new LiveChunkOptions(6.0, input).EndpointSilenceSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValues_FallBackToDefaults(double input)
    {
        var options = new LiveChunkOptions(input, input);

        Assert.Equal(LiveChunkOptions.Default.MaxChunkSeconds, options.MaxChunkSeconds);
        Assert.Equal(LiveChunkOptions.Default.EndpointSilenceSeconds, options.EndpointSilenceSeconds);
    }
}