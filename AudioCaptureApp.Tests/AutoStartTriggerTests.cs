using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 録音の自動開始の判定器（T168 / REQ-REC-12 / REQ-CFG-10）。
/// </summary>
public class AutoStartTriggerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    /// <summary>閾値 −30 dB / 累積 1 秒 / クールダウン 2 秒 / 落ち込み許容なし。</summary>
    private static AutoStartTrigger NewTrigger(double cooldownSeconds = 2.0, double dipGrace = 0.0)
        => new(new AutoStartOptions(-30.0, 1.0, cooldownSeconds, dipGrace));

    /// <summary>指定回数だけ観測し、発火した回数を返す。</summary>
    private static int Feed(AutoStartTrigger trigger, double levelDb, int ticks, bool canStart = true)
    {
        int fired = 0;
        for (int i = 0; i < ticks; i++)
        {
            if (trigger.Observe(levelDb, Tick, canStart))
            {
                fired++;
            }
        }
        return fired;
    }

    [Fact]
    public void Observe_SustainedAboveThreshold_FiresOnce()
    {
        var trigger = NewTrigger();

        // 0.95 秒（19 tick）ではまだ発火しない。20 tick 目で発火し、その 1 回だけ
        Assert.Equal(0, Feed(trigger, -20.0, 19));
        Assert.True(trigger.Observe(-20.0, Tick, canStart: true));
        Assert.Equal(TimeSpan.Zero, trigger.Sustained);
    }

    [Fact]
    public void Observe_DropBelowThreshold_ResetsSustain()
    {
        // 咳・キーボード音の 1 発で起動しない: 途中で下回ったら 0 から数え直す
        var trigger = NewTrigger();

        Feed(trigger, -20.0, 15);
        Assert.False(trigger.Observe(-50.0, Tick, canStart: true));
        Assert.Equal(TimeSpan.Zero, trigger.Sustained);
        Assert.Equal(0, Feed(trigger, -20.0, 19));
    }

    [Fact]
    public void Observe_AtThreshold_CountsAsVoice()
    {
        // 閾値「以上」で数える
        var trigger = NewTrigger();

        Assert.Equal(1, Feed(trigger, -30.0, 20));
    }

    [Fact]
    public void Observe_CannotStart_DoesNotAccumulate()
    {
        // 録音中・モーダル中などは積算しない。条件が整った瞬間に以前の積算で即発火しない
        var trigger = NewTrigger();

        Assert.Equal(0, Feed(trigger, -20.0, 40, canStart: false));
        Assert.Equal(TimeSpan.Zero, trigger.Sustained);
        Assert.Equal(0, Feed(trigger, -20.0, 19, canStart: true));
    }

    [Fact]
    public void Observe_WithinCooldownAfterStop_DoesNotFire()
    {
        // 止めた直後に会話が続いていても、クールダウン（2 秒）の間は再開しない
        var trigger = NewTrigger(cooldownSeconds: 2.0);
        trigger.NotifyStopped();

        // 1.5 秒ぶん声が続いても発火しない（積算は 1 秒に達している）
        Assert.Equal(0, Feed(trigger, -20.0, 30));
    }

    [Fact]
    public void Observe_AfterCooldown_FiresAgain()
    {
        var trigger = NewTrigger(cooldownSeconds: 2.0);
        trigger.NotifyStopped();

        // 2 秒経過（40 tick 目）までは発火しない。積算（継続 1 秒）は既に満ちているので、
        // クールダウンが明けた 40 tick 目でそのまま発火する
        Assert.Equal(0, Feed(trigger, -20.0, 39));
        Assert.True(trigger.Observe(-20.0, Tick, canStart: true));
    }

    [Fact]
    public void Observe_ZeroCooldown_FiresRightAfterStop()
    {
        var trigger = NewTrigger(cooldownSeconds: 0.0);
        trigger.NotifyStopped();

        Assert.Equal(1, Feed(trigger, -20.0, 20));
    }

    [Fact]
    public void AutoStartOptions_ClampsAndFallsBack()
    {
        // 範囲外はクランプ、非有限値は既定へ（REQ-CFG-10）
        var clamped = new AutoStartOptions(-100.0, 0.1, 9999.0, 99.0);
        Assert.Equal(-60.0, clamped.ThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(0.5), clamped.Sustain);
        Assert.Equal(TimeSpan.FromSeconds(600), clamped.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(5), clamped.DipGrace);

        var fallback = new AutoStartOptions(
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN);
        Assert.Equal(AutoStartOptions.DefaultThresholdDb, fallback.ThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(AutoStartOptions.DefaultSustainSeconds), fallback.Sustain);
        Assert.Equal(TimeSpan.FromSeconds(AutoStartOptions.DefaultCooldownSeconds), fallback.Cooldown);
        Assert.Equal(TimeSpan.FromSeconds(AutoStartOptions.DefaultDipGraceSeconds), fallback.DipGrace);
    }

    [Fact]
    public void AutoStartOptions_Defaults_MatchMeasuredValues()
    {
        // 実測にもとづく既定（REQ-REC-12）: −15 dB / 累積 1 秒 / 落ち込み 0.3 秒
        Assert.Equal(-15.0, AutoStartOptions.Default.ThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(1), AutoStartOptions.Default.Sustain);
        Assert.Equal(TimeSpan.FromSeconds(0.3), AutoStartOptions.Default.DipGrace);
    }

    // --- 語の切れ目は許容する (T176 / REQ-REC-12) ---

    [Fact]
    public void Observe_ShortDipWithinGrace_KeepsAccumulating()
    {
        // 実測では −15 dB を連続で超えるのは中央値 0.10 秒しかない。
        // 落ち込みを許容しないと、話していても発火しない
        var trigger = NewTrigger(dipGrace: 0.3);

        // 0.5 秒発話 → 0.2 秒の切れ目 → 0.5 秒発話 で合計 1 秒
        Feed(trigger, -20.0, 10);
        Feed(trigger, -50.0, 4);
        Assert.Equal(TimeSpan.FromSeconds(0.5), trigger.Sustained);
        Assert.Equal(0, Feed(trigger, -20.0, 9));
        Assert.True(trigger.Observe(-20.0, Tick, canStart: true));
    }

    [Fact]
    public void Observe_DipLongerThanGrace_ResetsAccumulation()
    {
        // 咳やクリック音の 1 発では起動しない（その後の静けさで捨てる）
        var trigger = NewTrigger(dipGrace: 0.3);

        Feed(trigger, -20.0, 10);
        Feed(trigger, -50.0, 7);   // 0.35 秒（許容超え）
        Assert.Equal(TimeSpan.Zero, trigger.Sustained);
        Assert.Equal(0, Feed(trigger, -20.0, 19));
    }

    [Fact]
    public void Observe_CannotStart_ResetsBothCounters()
    {
        // 条件が整った瞬間に、以前の積算や落ち込みの途中経過で即発火しない
        var trigger = NewTrigger(dipGrace: 0.3);

        Feed(trigger, -20.0, 10);
        Assert.False(trigger.Observe(-20.0, Tick, canStart: false));
        Assert.Equal(TimeSpan.Zero, trigger.Sustained);
    }

    [Fact]
    public void CanAutoStartFor_AllConditions_AreRequired()
    {
        Assert.True(MainViewModel.CanAutoStartFor(enabled: true, isNotBusy: true, hasMicrophone: true, isModalDialogOpen: false));
        Assert.False(MainViewModel.CanAutoStartFor(enabled: false, isNotBusy: true, hasMicrophone: true, isModalDialogOpen: false));
        Assert.False(MainViewModel.CanAutoStartFor(enabled: true, isNotBusy: false, hasMicrophone: true, isModalDialogOpen: false));
        Assert.False(MainViewModel.CanAutoStartFor(enabled: true, isNotBusy: true, hasMicrophone: false, isModalDialogOpen: false));
        Assert.False(MainViewModel.CanAutoStartFor(enabled: true, isNotBusy: true, hasMicrophone: true, isModalDialogOpen: true));
    }
}