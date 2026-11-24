namespace AudioCaptureApp.Services;

// TranscriptionService のうち、無音カット（有声区間への分割。REQ-TRX-06 / 09）を担当する部分。
// ライブとファイルの両方から使う（T193 で切り出した）。
public partial class TranscriptionService
{
    /// <summary>有声・無音判定の窓長（100ms）。チャンク全体の平均ではなく窓ごとに判定する。</summary>
    internal const int SilenceWindowSamples = TargetRate / 10;

    /// <summary>
    /// 「実体のある発話」とみなす有声ランの最小長（0.2 秒）。パディング**前**の長さで判定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 比べる相手は結合後の区間の**幅（span）**ではなく、結合する**前**の
    /// 連続有声窓のかたまり（ラン）の長さである。結合（<see cref="SplitVoicedRegions"/> の手順 3）
    /// を通った区間は内部に吸収した無音も幅に含むため、幅で判定すると 0.1 秒の物音が
    /// 結合幅の中に 2 つあるだけで足切りを素通りしてしまう（T125）。
    /// </para>
    /// <para>
    /// 値は <see cref="MinTailSamples"/> と同じだが根拠が別（あちらはセッション終端の
    /// 残バッファをどこまで処理するかの閾値）なので、定数は共有せず別に持つ。
    /// </para>
    /// </remarks>
    internal const int MinVoicedSamples = TargetRate / 5;

    /// <summary>
    /// 有声区間の合計がチャンクのこの割合以上なら分割しない。
    /// 落とせる無音がわずかなのに Whisper の呼び出し回数だけ増えるのを防ぐ。
    /// </summary>
    internal const double NoSplitVoicedRatio = 0.9;

    /// <summary>パディング後に接触・交差した区間を畳むための閾値（隙間 0 以下）。</summary>
    private const int TouchingGap = 1;

    /// <summary>
    /// 有声区間の開始時刻を、チャンク先頭の時刻と区間のチャンク内オフセットから求める。
    /// </summary>
    /// <param name="chunkStart">チャンク先頭の時刻（ライブは経過時間、ファイルはファイル先頭からの位置）。</param>
    /// <param name="regionStartSamples">チャンク先頭から区間先頭までのサンプル数（16kHz）。</param>
    /// <remarks>
    /// ライブとファイルの両方から呼ぶ。ここを間違えると
    /// 「無音を切ったぶんだけ時刻がずれる」という T112 で最も起こしやすい壊れ方をするため、
    /// 式を 2 箇所に散らさず 1 つにまとめてテストで固定している。
    /// <see cref="PadToMinimum"/> は末尾にしか無音を足さないので、この時刻には影響しない。
    /// </remarks>
    internal static TimeSpan RegionStart(TimeSpan chunkStart, int regionStartSamples)
        => chunkStart + TimeSpan.FromSeconds((double)regionStartSamples / TargetRate);

