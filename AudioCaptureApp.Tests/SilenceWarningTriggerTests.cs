using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 録音中に無音が続いたときの警告の判定器（T210 / REQ-REC-14 / REQ-CFG-16）。
/// </summary>
public class SilenceWarningTriggerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    /// <summary>既定の 60 秒（1,200 tick）/ −50 dB。</summary>
    private static SilenceWarningTrigger NewTrigger() => new(SilenceWarningOptions.Default);

    /// <summary>指定回数だけ観測し、発火した回数を返す。</summary>
    private static int Feed(
        SilenceWarningTrigger trigger, double micDb, double loopbackDb, int ticks, bool isRecording = true)
    {
        int fired = 0;
        for (int i = 0; i < ticks; i++)
        {
            if (trigger.Observe(micDb, loopbackDb, Tick, isRecording))
            {
                fired++;
            }
        }
        return fired;
    }

    [Fact]
    public void Observe_BothSilentForDuration_FiresOnceAtDuration()
    {
        var trigger = NewTrigger();

        // 59.95 秒ではまだ出さない。60 秒ちょうどで 1 回
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1199));
        Assert.True(trigger.Observe(-60.0, -60.0, Tick, isRecording: true));
    }

    [Fact]
    public void Observe_SilenceContinues_DoesNotFireAgain()
    {
        // 10/30 の録音のように 73 分無音が続いても、出すのは 1 回だけ
        var trigger = NewTrigger();

        Assert.Equal(1, Feed(trigger, -60.0, -60.0, 1200 * 73));
    }

    [Fact]
    public void Observe_SoundReturns_RearmsForNextSilence()
    {
        var trigger = NewTrigger();
        Assert.Equal(1, Feed(trigger, -60.0, -60.0, 1200));

        // 音が戻ったら次の無音で改めて 60 秒数える
        Assert.Equal(0, Feed(trigger, -20.0, -60.0, 1));
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1199));
        Assert.Equal(1, Feed(trigger, -60.0, -60.0, 1));
    }

    [Fact]
    public void Observe_ShortSoundInsideSilence_RestartsCount()
    {
        // 途切れずに続いた無音だけを数える。50 秒の無音の後に音があれば 0 からやり直す
        var trigger = NewTrigger();

        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1000));
        Assert.Equal(0, Feed(trigger, -45.0, -60.0, 1));
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1199));
    }

    [Theory]
    [InlineData(-20.0, -60.0)]   // マイクだけ鳴っている
    [InlineData(-60.0, -20.0)]   // スピーカーだけ鳴っている（自分は聞いているだけ）
    [InlineData(-50.0, -60.0)]   // しきい値ちょうどは無音ではない
    public void Observe_EitherSourceAtOrAboveThreshold_NeverFires(double micDb, double loopbackDb)
    {
        var trigger = NewTrigger();

        Assert.Equal(0, Feed(trigger, micDb, loopbackDb, 1200 * 2));
    }

    [Fact]
    public void Observe_NotRecording_DoesNotCountAndResets()
    {
        var trigger = NewTrigger();

        // 録音していない間（停止処理中を含む）は数えない
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1200 * 2, isRecording: false));

        // 録音の途中で止めて始め直したら、0 から数える
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1000));
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1, isRecording: false));
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1199));
        Assert.Equal(1, Feed(trigger, -60.0, -60.0, 1));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Observe_SecondsZeroOrLess_Disabled(double seconds)
    {
        var options = new SilenceWarningOptions(seconds, -50.0);
        var trigger = new SilenceWarningTrigger(options);

        Assert.False(options.Enabled);
        Assert.Equal(0, Feed(trigger, -60.0, -60.0, 1200 * 5));
    }

    [Fact]
    public void Options_Defaults_MatchMeasuredValues()
    {
        // 普通に録れた会議で −50 dB 未満が続いた最長は 14.1 秒（REQ-REC-14 の実測）
        var options = SilenceWarningOptions.Default;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Duration);
        Assert.Equal(-50.0, options.ThresholdDb);
    }

    [Theory]
    [InlineData(5.0, -50.0, 10.0, -50.0)]                       // 短すぎる秒数は 10 秒へ
    [InlineData(99999.0, -50.0, 3600.0, -50.0)]                 // 長すぎる秒数は 3600 秒へ
    [InlineData(60.0, -60.0, 60.0, -59.0)]                      // −60 は「未満」が成り立たないので −59 へ
    [InlineData(60.0, 0.0, 60.0, -20.0)]                        // 大きすぎるしきい値は −20 へ
    [InlineData(double.NaN, double.PositiveInfinity, 60.0, -50.0)] // 非有限値は既定へ
    public void Options_ClampsHandEditedValues(
        double seconds, double thresholdDb, double expectedSeconds, double expectedThresholdDb)
    {
        var options = new SilenceWarningOptions(seconds, thresholdDb);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.Duration);
        Assert.Equal(expectedThresholdDb, options.ThresholdDb);
    }

    [Fact]
    public void AppSettings_Defaults_EnableWarning()
    {
        // 既存の settings.json にキーが無ければ既定で有効（REQ-CFG-16）
        var settings = new AppSettings();

        Assert.Equal(60.0, settings.SilenceWarningSeconds);
        Assert.Equal(-50.0, settings.SilenceWarningThresholdDb);
    }

    [Fact]
    public void SilenceWarningMessage_ShowsSecondsAndThatRecordingContinues()
    {
        var message = MainViewModel.SilenceWarningMessage(TimeSpan.FromSeconds(60));

        Assert.Contains("60 秒以上", message, StringComparison.Ordinal);
        Assert.Contains("録音は続いています", message, StringComparison.Ordinal);
    }
}