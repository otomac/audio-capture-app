namespace AudioCaptureApp.Models;

/// <summary>
/// セグメントの中で実際に発話が存在する時間帯（REQ-TRX-DIA-13）。
/// Whisper のトークン単位のタイムスタンプから作る。
/// </summary>
/// <remarks>
/// これが要るのは、Whisper のセグメントが実際の発話より長く伸びるためである。
/// REQ-TRX-08 の最小長パディングで足した無音や、発話が終わった後の余白まで含んだ範囲で
/// 重複長を測ると、その余白が隣の話者の時間帯にかかって誤った話者へ寄る。
/// </remarks>
public sealed record SpeechSpan(TimeSpan Start, TimeSpan End);

/// <summary>
/// 文字起こしの 1 セグメント。<see cref="Start"/> / <see cref="End"/> は
/// **音声ファイル先頭からの相対時間**であり、REQ-TRX-FILE-10 の開始時刻は含まない。
/// 開始時刻は行を整形する直前に足す（含めてしまうと話者区間と同じ時間軸で比較できなくなる）。
/// </summary>
/// <param name="SpeechSpans">
/// セグメント内で実際に発話が存在する時間帯（REQ-TRX-DIA-13）。話者の重複長はここだけで測る。
/// <c>null</c> または空なら <see cref="Start"/>〜<see cref="End"/> をそのまま使う（従来動作）。
/// </param>
public sealed record TranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    IReadOnlyList<SpeechSpan>? SpeechSpans = null);

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