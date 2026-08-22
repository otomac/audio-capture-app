namespace AudioCaptureApp.Models;

/// <summary>
/// 文字起こしの 1 セグメント。<see cref="Start"/> / <see cref="End"/> は
/// **音声ファイル先頭からの相対時間**であり、REQ-TRX-FILE-10 の開始時刻は含まない。
/// 開始時刻は行を整形する直前に足す（含めてしまうと話者区間と同じ時間軸で比較できなくなる）。
/// </summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// 話者区間（REQ-TRX-DIA-05）。
/// </summary>
/// <param name="SpeakerId">
/// sherpa-onnx が返す 0 始まりの話者 ID。**その音声ファイルの中でのみ有効**であり、
/// 別のファイルの同じ値が同一人物であることを意味しない。表示は 1 始まりへ変換する
/// （<see cref="Services.TranscriptDiarizationMerger.FormatSpeaker"/>、REQ-TRX-DIA-06）。
/// </param>
public sealed record SpeakerSegment(TimeSpan Start, TimeSpan End, int SpeakerId);

/// <summary>
/// 話者を割り当てた文字起こしセグメント。
/// </summary>
/// <param name="SpeakerId">
/// 重複する話者区間が 1 つも無かった場合は <c>null</c>（＝話者不明）。
/// 直前のセグメントの話者を引き継いではならない（REQ-TRX-DIA-05）。
/// </param>
public sealed record SpeakerAttributedSegment(TimeSpan Start, TimeSpan End, int? SpeakerId, string Text);