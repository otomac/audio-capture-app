using AudioCaptureApp.Services;
using AudioCaptureApp.ViewModels;
using Entry = AudioCaptureApp.ViewModels.LiveTranscriptViewModel.ProvisionalEntry;

namespace AudioCaptureApp.Tests;

/// <summary>字幕帯の反映規則（T187 / REQ-LIVEVIEW-10）。</summary>
public class LiveTranscriptProvisionalTests
{
    private const long Session = 7;
    private const int Max = LiveTranscriptViewModel.MaxProvisionalLines;

    private static ProvisionalUpdate Update(
        int phrase, long start, long end, string text = "本文",
        AudioSourceType source = AudioSourceType.Speaker, long session = Session)
        => new(session, source, source == AudioSourceType.Speaker ? "スピーカー" : "マイク", phrase, start, end, false, text);

    private static bool Apply(List<Entry> entries, Dictionary<AudioSourceType, long> confirmed, ProvisionalUpdate update)
        => LiveTranscriptViewModel.ApplyProvisionalUpdate(entries, confirmed, Session, update, Max);

    [Fact]
    public void ApplyProvisionalUpdate_SamePhrase_OverwritesInPlace()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long>();
        Apply(entries, confirmed, Update(1, 0, 16000, "こんに"));
        Apply(entries, confirmed, Update(2, 32000, 48000, "次の句"));

        Assert.True(Apply(entries, confirmed, Update(1, 0, 24000, "こんにちは")));

        Assert.Equal(["[スピーカー] こんにちは", "[スピーカー] 次の句"], entries.Select(e => e.Display));
        Assert.Equal(24000, entries[0].EndPosition);
    }

    [Fact]
    public void ApplyProvisionalUpdate_NewPhrase_AppendsAndKeepsAtMostFour()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long>();

        for (int phrase = 1; phrase <= 5; phrase++)
        {
            Apply(entries, confirmed, Update(phrase, phrase * 16000L, (phrase + 1) * 16000L, $"句{phrase}"));
        }

        Assert.Equal([2, 3, 4, 5], entries.Select(e => e.PhraseId));
    }

    [Fact]
    public void ApplyProvisionalUpdate_OtherSession_IsIgnored()
    {
        var entries = new List<Entry>();

        bool applied = Apply(entries, [], Update(1, 0, 16000, session: Session - 1));

        Assert.False(applied);
        Assert.Empty(entries);
    }

    [Fact]
    public void ApplyProvisionalUpdate_NoSession_IsIgnored()
    {
        // 録音していない（停止の完了後）。遅れて届いた通知を出さない
        var entries = new List<Entry>();

        bool applied = LiveTranscriptViewModel.ApplyProvisionalUpdate(entries, [], null, Update(1, 0, 16000), Max);

        Assert.False(applied);
        Assert.Empty(entries);
    }

    [Fact]
    public void ApplyProvisionalUpdate_AlreadyConfirmed_IsIgnored()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long> { [AudioSourceType.Speaker] = 160000 };

        Assert.False(Apply(entries, confirmed, Update(3, 120000, 160000)));
        Assert.True(Apply(entries, confirmed, Update(4, 150000, 170000)));
        Assert.Single(entries);
    }

    [Fact]
    public void ApplyProvisionalUpdate_OtherSourceConfirmation_DoesNotAffect()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long> { [AudioSourceType.Mic] = 160000 };

        Assert.True(Apply(entries, confirmed, Update(1, 0, 16000)));
    }

    [Fact]
    public void ApplyChunkCompletion_RemovesConfirmedAndKeepsStraddling()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long>();
        Apply(entries, confirmed, Update(1, 0, 64000));        // 0〜4 秒: 確定位置（10 秒）より前
        Apply(entries, confirmed, Update(2, 96000, 176000));   // 6〜11 秒: 確定位置をまたぐ
        Apply(entries, confirmed, Update(3, 176000, 200000));  // 11 秒〜: 後ろ

        bool applied = LiveTranscriptViewModel.ApplyChunkCompletion(
            entries, confirmed, Session, new LiveChunkCompletion(Session, AudioSourceType.Speaker, 160000));

        Assert.True(applied);
        Assert.Equal([2, 3], entries.Select(e => e.PhraseId));
        Assert.Equal(160000, confirmed[AudioSourceType.Speaker]);
    }

    [Fact]
    public void ApplyChunkCompletion_PositionNeverGoesBack()
    {
        var confirmed = new Dictionary<AudioSourceType, long> { [AudioSourceType.Speaker] = 320000 };

        LiveTranscriptViewModel.ApplyChunkCompletion(
            [], confirmed, Session, new LiveChunkCompletion(Session, AudioSourceType.Speaker, 160000));

        Assert.Equal(320000, confirmed[AudioSourceType.Speaker]);
    }

    [Fact]
    public void ApplyChunkCompletion_OtherSource_IsUntouched()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long>();
        Apply(entries, confirmed, Update(1, 0, 16000, source: AudioSourceType.Mic));

        LiveTranscriptViewModel.ApplyChunkCompletion(
            entries, confirmed, Session, new LiveChunkCompletion(Session, AudioSourceType.Speaker, 160000));

        Assert.Single(entries);
    }

    [Fact]
    public void ApplyChunkCompletion_OtherSession_IsIgnored()
    {
        var entries = new List<Entry>();
        var confirmed = new Dictionary<AudioSourceType, long>();
        Apply(entries, confirmed, Update(1, 0, 16000));

        bool applied = LiveTranscriptViewModel.ApplyChunkCompletion(
            entries, confirmed, Session, new LiveChunkCompletion(Session + 1, AudioSourceType.Speaker, 160000));

        Assert.False(applied);
        Assert.Single(entries);
        Assert.Empty(confirmed);
    }

    [Fact]
    public void ProvisionalDisplay_HasLabelAndText()
    {
        Assert.Equal("[マイク] はい", LiveTranscriptViewModel.ProvisionalDisplay(Update(1, 0, 1, "はい", AudioSourceType.Mic)));
    }

    [Fact]
    public void SyncLines_ReplacesChangedAndTrimsExtra()
    {
        var target = new List<string> { "a", "b", "c" };

        LiveTranscriptViewModel.SyncLines(target, ["a", "B"]);

        Assert.Equal(["a", "B"], target);
    }

    [Fact]
    public void SyncLines_AppendsMissing()
    {
        var target = new List<string> { "a" };

        LiveTranscriptViewModel.SyncLines(target, ["a", "b", "c"]);

        Assert.Equal(["a", "b", "c"], target);
    }
}