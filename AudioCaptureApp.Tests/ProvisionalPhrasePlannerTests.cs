using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 速報の区切り（T186 / REQ-TRX-LIVE-16）。T182 の計測ハーネスで合格した規則を、100ms 窓の有声判定の並びで確かめる。
/// </summary>
public class ProvisionalPhrasePlannerTests
{
    private const int W = ProvisionalPhrasePlanner.WindowSamples;

    /// <summary>'1' を有声窓、'0' を無音窓として 1 文字 100ms で足す。</summary>
    private static void Feed(ProvisionalPhrasePlanner planner, string pattern)
    {
        foreach (var c in pattern)
        {
            planner.AppendWindow(c == '1');
        }
    }

    private static string Voiced(int windows) => new('1', windows);

    private static string Silent(int windows) => new('0', windows);

    [Fact]
    public void Plan_NoVoice_ReturnsNothing()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(50));

        Assert.Empty(planner.Plan());
    }

    [Fact]
    public void Plan_ShortPhrase_WaitsUntilOneSecond()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(5) + Voiced(5));

        // 句は 0.3 秒から 1.0 秒まで（0.7 秒）しか無い → まだ呼ばない
        Assert.Empty(planner.Plan());

        Feed(planner, Voiced(3));

        var request = Assert.Single(planner.Plan());
        Assert.Equal(new ProvisionalRequest(1, (5 * W) - (2 * W), 13 * W, Closing: false), request);
    }

    [Fact]
    public void Plan_PhraseStartsLeadBeforeFirstVoicedWindow()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(20) + Voiced(12));

        var request = Assert.Single(planner.Plan());

        Assert.Equal((20 * W) - ProvisionalPhrasePlanner.LeadSamples, request.Start);
    }

    [Fact]
    public void Plan_VoiceAtVeryBeginning_StartsAtZero()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(12));

        Assert.Equal(0, Assert.Single(planner.Plan()).Start);
    }

    [Fact]
    public void Plan_TrailingSilenceOf600ms_ClosesPhrase()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(15) + Silent(6));

        var request = Assert.Single(planner.Plan());

        // 末尾の無音は渡さない
        Assert.Equal(new ProvisionalRequest(1, 0, 15 * W, Closing: true), request);
    }

    [Fact]
    public void Plan_TrailingSilenceOf500ms_KeepsPhraseOpen()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(15) + Silent(5));

        Assert.False(Assert.Single(planner.Plan()).Closing);
    }

    [Fact]
    public void Plan_LessThan300msNewVoice_DoesNotCallAgain()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(12));
        Assert.Single(planner.Plan());

        Feed(planner, Voiced(2));
        Assert.Empty(planner.Plan());

        Feed(planner, Voiced(1));
        var request = Assert.Single(planner.Plan());
        Assert.Equal(1, request.PhraseId);
        Assert.Equal(15 * W, request.End);
    }

    [Fact]
    public void Plan_ReachingMaxPhrase_ClosesAndContinuesFromCut()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(100));

        var requests = planner.Plan();

        Assert.Equal(
            [
                new ProvisionalRequest(1, 0, 80 * W, Closing: true),
                new ProvisionalRequest(2, 80 * W, 100 * W, Closing: false)
            ],
            requests);
    }

    [Fact]
    public void Plan_SilenceInsideUnplannedAudio_ClosesPhraseThere()
    {
        // 1 秒の間（ま）が呼び出しの合間に収まっても、句はその手前で閉じる（T186 D12）
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(12) + Silent(10) + Voiced(12));

        var first = planner.Plan()[0];

        Assert.Equal(new ProvisionalRequest(1, 0, 12 * W, Closing: true), first);
    }

    [Fact]
    public void Plan_NextPhraseAfterSilence_GetsNewId()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(12) + Silent(10) + Voiced(12));

        var requests = planner.Plan();

        Assert.Equal([1, 2], requests.Select(r => r.PhraseId));
        Assert.Equal((22 * W) - ProvisionalPhrasePlanner.LeadSamples, requests[1].Start);
    }

    // --- 確定済みの音声 (T186 D4) ---

    [Fact]
    public void SkipBefore_IdleMovesSearchStart()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        planner.SkipBefore(30 * W);
        Feed(planner, Voiced(45));

        var request = Assert.Single(planner.Plan());

        Assert.Equal(30 * W, request.Start);
    }

    [Fact]
    public void SkipBefore_WholePhraseConfirmed_ClosesPhrase()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(15) + Silent(3));
        Assert.Single(planner.Plan());

        // 確定パスが有声の終わり（1.5 秒）より後ろまで確定した → この句にはもう速報を出さない
        planner.SkipBefore(18 * W);
        Feed(planner, Voiced(12));
        var request = Assert.Single(planner.Plan());

        Assert.Equal(2, request.PhraseId);
        Assert.Equal(18 * W, request.Start);
    }

    [Fact]
    public void SkipBefore_StraddlingPhrase_IsKept()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Voiced(15));
        Assert.Single(planner.Plan());

        // 確定の位置が句の途中 → 句は続ける（字幕帯では確定の位置をまたぐ速報は残る）
        planner.SkipBefore(10 * W);
        Feed(planner, Voiced(5));
        var request = Assert.Single(planner.Plan());

        Assert.Equal(new ProvisionalRequest(1, 0, 20 * W, Closing: false), request);
    }

    // --- 写しを溜めない (T186 D3) ---

    [Fact]
    public void KeepFrom_DuringLongSilence_Advances()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(300));
        planner.Plan();

        Assert.Equal((300 * W) - ProvisionalPhrasePlanner.LeadSamples, planner.KeepFrom);
    }

    [Fact]
    public void KeepFrom_OpenPhrase_IsPhraseStart()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(20) + Voiced(12));
        planner.Plan();

        Assert.Equal((20 * W) - ProvisionalPhrasePlanner.LeadSamples, planner.KeepFrom);
    }

    [Fact]
    public void TrimBefore_KeepsPlanningAfterTrim()
    {
        var planner = new ProvisionalPhrasePlanner(8.0);
        Feed(planner, Silent(300));
        planner.Plan();
        planner.TrimBefore(planner.KeepFrom);
        Feed(planner, Voiced(12));

        var request = Assert.Single(planner.Plan());

        Assert.Equal(300, planner.KnownWindows - 12);
        Assert.Equal((300 * W) - ProvisionalPhrasePlanner.LeadSamples, request.Start);
    }
}