    /// <summary>
    /// Whisper が極端に短い入力を扱えないため、<paramref name="minSamples"/> 未満なら
    /// 末尾を無音で埋めて伸ばす。先頭は動かさないのでセグメント時刻は影響を受けない。
    /// </summary>
    internal static float[] PadToMinimum(float[] samples, int minSamples)
    {
        if (samples.Length >= minSamples)
        {
            return samples;
        }
        var padded = new float[minSamples];
        samples.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>
    /// バッファ末尾から 100ms 窓ごとに遡り、最初の有声窓に当たるまでの無音サンプル数を返す。
    /// 有声窓が 1 つも無ければ <c>null</c>（＝バッファ全体が無音）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 窓は<b>末尾に揃えて</b>敷く。判定したいのは末尾の連続無音であり、
    /// <see cref="CollectVoicedWindows"/> と同じ先頭揃えにすると末尾の最大 99ms が
    /// 端数窓に紛れて判定がぶれる。先頭側に余る端数も実長で 1 つの窓として評価する。
    /// 飛ばすと、発話がバッファ先頭の端数に収まっている場合に「全体が無音」と
    /// 誤判定して確定を取りこぼす。
    /// </para>
    /// <para>
    /// 有声窓を見つけた時点で打ち切るため、全体が無音のときだけ全走査になる。
    /// 20 秒分でも 200 窓であり、1 秒周期のポーリングに対して無視できる。
    /// </para>
    /// </remarks>
    internal static int? TrailingSilenceSamples(List<float> buffer, double rmsThreshold)
    {
        int end = buffer.Count;
        while (end > 0)
        {
            int start = Math.Max(0, end - SilenceWindowSamples);

            double sumSquares = 0;
            for (int i = start; i < end; i++)
            {
                sumSquares += buffer[i] * (double)buffer[i];
            }

            if (Math.Sqrt(sumSquares / (end - start)) >= rmsThreshold)
            {
                return buffer.Count - end;
            }

            end = start;
        }

        return null;
    }

    /// <summary>
    /// チャンクを有声区間へ分割する。無音だけのチャンクなら空を返す。
    /// </summary>
    /// <param name="samples">16kHz モノラルのチャンク。</param>
    /// <param name="options">閾値・結合幅・余白の調整値。</param>
    /// <returns>
    /// チャンク先頭からのサンプル位置で表した有声区間。時刻順に並び、互いに重ならない。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 空チャンクは手順に入る前に弾き、そのまま空を返す。
    /// 以降の手順は REQ-TRX-09 の ①〜⑥ と 1 対 1 で対応し、順序に意味がある。
    /// </para>
    /// <list type="number">
    /// <item>100ms 窓ごとに RMS を判定し、閾値以上の窓を有声とみなす（REQ-TRX-06）。</item>
    /// <item>連続する有声窓をひとつの区間にまとめる。</item>
    /// <item>区間どうしの隙間（パディング前の生の間隔）が
    /// <see cref="SilenceCutOptions.MergeGapSeconds"/> 未満なら結合する
    /// （息継ぎ程度の間で発話を切らないため）。</item>
    /// <item><see cref="MinVoicedSamples"/> 以上続くラン（結合前のかたまり）を
    /// 1 本も含まない区間を捨てる（足切り）。</item>
    /// <item>残った区間の前後に <see cref="SilenceCutOptions.PaddingSeconds"/> の余白を付けて
    /// チャンクの範囲内へクランプし（語頭・語尾を削らないため）、
    /// 余白で接触・交差した区間をさらに結合する。</item>
    /// <item>パディング後の区間の合計がチャンクの <see cref="NoSplitVoicedRatio"/> 以上なら、
    /// 分割しても削れる無音がわずかなので、チャンク全体を 1 区間として返す。</item>
    /// </list>
    /// <para>
    /// 足切り（④）はパディング（⑤）より<b>先</b>に行う。順序を逆にすると、0.1 秒
    /// （＝窓 1 つ分。窓量子化により、これが存在しうる最短のラン）のクリック音が
    /// 前後 0.2 秒ずつ広がって 0.5 秒のランになり、0.2 秒の足切りを素通りしてしまう。
    /// 落としたいのは「元々短い音」であって「余白を足したら長くなった音」ではない。
    /// </para>
    /// <para>
    /// 足切り（④）が結合後の区間幅ではなく<b>結合前のラン</b>を見るのも同じ理由による。
    /// 結合後の幅には内部に吸収した無音が含まれるため、0.1 秒の物音が結合幅の中に
    /// 2 つあるだけで 0.2 秒を超え、中身の大半が無音の区間が生き残っていた（T125）。
    /// 結合（③）は「実体のある発話へ、その前後の短い断片を貼り付ける」ための手順であり、
    /// 貼り付ける先の発話が無いなら結合結果に残すべきものは無い。
    /// </para>
    /// <para>
    /// 有声判定をチャンク全体の平均ではなく窓ごとに行うのは、長い無音に埋もれた
    /// 短い発話が平均でならされて無音扱いになるため（T116）。20 秒チャンク中 d 秒の
    /// 発話は全体平均で sqrt(d / 20) 倍まで薄まり、通常会話の音量でも
    /// 2〜3 秒以下だと丸ごと捨てられていた。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<VoicedRegion> SplitVoicedRegions(
        float[] samples, SilenceCutOptions options)
    {
        if (samples.Length == 0)
        {
            return [];
        }

        // ラン（結合前の連続有声窓）は足切り（④）の判定に要るので、結合で潰さず取っておく。
        var runs = CollectVoicedWindows(samples, options.RmsThreshold);
        if (runs.Count == 0)
        {
            return [];
        }

        var regions = new List<VoicedRegion>(runs);
        MergeCloseRegions(regions, SecondsToSamples(options.MergeGapSeconds));
        regions.RemoveAll(region => !ContainsSustainedRun(runs, region));
        if (regions.Count == 0)
        {
            return [];
        }

        ApplyPadding(regions, SecondsToSamples(options.PaddingSeconds), samples.Length);

        // パディングで区間が接触・交差しうるので、隙間ゼロのものを畳む。
        MergeCloseRegions(regions, TouchingGap);

        long voicedTotal = 0;
        foreach (var region in regions)
        {
            voicedTotal += region.Length;
        }

        return voicedTotal >= samples.Length * NoSplitVoicedRatio
            ? [new VoicedRegion(0, samples.Length)]
            : regions;
    }

