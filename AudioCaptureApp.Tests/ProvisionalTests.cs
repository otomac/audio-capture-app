using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

/// <summary>速報（T186 / REQ-TRX-LIVE-15〜17・REQ-CFG-12）の純粋関数と調整値。</summary>
public class ProvisionalTests
{
    // --- audio_ctx (REQ-TRX-LIVE-17 / T182 の E3) ---

    [Theory]
    [InlineData(0.0, 128)]   // (0 + 1) × 50 = 50 → 128
    [InlineData(1.0, 128)]   // 100 → 128
    [InlineData(2.0, 256)]   // 150 → 256
    [InlineData(8.0, 512)]   // 450 → 512
    [InlineData(29.0, 1500)] // 1500 → 1536 → 上限 1500
    public void AudioContextFor_RoundsUpToStepsOf128WithinLimit(double seconds, int expected)
    {
        Assert.Equal(expected, TranscriptionService.AudioContextFor(seconds));
    }

    [Fact]
    public void AudioContextFor_IsAlwaysLongerThanThePhrase()
    {
        // この値より長い音声は黙って切り捨てられるので、句（最大 25 秒）＋ 余裕を必ず覆う
        for (double seconds = 1.0; seconds <= 25.0; seconds += 0.1)
        {
            Assert.True(TranscriptionService.AudioContextFor(seconds) >= seconds * 50);
        }
    }

    // --- 間隔 (REQ-TRX-LIVE-16) ---

    [Theory]
    [InlineData(0.3, 1.0, 1.0)]  // 速い → 下限の 1 秒
    [InlineData(2.5, 1.0, 2.5)]  // 処理に 2.5 秒 → 2.5 秒に伸びる
    [InlineData(10.0, 1.0, 4.0)] // 上限 4 秒
    [InlineData(0.8, 0.5, 1.6)]  // duty の上限 0.5 → 使った時間の 2 倍
    public void NextProvisionalInterval_StretchesByDutyCap(double busy, double dutyCap, double expected)
    {
        var options = new ProvisionalOptions(1.0, dutyCap, 4, ProvisionalSources.Speaker, 8.0);

        Assert.Equal(expected, TranscriptionService.NextProvisionalInterval(busy, options), precision: 6);
    }

    // --- 幻聴の除去 (REQ-TRX-LIVE-17) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("。、")]
    [InlineData("ご視聴ありがとうございました")]
    [InlineData("ご視聴ありがとうございました。")]
    [InlineData("お疲れ様でした")]
    [InlineData("あああああああああ")]
    [InlineData("はいはいはいはい")]
    [InlineData("そうですそうですそうですそうです")]
    public void SanitizeProvisionalText_DropsHallucinations(string? text)
    {
        Assert.Null(TranscriptionService.SanitizeProvisionalText(text));
    }

    [Theory]
    [InlineData(" 今日は会議の議題を確認します ", "今日は会議の議題を確認します")]
    [InlineData("ありがとうございました、では次に進みます", "ありがとうございました、では次に進みます")]
    [InlineData("はい", "はい")]
    public void SanitizeProvisionalText_KeepsNormalSentences(string text, string expected)
    {
        Assert.Equal(expected, TranscriptionService.SanitizeProvisionalText(text));
    }

    // --- 調整値 (REQ-CFG-12) ---

    [Fact]
    public void ProvisionalOptions_Default_MatchesMeasuredSettings()
    {
        var options = ProvisionalOptions.Default;

        Assert.Equal(1.0, options.IntervalSeconds);
        Assert.Equal(1.0, options.DutyCap);
        Assert.Equal(4, options.Threads);
        Assert.Equal(ProvisionalSources.Speaker, options.Sources);
        Assert.Equal(8.0, options.MaxPhraseSeconds);
    }

    [Fact]
    public void ProvisionalOptions_OutOfRange_IsClamped()
    {
        var options = new ProvisionalOptions(0.1, 5.0, 100, ProvisionalSources.Both, 100);

        Assert.Equal(0.5, options.IntervalSeconds);
        Assert.Equal(1.0, options.DutyCap);
        Assert.Equal(16, options.Threads);
        Assert.Equal(25.0, options.MaxPhraseSeconds);
    }

    [Fact]
    public void ProvisionalOptions_NonFiniteAndUnknown_FallBackToDefaults()
    {
        var options = new ProvisionalOptions(double.NaN, double.PositiveInfinity, 0, (ProvisionalSources)99, double.NaN);

        Assert.Equal(1.0, options.IntervalSeconds);
        Assert.Equal(1.0, options.DutyCap);
        Assert.Equal(1, options.Threads);
        Assert.Equal(ProvisionalSources.Speaker, options.Sources);
        Assert.Equal(8.0, options.MaxPhraseSeconds);
    }

    [Theory]
    [InlineData("speaker", ProvisionalSources.Speaker)]
    [InlineData("Both", ProvisionalSources.Both)]
    [InlineData(" MIC ", ProvisionalSources.Mic)]
    [InlineData("loopback", ProvisionalSources.Speaker)]
    [InlineData("", ProvisionalSources.Speaker)]
    [InlineData(null, ProvisionalSources.Speaker)]
    public void ParseSources_UnknownFallsBackToSpeaker(string? value, ProvisionalSources expected)
    {
        Assert.Equal(expected, ProvisionalOptions.ParseSources(value));
    }

    [Theory]
    [InlineData(ProvisionalSources.Speaker, AudioSourceType.Speaker, true)]
    [InlineData(ProvisionalSources.Speaker, AudioSourceType.Mic, false)]
    [InlineData(ProvisionalSources.Mic, AudioSourceType.Mic, true)]
    [InlineData(ProvisionalSources.Mic, AudioSourceType.Speaker, false)]
    [InlineData(ProvisionalSources.Both, AudioSourceType.Mic, true)]
    [InlineData(ProvisionalSources.Both, AudioSourceType.Speaker, true)]
    public void ProvisionalTargets_SelectsSources(ProvisionalSources sources, AudioSourceType source, bool expected)
    {
        var options = new ProvisionalOptions(1.0, 1.0, 4, sources, 8.0);

        Assert.Equal(expected, options.Targets(source));
    }

    // --- 速報用モデルの選択の解決 (T186 D11) ---

    [Fact]
    public void FindWhisperModelByName_MatchesExactName()
    {
        var models = new[]
        {
            new WhisperModelEntry { ModelName = "small", ModelPath = @"C:\m\small.bin" },
            new WhisperModelEntry { ModelName = "base", ModelPath = @"C:\m\base.bin" }
        };

        Assert.Same(models[1], SettingsViewModel.FindWhisperModelByName(models, "base"));
        Assert.Null(SettingsViewModel.FindWhisperModelByName(models, "Base"));
        Assert.Null(SettingsViewModel.FindWhisperModelByName(models, "tiny"));
        Assert.Null(SettingsViewModel.FindWhisperModelByName(models, null));
    }
}