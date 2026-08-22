using System.Globalization;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Services;

/// <summary>
/// 文字起こしのタイムラインと話者のタイムラインを突き合わせ、各文字起こしセグメントへ
/// 話者を割り当てる（REQ-TRX-DIA-05）。
/// </summary>
/// <remarks>
/// **音声認識も ONNX 推論も行わない純粋関数である。** 副作用を持たせないこと。
/// インターフェースを切らずに <c>internal static</c> にしているのは、実装が 1 つしかない抽象を
/// 作らないという方針（[ADR-0001] / [ADR-0003] 争点 3）による。差し替えではなくテストで守る。
/// <para>
/// **既知の制約（要求仕様 §13）:** 割り当ての単位は Whisper の**セグメント**である。
/// 1 つのセグメントの途中で話者が切り替わる場合（「そうですね。では次の件ですが」を
/// 2 人が分けて話した場合など）、そのセグメント全体が最も重複の長い 1 人へ寄る。
/// より細かくするには word / token 単位のタイムスタンプが要るが、現在の Whisper.net
/// ラッパー経由では取得していない。将来それが取れるようになったら、
/// <see cref="TranscriptSegment"/> をより細かい粒度で作って本メソッドへ渡せばよい
/// （本メソッド自体は粒度を問わない）。
/// </para>
/// </remarks>
internal static class TranscriptDiarizationMerger
{
    /// <summary>話者を決められなかったセグメントの表示（REQ-TRX-DIA-06）。</summary>
    internal const string UnknownSpeakerLabel = "話者不明";

    /// <summary>
    /// 各文字起こしセグメントへ話者を割り当てる。
    /// </summary>
    /// <returns>
    /// <paramref name="transcriptSegments"/> と**同じ件数・同じ順序**の結果。
    /// 行の欠落や並べ替えは行わない（同一話者の連続セグメントも結合しない）。
    /// </returns>
    /// <exception cref="ArgumentException">
    /// いずれかの区間で終了時刻が開始時刻より前だった場合（要求仕様 §23 Case 7）。
    /// 時間軸が壊れた入力を黙って処理すると、誤った話者を確信をもって出力してしまうため拒否する。
    /// </exception>
    internal static IReadOnlyList<SpeakerAttributedSegment> Merge(
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<SpeakerSegment> speakerSegments)
    {
        ArgumentNullException.ThrowIfNull(transcriptSegments);
        ArgumentNullException.ThrowIfNull(speakerSegments);

        Validate(transcriptSegments, speakerSegments);

        var result = new List<SpeakerAttributedSegment>(transcriptSegments.Count);
        if (transcriptSegments.Count == 0)
        {
            return result;
        }

        // 話者区間を開始時刻順に並べる。sherpa-onnx は時間順に返すが、それに依存しない
        // （入力順で結果が変わる実装にしない。REQ-TRX-DIA-05 の決定性）。
        // 同時刻は話者 ID 順にして、同点時の走査順も決定的にする。
        var speakers = speakerSegments
            .OrderBy(s => s.Start)
            .ThenBy(s => s.SpeakerId)
            .ToArray();

        // 文字起こしセグメントも開始時刻順に走査する。出力は元の順序へ戻すため、
        // 元インデックスを添えて並べ替える。
        var order = Enumerable.Range(0, transcriptSegments.Count)
            .OrderBy(i => transcriptSegments[i].Start)
            .ToArray();

        // 全組み合わせ比較（O(N×M)）を避けるための走査窓（要求仕様 §26）。
        // active には「開始が現在のセグメント終端より前」かつ「終了が現在のセグメント始端より後」の
        // 話者区間だけが入る。開始時刻順に進むので、一度落とした区間が再び必要になることはない。
        var active = new List<SpeakerSegment>();
        var attributed = new SpeakerAttributedSegment[transcriptSegments.Count];
        int next = 0;

        foreach (var index in order)
        {
            var transcript = transcriptSegments[index];

            while (next < speakers.Length && speakers[next].Start < transcript.End)
            {
                active.Add(speakers[next]);
                next++;
            }

            // 走査は開始時刻の昇順なので、ここで落ちた区間は以降のどのセグメントとも重ならない。
            active.RemoveAll(s => s.End <= transcript.Start);

            attributed[index] = new SpeakerAttributedSegment(
                transcript.Start,
                transcript.End,
                SelectSpeaker(active, transcript),
                transcript.Text);
        }

        result.AddRange(attributed);
        return result;
    }

    /// <summary>
    /// 内部の話者 ID（0 始まり）を表示用の文字列へ変換する（REQ-TRX-DIA-06）。
    /// 内部 ID と表示番号を混同しないよう、変換はここ 1 か所に閉じる。
    /// </summary>
    internal static string FormatSpeaker(int? speakerId)
    {
        if (speakerId is not int id)
        {
            return UnknownSpeakerLabel;
        }

        return string.Create(CultureInfo.InvariantCulture, $"話者{id + 1}");
    }

    /// <summary>
    /// 重複時間が最も長い話者を選ぶ。**開始時刻だけの比較で決めてはならない。**
    /// 重複が 1 つも無ければ <c>null</c>（話者不明）を返し、直前の話者は引き継がない。
    /// </summary>
    private static int? SelectSpeaker(List<SpeakerSegment> candidates, TranscriptSegment transcript)
    {
        int? best = null;
        var bestOverlap = TimeSpan.Zero;

        foreach (var speaker in candidates)
        {
            var overlap = Overlap(transcript.Start, transcript.End, speaker.Start, speaker.End);
            if (overlap <= TimeSpan.Zero)
            {
                continue;
            }

            // 同点なら話者 ID が小さい方を採る（要求仕様 §23 Case 3）。
            // 「先に見つかった方」にすると入力順で結果が変わり、決定的でなくなる。
            if (overlap > bestOverlap || (overlap == bestOverlap && speaker.SpeakerId < best))
            {
                bestOverlap = overlap;
                best = speaker.SpeakerId;
            }
        }

        return best;
    }

    /// <summary>2 つの区間が重なっている長さ。重なっていなければ <see cref="TimeSpan.Zero"/>。</summary>
    private static TimeSpan Overlap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd)
    {
        var start = aStart > bStart ? aStart : bStart;
        var end = aEnd < bEnd ? aEnd : bEnd;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private static void Validate(
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<SpeakerSegment> speakerSegments)
    {
        for (int i = 0; i < transcriptSegments.Count; i++)
        {
            var segment = transcriptSegments[i];
            if (segment.End < segment.Start)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"文字起こしセグメント[{i}] の終了時刻 {segment.End} が開始時刻 {segment.Start} より前です。"),
                    nameof(transcriptSegments));
            }
        }

        for (int i = 0; i < speakerSegments.Count; i++)
        {
            var segment = speakerSegments[i];
            if (segment.End < segment.Start)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"話者区間[{i}] の終了時刻 {segment.End} が開始時刻 {segment.Start} より前です。"),
                    nameof(speakerSegments));
            }
        }
    }
}