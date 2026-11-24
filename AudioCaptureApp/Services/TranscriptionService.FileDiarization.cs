using System.IO;
using System.Text;
using AudioCaptureApp.Models;
using Whisper.net;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ファイル文字起こしの話者識別ありの経路（REQ-TRX-DIA-*）を担当する部分（T193 で切り出した）。
public partial class TranscriptionService
{
    /// <summary>
    /// 話者ダイアライゼーション有効時のファイル文字起こし（REQ-TRX-DIA-04）。
    /// </summary>
    /// <remarks>
    /// 従来経路（<see cref="TranscribeFileCoreAsync"/>）と違い、**デコード結果を丸ごとメモリへ載せる**。
    /// sherpa-onnx の Diarization API が音声全体を 1 つの配列で要求するためで、
    /// 16kHz モノラル float では 約 230MB/時間 になる（NFR-07）。この経路は
    /// <c>SpeakerDiarizationEnabled = true</c> のときにしか通らない。
    /// <para>
    /// **Diarization を Whisper より先に走らせる。** モデル不備やレート不一致を、
    /// Whisper に数分掛けた後ではなく着手直後に判明させるためである（REQ-TRX-DIA-11）。
    /// </para>
    /// <para>
    /// 出力ファイルはマージが終わってから開く。途中で失敗・中止したときに
    /// 話者欄の欠けた中途半端な .transcript.txt を残さないためである。
    /// </para>
    /// </remarks>
    private async Task<bool> TranscribeFileWithDiarizationAsync(
        WhisperFactory factory,
        string audioFilePath,
        string outputPath,
        TimeSpan startOffset,
        string language,
        int? knownSpeakerCount,
        SpeakerDiarizationService diarization,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        var pcm = DecodeToMono16k(audioFilePath, ct, out var totalTime);
        progress?.Report(new FileTranscriptionProgress(DiarizePhase, TimeSpan.Zero, totalTime));

        // ① 話者識別。キャンセルは開始前と完了後にだけ効く（REQ-TRX-DIA-12）。
        ct.ThrowIfCancellationRequested();
        var diarizeProgress = progress == null
            ? null
            : new FractionProgress(progress, DiarizePhase, totalTime);
        var speakerSegments = diarization.Diarize(pcm, knownSpeakerCount, diarizeProgress, ct);
        ct.ThrowIfCancellationRequested();

        // ② Whisper は同じ音声を独立に解析する。Diarization の結果で音声を切り分けない
        //    （切り分けると Whisper の認識コンテキストが失われる）。
        var transcriptSegments = await CollectTranscriptSegmentsAsync(factory, pcm, totalTime, language, progress, ct)
            .ConfigureAwait(false);

        // ③ タイムラインを突き合わせる。ここは純粋関数で、推論も I/O も行わない。
        var attributed = TranscriptDiarizationMerger.Merge(transcriptSegments, speakerSegments);

        // ④ ここで初めてファイルへ書く。
        // ここから先はキャンセルを見ない。全部書くか一行も書かないかのどちらかにする。
        await WriteAttributedSegmentsAsync(outputPath, attributed, startOffset).ConfigureAwait(false);

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, totalTime, totalTime));
        return true;
    }

    /// <summary>
    /// 音声ファイル全体を 16kHz モノラルへデコードする（REQ-TRX-05 と同じ変換）。
    /// </summary>
    /// <remarks>
    /// 総再生時間から必要量を先に確保する。<see cref="List{T}"/> の倍々成長に任せると、
    /// 拡張のたびに旧配列と新配列が同時に存在して長時間音声でメモリのピークが跳ねるため。
    /// 末尾の <c>ToArray</c> で一度だけコピーが発生し、その瞬間だけ約 2 倍を要する
    /// （sherpa-onnx が <c>float[]</c> を要求するため避けられない）。
    /// </remarks>
    private static float[] DecodeToMono16k(string audioFilePath, CancellationToken ct, out TimeSpan totalTime)
    {
        using var reader = OpenAudioFile(audioFilePath);
        int sourceRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        totalTime = reader.TotalTime;
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        long estimated = (long)(totalTime.TotalSeconds * TargetRate) + TargetRate;
        var pcm = new List<float>((int)Math.Clamp(estimated, TargetRate, int.MaxValue / 2));

        var readBuffer = new float[sourceRate * channels];
        double resamplePos = 0;
        float lpfPrev = 0f;

        int samplesRead;
        while ((samplesRead = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            DownmixResampleAppend(
                readBuffer, samplesRead, channels, sourceRate,
                alpha, ref resamplePos, ref lpfPrev, pcm);
        }

        return pcm.ToArray();
    }

    /// <summary>
    /// デコード済み PCM を 20 秒チャンク → 有声区間の順に Whisper へ掛け、結果を溜める。
    /// </summary>
    /// <remarks>
    /// 時刻は**ファイル先頭基準**で持つ。REQ-TRX-FILE-10 の開始時刻をここで足してはならない。
    /// 足すと話者区間（ファイル先頭基準）と同じ時間軸で比較できなくなる。開始時刻は
    /// <see cref="WriteAttributedSegmentsAsync"/> で行を整形する直前に足す。
    /// </remarks>
    private async Task<List<TranscriptSegment>> CollectTranscriptSegmentsAsync(
        WhisperFactory factory,
        float[] pcm,
        TimeSpan totalTime,
        string language,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        var segments = new List<TranscriptSegment>();

        // トークン単位のタイムスタンプを有効にする（REQ-TRX-DIA-13）。
        // 話者の重複長を、セグメントの範囲ではなく実際に発話がある時間帯で測るために要る。
        // **Diarization 経路だけに付ける。** ライブ側と Diarization 無効時の推論コストと
        // 挙動を変えないため。DTW（UseDtwTimeStamps）は使わない — 有効化にはモデルごとの
        // alignment heads プリセットが要るが、本アプリはモデルパスを設定で差し替えられるため
        // 対応付けを保証できない。実測では DTW なしのトークン時刻で足りている。
        await using var processor = WithLanguageOption(factory.CreateBuilder(), language)
            .WithTokenTimestamps()
            .Build();

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, TimeSpan.Zero, totalTime));

        for (int offset = 0; offset < pcm.Length; offset += BufferThresholdSamples)
        {
            ct.ThrowIfCancellationRequested();

            int take = Math.Min(BufferThresholdSamples, pcm.Length - offset);

            // 末尾の端数は従来経路と同じ足切り（0.2 秒未満は Whisper がセグメントを返さない）。
            if (take < MinTailSamples)
            {
                break;
            }

            var chunk = new float[take];
            Array.Copy(pcm, offset, chunk, 0, take);
            var chunkStart = TimeSpan.FromSeconds((double)offset / TargetRate);

            foreach (var region in SplitVoicedRegions(chunk, SilenceCut))
            {
                ct.ThrowIfCancellationRequested();

                var regionOffset = RegionStart(chunkStart, region.Start);
                var samples = PadToMinimum(SliceRegion(chunk, region), MinWhisperSamples);

                await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                {
                    // 行頭の話者名の付け札は落とす（REQ-TRX-LIVE-20。話者欄と食い違う名前が並ぶため。T194）
                    var text = StripSpeakerTag(segment.Text?.Trim() ?? "");
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var speechSpans = BuildSpeechSpans(
                        segment.Tokens?.Select(t => (t.Text, t.Start, t.End)), regionOffset);

                    segments.Add(new TranscriptSegment(
                        regionOffset + segment.Start, regionOffset + segment.End, text, speechSpans));
                }
            }

            progress?.Report(new FileTranscriptionProgress(
                TranscribePhase,
                TimeSpan.FromSeconds((double)(offset + take) / TargetRate),
                totalTime));
        }

        return segments;
    }

    /// <summary>whisper.cpp のトークン時刻の単位（10ms）。</summary>
    private const int TokenTimestampUnitMs = 10;

    /// <summary>
    /// トークン時刻から、セグメント内で実際に発話が存在する時間帯を組み立てる（REQ-TRX-DIA-13）。
    /// </summary>
    /// <param name="tokens">
    /// (テキスト, 開始, 終了) の並び。開始・終了は 10ms 単位で、処理対象音声の先頭が基準。
    /// Whisper.net の型をそのまま受け取らないのは、モデル無しでテストできるようにするためである。
    /// </param>
    /// <param name="offset">区間先頭のオフセット。結果はこれを足した絶対時刻で返す。</param>
    /// <returns>
    /// 開始時刻順にならび、重なり・隣接がまとめられた時間帯。使えるトークンが 1 つも無ければ空。
    /// 空を返した場合、呼び出し側はセグメントの範囲へ縮退する。
    /// </returns>
    /// <remarks>
    /// 除くもの:
    /// <list type="bullet">
    /// <item><c>[_BEG_]</c> / <c>[_TT_nnn]</c> のような特殊トークン。
    /// <see cref="WhisperToken"/> に判別用のメンバーが無く、ID の閾値はモデル依存で当てにできないため、
    /// テキストの形（<c>[_</c> で始まり <c>]</c> で終わる）で判定する。</item>
    /// <item>長さ 0 のトークン。重複長に寄与せず、前後のトークンが同じ時間帯を覆う。</item>
    /// <item>終了が開始より前の壊れたトークン。</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<SpeechSpan> BuildSpeechSpans(
        IEnumerable<(string? Text, long Start, long End)>? tokens, TimeSpan offset)
    {
        var result = new List<SpeechSpan>();
        if (tokens == null)
        {
            return result;
        }

        var usable = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var (text, start, end) in tokens)
        {
            if (end <= start || IsSpecialToken(text))
            {
                continue;
            }

            usable.Add((
                offset + TimeSpan.FromMilliseconds(start * TokenTimestampUnitMs),
                offset + TimeSpan.FromMilliseconds(end * TokenTimestampUnitMs)));
        }

        if (usable.Count == 0)
        {
            return result;
        }

        // トークン時刻は概ね単調だが、それに依存せず並べ替えてから結合する。
        usable.Sort((a, b) => a.Start.CompareTo(b.Start));

        var currentStart = usable[0].Start;
        var currentEnd = usable[0].End;
        for (int i = 1; i < usable.Count; i++)
        {
            if (usable[i].Start <= currentEnd)
            {
                // 重なる、または隣接する。1 つにまとめる。
                if (usable[i].End > currentEnd)
                {
                    currentEnd = usable[i].End;
                }
                continue;
            }

            result.Add(new SpeechSpan(currentStart, currentEnd));
            currentStart = usable[i].Start;
            currentEnd = usable[i].End;
        }

        result.Add(new SpeechSpan(currentStart, currentEnd));
        return result;
    }

    /// <summary>
    /// <c>[_BEG_]</c> / <c>[_TT_173]</c> / <c>[_EOT_]</c> のような特殊トークンか。
    /// </summary>
    internal static bool IsSpecialToken(string? text)
        => text != null
           && text.StartsWith("[_", StringComparison.Ordinal)
           && text.EndsWith(']');   // char 版は常に序数比較（CA1865）

    /// <summary>
    /// 話者付きの結果を <c>.transcript.txt</c> へ書き出す（REQ-TRX-07 / REQ-TRX-DIA-06）。
    /// </summary>
    /// <remarks>
    /// 話者の割り当てはタイムライン全体が揃うまで確定しないため、
    /// <see cref="SegmentTranscribed"/> の発火もここまで遅れる。
    /// 従来経路（Diarization 無効）は 1 セグメント確定ごとに発火する。
    /// <para>
    /// **ここではキャンセルを見ない。** 推論はすべて終わっており、残るのは整形と書き出しだけの
    /// 確定処理である。途中で抜けると話者欄の欠けた中途半端なファイルが残るうえ、
    /// このファイルは <see cref="DeletePartialOutput"/> の対象外（Diarization 経路では
    /// 「この実行が作ったか」を区別できない）なので消してもやれない。
    /// 全部書くか、一行も書かないかのどちらかにする。
    /// </para>
    /// </remarks>
    private async Task WriteAttributedSegmentsAsync(
        string outputPath,
        IReadOnlyList<SpeakerAttributedSegment> segments,
        TimeSpan startOffset)
    {
        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);

        foreach (var segment in segments)
        {
            var startTime = startOffset + segment.Start;
            var endTime = startOffset + segment.End;
            var speaker = TranscriptDiarizationMerger.FormatSpeaker(segment.SpeakerId);
            var line =
                $"[{startTime:hh\\:mm\\:ss} - {endTime:hh\\:mm\\:ss}] [{FileSourceLabel}] [{speaker}] {segment.Text}";
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            SegmentTranscribed?.Invoke(line);
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 0.0〜1.0 の割合を、フェーズ名付きのファイル進捗へ変換する小さなアダプター。
    /// <see cref="SpeakerDiarizationService"/> に UI 都合の型を持ち込まないために挟む。
    /// </summary>
    private sealed class FractionProgress(
        IProgress<FileTranscriptionProgress> inner, string phase, TimeSpan total) : IProgress<double>
    {
        public void Report(double value)
            => inner.Report(new FileTranscriptionProgress(phase, total * Math.Clamp(value, 0.0, 1.0), total));
    }
}