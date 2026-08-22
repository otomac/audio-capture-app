using AudioCaptureApp.Models;
using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 文字起こしタイムラインと話者タイムラインの統合（REQ-TRX-DIA-05 / REQ-TRX-DIA-06）。
/// マージ規則は純粋関数なので、モデルファイルなしで境界条件を全部固定できる。
/// </summary>
public class TranscriptDiarizationMergerTests
{
    private static TranscriptSegment Text(double start, double end, string text = "テキスト")
        => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text);

    private static SpeakerSegment Speaker(double start, double end, int id)
        => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), id);

    // --- Case 1: 完全一致 ---

    [Fact]
    public void Merge_ExactOverlap_AssignsThatSpeaker()
    {
        var result = TranscriptDiarizationMerger.Merge(
            [Text(0, 5)],
            [Speaker(0, 5, 0)]);

        Assert.Equal(0, Assert.Single(result).SpeakerId);
    }

    // --- Case 2: 部分重複 — 重複時間の長い方を採る ---

    [Fact]
    public void Merge_PartialOverlap_AssignsLongerOverlapSpeaker()
    {
        // Transcript 1-5 に対し Speaker0 は 3 秒、Speaker1 は 1 秒しか重ならない。
        // 開始時刻だけを見ると Speaker0 が先だが、判定根拠はあくまで重複時間である。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(1, 5)],
            [Speaker(0, 4, 0), Speaker(4, 8, 1)]);

        Assert.Equal(0, Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void Merge_LaterSpeakerHasLongerOverlap_AssignsLaterSpeaker()
    {
        // 先に始まった話者が勝つわけではないことを確かめる（Case 2 の裏返し）。
        // Transcript 1-5: Speaker0 は 1 秒、Speaker1 は 3 秒。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(1, 5)],
            [Speaker(0, 2, 0), Speaker(2, 8, 1)]);

        Assert.Equal(1, Assert.Single(result).SpeakerId);
    }

    // --- Case 3: 話者境界 — 同点は決定的に解決する ---

    [Fact]
    public void Merge_EqualOverlap_AssignsSmallerSpeakerId()
    {
        // Transcript 3-7 は Speaker0（3-5）と Speaker1（5-7）にちょうど 2 秒ずつ重なる。
        // 同点のときは話者 ID が小さい方に倒す（要求仕様 §23 Case 3 / 決定 D5）。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(3, 7)],
            [Speaker(0, 5, 0), Speaker(5, 10, 1)]);

        Assert.Equal(0, Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void Merge_EqualOverlapWithSpeakersReversed_StillAssignsSmallerSpeakerId()
    {
        // 同点解決が「先に見つかった方」になっていないこと。入力順を入れ替えても結果は同じ。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(3, 7)],
            [Speaker(5, 10, 1), Speaker(0, 5, 0)]);

        Assert.Equal(0, Assert.Single(result).SpeakerId);
    }

    // --- Case 4: Diarization 結果なし ---

    [Fact]
    public void Merge_NoSpeakerSegments_ReturnsUnknown()
    {
        var result = TranscriptDiarizationMerger.Merge(
            [Text(0, 5)],
            []);

        Assert.Null(Assert.Single(result).SpeakerId);
    }

    // --- Case 5: 無音区間 — 直前の話者を引き継がない ---

    [Fact]
    public void Merge_InSilenceBetweenSpeakers_ReturnsUnknown()
    {
        // 5-6 は Speaker0（0-4）と Speaker1（8-10）のどちらとも重ならない。
        // 直前が Speaker0 だからといって引き継いではならない（REQ-TRX-DIA-05）。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(0, 4), Text(5, 6), Text(8, 10)],
            [Speaker(0, 4, 0), Speaker(8, 10, 1)]);

        Assert.Equal(0, result[0].SpeakerId);
        Assert.Null(result[1].SpeakerId);
        Assert.Equal(1, result[2].SpeakerId);
    }

    [Fact]
    public void Merge_ZeroLengthTranscript_ReturnsUnknown()
    {
        // 長さ 0 のセグメントはどの話者とも「重複時間 0」なので話者不明になる。
        // 境界に接しているだけの区間を重複と見なさないことの確認でもある。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(5, 5)],
            [Speaker(0, 5, 0), Speaker(5, 10, 1)]);

        Assert.Null(Assert.Single(result).SpeakerId);
    }

    // --- Case 6: 複数話者 — 再登場した話者の ID が保たれる ---

    [Fact]
    public void Merge_SameSpeakerReappears_KeepsSameId()
    {
        var result = TranscriptDiarizationMerger.Merge(
            [Text(0, 2), Text(3, 5), Text(6, 8)],
            [Speaker(0, 2, 0), Speaker(3, 5, 1), Speaker(6, 8, 0)]);

        Assert.Equal([0, 1, 0], result.Select(r => r.SpeakerId));
    }

    // --- Case 7: 不正なタイムスタンプは拒否する ---

    [Fact]
    public void Merge_TranscriptEndBeforeStart_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => TranscriptDiarizationMerger.Merge(
            [Text(5, 3)],
            [Speaker(0, 10, 0)]));

        Assert.Equal("transcriptSegments", ex.ParamName);
    }

    [Fact]
    public void Merge_SpeakerEndBeforeStart_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => TranscriptDiarizationMerger.Merge(
            [Text(0, 5)],
            [Speaker(10, 2, 0)]));

        Assert.Equal("speakerSegments", ex.ParamName);
    }

    // --- Case 8: 空入力 ---

    [Fact]
    public void Merge_EmptyInputs_ReturnsEmpty()
    {
        Assert.Empty(TranscriptDiarizationMerger.Merge([], []));
    }

    [Fact]
    public void Merge_EmptyTranscriptWithSpeakers_ReturnsEmpty()
    {
        Assert.Empty(TranscriptDiarizationMerger.Merge([], [Speaker(0, 5, 0)]));
    }

    // --- 出力の性質 ---

    [Fact]
    public void Merge_PreservesTranscriptOrderTextAndCount()
    {
        // 同一話者が続いても結合しない（決定 D6）。行数・順序・本文・時刻は入力のまま。
        var transcript = new[] { Text(0, 1, "今日は"), Text(1, 2, "よろしく"), Text(2, 3, "お願いします") };

        var result = TranscriptDiarizationMerger.Merge(transcript, [Speaker(0, 3, 0)]);

        Assert.Equal(3, result.Count);
        Assert.Equal(transcript.Select(t => t.Text), result.Select(r => r.Text));
        Assert.Equal(transcript.Select(t => t.Start), result.Select(r => r.Start));
        Assert.Equal(transcript.Select(t => t.End), result.Select(r => r.End));
    }

    [Fact]
    public void Merge_SpeakerSegmentsUnordered_SameResult()
    {
        // sherpa-onnx は時間順に返すが、それに依存した実装になっていないこと。
        var transcript = new[] { Text(0, 2), Text(3, 5), Text(6, 8) };
        var ordered = new[] { Speaker(0, 2, 0), Speaker(3, 5, 1), Speaker(6, 8, 2) };
        var shuffled = new[] { Speaker(6, 8, 2), Speaker(0, 2, 0), Speaker(3, 5, 1) };

        Assert.Equal(
            TranscriptDiarizationMerger.Merge(transcript, ordered).Select(r => r.SpeakerId),
            TranscriptDiarizationMerger.Merge(transcript, shuffled).Select(r => r.SpeakerId));
    }

    [Fact]
    public void Merge_TranscriptUnordered_AssignsPerSegmentInInputOrder()
    {
        // 内部で開始時刻順に走査しても、返す順序は入力どおりであること。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(6, 8, "あと"), Text(0, 2, "さき")],
            [Speaker(0, 2, 0), Speaker(6, 8, 1)]);

        Assert.Equal("あと", result[0].Text);
        Assert.Equal(1, result[0].SpeakerId);
        Assert.Equal("さき", result[1].Text);
        Assert.Equal(0, result[1].SpeakerId);
    }

    [Fact]
    public void Merge_OverlappingSpeakerSegments_PicksLongestOverlap()
    {
        // 話者区間どうしが重なっていても（同時発話）落とさずに比較する。
        var result = TranscriptDiarizationMerger.Merge(
            [Text(2, 6)],
            [Speaker(0, 3, 0), Speaker(1, 6, 1)]);

        Assert.Equal(1, Assert.Single(result).SpeakerId);
    }

    // --- 表示変換（REQ-TRX-DIA-06）---

    [Fact]
    public void FormatSpeaker_ZeroBasedId_FormatsAsOneBased()
    {
        Assert.Equal("話者1", TranscriptDiarizationMerger.FormatSpeaker(0));
        Assert.Equal("話者2", TranscriptDiarizationMerger.FormatSpeaker(1));
    }

    [Fact]
    public void FormatSpeaker_Null_FormatsAsUnknown()
    {
        Assert.Equal("話者不明", TranscriptDiarizationMerger.FormatSpeaker(null));
    }
}