    /// <summary>
    /// <paramref name="region"/> が <see cref="MinVoicedSamples"/> 以上続くランを含むか。
    /// </summary>
    /// <param name="runs">結合前のラン。時刻順・非交差であること。</param>
    /// <remarks>
    /// 結合（<see cref="MergeCloseRegions"/>）は隣接する区間の和集合しか作らないため、
    /// 結合後の区間は必ず「連続するいくつかのランとその隙間」になる。
    /// ランが区間の境界をまたいで半分だけ入ることはないので、包含判定で足りる。
    /// </remarks>
    private static bool ContainsSustainedRun(List<VoicedRegion> runs, VoicedRegion region)
    {
        int regionEnd = region.Start + region.Length;
        foreach (var run in runs)
        {
            if (run.Length >= MinVoicedSamples
                && run.Start >= region.Start
                && run.Start + run.Length <= regionEnd)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>区間を切り出す。チャンク全体と一致するならコピーせず元配列を返す。</summary>
    private static float[] SliceRegion(float[] samples, VoicedRegion region)
        => region.Start == 0 && region.Length == samples.Length
            ? samples
            : samples[region.Start..(region.Start + region.Length)];

    private static int SecondsToSamples(double seconds) => (int)(seconds * TargetRate);

    /// <summary>100ms 窓ごとに RMS を判定し、連続する有声窓を 1 区間にまとめる。</summary>
    private static List<VoicedRegion> CollectVoicedWindows(float[] samples, double threshold)
    {
        var regions = new List<VoicedRegion>();
        int runStart = -1;

        for (int start = 0; start < samples.Length; start += SilenceWindowSamples)
        {
            int length = Math.Min(SilenceWindowSamples, samples.Length - start);

            double sumSquares = 0;
            for (int i = start; i < start + length; i++)
            {
                sumSquares += samples[i] * (double)samples[i];
            }

            if (Math.Sqrt(sumSquares / length) >= threshold)
            {
                if (runStart < 0)
                {
                    runStart = start;
                }
            }
            else if (runStart >= 0)
            {
                regions.Add(new VoicedRegion(runStart, start - runStart));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            regions.Add(new VoicedRegion(runStart, samples.Length - runStart));
        }

        return regions;
    }

    /// <summary>
    /// 隙間が <paramref name="gapThreshold"/> <b>未満</b>の隣接区間を結合する（境界は結合しない）。
    /// </summary>
    /// <remarks>
    /// 結合後の終端は両区間の終端の大きい方を採る。呼び出し時点で区間が整列・非交差で
    /// あれば後ろの区間の終端と一致するが、その前提を暗黙に置かない。
    /// </remarks>
    private static void MergeCloseRegions(List<VoicedRegion> regions, int gapThreshold)
    {
        for (int i = regions.Count - 1; i > 0; i--)
        {
            var previous = regions[i - 1];
            int gap = regions[i].Start - (previous.Start + previous.Length);
            if (gap < gapThreshold)
            {
                int end = Math.Max(
                    previous.Start + previous.Length,
                    regions[i].Start + regions[i].Length);
                regions[i - 1] = new VoicedRegion(previous.Start, end - previous.Start);
                regions.RemoveAt(i);
            }
        }
    }

    /// <summary>各区間の前後に余白を付け、チャンクの範囲内へクランプする。</summary>
    private static void ApplyPadding(List<VoicedRegion> regions, int padding, int totalSamples)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            int start = Math.Max(0, regions[i].Start - padding);
            int end = Math.Min(totalSamples, regions[i].Start + regions[i].Length + padding);
            regions[i] = new VoicedRegion(start, end - start);
        }
    }
}