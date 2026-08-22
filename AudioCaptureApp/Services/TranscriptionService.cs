using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AudioCaptureApp.Models;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace AudioCaptureApp.Services;

public enum AudioSourceType { Mic, Speaker }

/// <summary>
/// 無音カットの調整値。settings.json から与えられるため、
/// 手書きされた不正値（負値・NaN・無限大・過大値）でも壊れないよう
/// コンストラクターで必ずクランプする。
/// </summary>
public sealed record SilenceCutOptions
{
    private const double DefaultRmsThreshold = 0.01;
    private const double DefaultMergeGapSeconds = 2.0;
    private const double DefaultPaddingSeconds = 0.2;

    /// <summary>余白の上限。1 チャンク（20 秒）に対して現実的な範囲に収める。</summary>
    private const double MaxPaddingSeconds = 5.0;

    /// <summary>結合幅の上限。チャンク長（20 秒）を超えても意味が無い。</summary>
    private const double MaxMergeGapSeconds = 20.0;

    public SilenceCutOptions(double rmsThreshold, double mergeGapSeconds, double paddingSeconds)
    {
        RmsThreshold = Sanitize(rmsThreshold, 0.0, 1.0, DefaultRmsThreshold);
        MergeGapSeconds = Sanitize(mergeGapSeconds, 0.0, MaxMergeGapSeconds, DefaultMergeGapSeconds);
        PaddingSeconds = Sanitize(paddingSeconds, 0.0, MaxPaddingSeconds, DefaultPaddingSeconds);
    }

    /// <summary>有声とみなす窓の RMS 下限。</summary>
    public double RmsThreshold { get; }

    /// <summary>これ未満の無音を挟む有声区間どうしは 1 区間に結合する。</summary>
    public double MergeGapSeconds { get; }

    /// <summary>各有声区間の前後に付ける余白。</summary>
    public double PaddingSeconds { get; }

    public static SilenceCutOptions Default { get; } =
        new(DefaultRmsThreshold, DefaultMergeGapSeconds, DefaultPaddingSeconds);

    // 非有限値は「設定が壊れている」とみなして既定値へ戻す。
    // Math.Clamp は NaN をそのまま返すため、先に弾く必要がある。
    private static double Sanitize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>チャンク内の有声区間。Start はチャンク先頭からのサンプル位置。</summary>
public readonly record struct VoicedRegion(int Start, int Length);

/// <summary>
/// ファイル文字起こしの進捗（REQ-TRX-FILE-06）。
/// </summary>
/// <param name="Phase">
/// 実行中のフェーズ名。話者ダイアライゼーションが有効なときは「話者識別中」→「処理中」の
/// 2 フェーズになり、フェーズごとに 0% から進む。フェーズ名とセットで表示しないと
/// 進捗バーが 2 度 0% に戻る理由が分からなくなる。
/// </param>
/// <param name="Processed">
/// そのフェーズが読み終えたファイル内の位置。**常にファイル先頭を基準**とし、
/// REQ-TRX-FILE-10 の開始時刻は足さない（残り時間の目安であって時刻ではないため）。
/// </param>
public readonly record struct FileTranscriptionProgress(string Phase, TimeSpan Processed, TimeSpan Total);

public class TranscriptionService : IDisposable
{
    /// <summary>
    /// Whisper へ渡す 1 チャンク。<paramref name="StartElapsed"/> はセッション開始からの
    /// 経過時間で、チャンク先頭サンプルが録音された時刻を指す。
    /// </summary>
    private sealed record PendingChunk(float[] Samples, TimeSpan StartElapsed);

    private class SourceState
    {
        public readonly List<float> Pcm16kBuffer = new(BufferThresholdSamples + TargetRate);
        public readonly object BufferLock = new();

        /// <summary>ギャップで確定済みだがまだ Whisper に渡していないチャンク。</summary>
        public readonly Queue<PendingChunk> Ready = new();

        /// <summary>
        /// <see cref="Pcm16kBuffer"/> の最後のサンプルに対応する、セッション開始からの経過時間。
        /// チャンク内に閾値を超えるギャップは存在しない（あれば分割される）ため、
        /// チャンク先頭時刻はここからバッファ長を引いて求められる。
        /// </summary>
        public TimeSpan BufferEndElapsed;

        public int SourceRate;
        public int SourceChannels;
        public double ResamplePos;
        public string Label = "";
        public WhisperProcessor? Processor;
        // ローパスフィルタ用
        public float LpfAlpha;
        public float LpfPrev;
    }

    private WhisperFactory? _factory;
    private readonly Dictionary<AudioSourceType, SourceState> _sources = new();
    private Thread? _thread;
    private volatile bool _isRunning;
    private CancellationTokenSource? _cts;
    private string _outputPath = "";
    private DateTime _sessionStartTime;

    /// <summary>
    /// セッション開始からの経過時間を測る単調増加クロック。
    /// 記録時刻は「投入されたサンプル数の累積」ではなくこの実時間から求める
    /// （ミュート中や再生停止中はサンプルが供給されず、累積では時計が止まるため）。
    /// システム時刻の変更に影響されないよう <see cref="DateTime"/> ではなく
    /// <see cref="Stopwatch"/> を使う。
    /// </summary>
    private readonly Stopwatch _sessionClock = new();

    private const int TargetRate = 16000;
    private const int BufferThresholdSamples = TargetRate * 20; // 20秒分

    /// <summary>
    /// 音声の供給が途切れたと判定する閾値。これを超えるギャップを検出したら、
    /// そこまでのバッファを 1 チャンクとして確定し、次チャンクの基準時刻を打ち直す。
    /// </summary>
    /// <remarks>
    /// ギャップを「見逃す」と、その分だけチャンク先頭の時刻が後ろへずれる（誤差はギャップ長まで）。
    /// 逆に誤検出しても時刻は壁時計基準のままなので不正確にはならず、チャンクが細かく分かれるだけ。
    /// よって多少大きめに取り、キャプチャスレッドのスケジューリング遅延で
    /// 無用に分割されないようにしている。
    /// </remarks>
    internal static readonly TimeSpan GapThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>有声・無音判定の窓長（100ms）。チャンク全体の平均ではなく窓ごとに判定する。</summary>
    internal const int SilenceWindowSamples = TargetRate / 10;

    /// <summary>Whisper へ渡す最小サンプル数（1 秒）。これ未満は無音で埋めて伸ばす。</summary>
    internal const int MinWhisperSamples = TargetRate;

    /// <summary>セッション終了時、残りバッファを処理する最小サンプル数（0.2 秒）。</summary>
    internal const int MinTailSamples = TargetRate / 5;

    /// <summary>
    /// 音声の供給がこの時間以上途絶えていたら、20 秒分たまっていなくても
    /// チャンクとして確定する（T120 / REQ-TRX-LIVE-12）。
    /// </summary>
    /// <remarks>
    /// 測るのは「最後にサンプルを受け取ってからの経過時間」であって、
    /// <b>バッファ先頭サンプルの滞留時間ではない</b>。滞留時間は
    /// 「供給の途絶時間 ＋ バッファ長」なので、供給が続いている限りバッファ長とほぼ等しくなる。
    /// 滞留時間で 5 秒を判定すると「バッファ長 5 秒で確定」＝チャンク長を 5 秒に固定したのと
    /// 同じ挙動になり、発話の途中で機械的に分断される。
    /// この同値性のため、閾値が 20 秒だった頃はこの契機が
    /// <see cref="BufferThresholdSamples"/> に吸収され、連続供給下では常に空振りしていた（T129）。
    /// </remarks>
    internal static readonly TimeSpan StaleSupplyIdle = TimeSpan.FromSeconds(5);

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

    public event Action<string>? Error;
    public event Action<string>? SegmentTranscribed;
    public event Action<string>? RuntimeInfo;

    public bool IsModelLoaded => _factory != null;

    /// <summary>無音カットの調整値。MainViewModel が設定から反映する。</summary>
    public SilenceCutOptions SilenceCut { get; set; } = SilenceCutOptions.Default;

    // GPU優先順（CUDA > Vulkan > CoreML > OpenVino > CPU）
    private static readonly List<RuntimeLibrary> GpuPreferredOrder = new()
    {
        RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.CoreML,
        RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx
    };

    private static bool IsGpuLibrary(RuntimeLibrary? library) =>
        library is RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

    /// <summary>
    /// ステータス通知用の実行先表記を作る（REQ-GPU-05）。
    /// GPU 実行のときだけランタイム種別を併記する。
    /// </summary>
    internal static string DescribeRuntime(RuntimeLibrary? loaded, bool gpuInUse)
        => gpuInUse && IsGpuLibrary(loaded) ? $"GPU ({loaded})" : "CPU";

    // --- モデル読み込み中のネイティブログから GPU の実態を読み取る (T123) ---
    //
    // GPU 版のランタイム DLL は使える GPU デバイスが無くても読み込めるため、
    // RuntimeOptions.LoadedLibrary だけでは GPU 利用可否を判定できない
    // （Vulkan ローダーの ICD を無効化した実測で、CPU 速度なのに "GPU (Vulkan)" と
    //  表示されることを確認済み）。Whisper.net には他に GPU の情報源が無いため、
    // whisper.cpp / ggml がモデル読み込み時に出すログから 2 つの事実を拾う。
    //
    //   whisper_init_with_params_no_state: backends   = 2   → GPU バックエンドが登録されたか
    //   whisper_model_load:      Vulkan0 total size = ...    → 重みが実際にどこへ載ったか
    //
    // ログ書式は公開 API ではないため、解析できなければ従来の判定へフォールバックする。

    /// <summary>
    /// <c>whisper_init_with_params_no_state: backends   = 2</c> の形の行から数値を取り出す。
    /// 該当しない行なら <c>null</c>。
    /// </summary>
    internal static int? ParseBackendCount(string logLine)
    {
        const string key = "backends";
        int keyIndex = logLine.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return null;
        }

        int equalsIndex = logLine.IndexOf('=', keyIndex + key.Length);
        if (equalsIndex < 0)
        {
            return null;
        }

        return int.TryParse(
            logLine.AsSpan(equalsIndex + 1).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var count) ? count : null;
    }

    /// <summary>
    /// <c>whisper_model_load:      Vulkan0 total size =   487.01 MB</c> の形の行から
    /// 重みが載ったバックエンド名（<c>Vulkan0</c> / <c>CPU</c> 等）を取り出す。
    /// 該当しない行なら <c>null</c>。
    /// </summary>
    internal static string? ParseModelBackend(string logLine)
    {
        const string marker = "total size";
        if (!logLine.Contains("whisper_model_load:", StringComparison.Ordinal))
        {
            return null;
        }

        int markerIndex = logLine.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        // "total size" の直前のトークンがバックエンド名
        var head = logLine.AsSpan(0, markerIndex).TrimEnd();
        int separator = head.LastIndexOf(' ');
        if (separator < 0)
        {
            return null;
        }

        var name = head[(separator + 1)..].ToString();
        // バックエンド名が無い行（"whisper_model_load: total size ..."）を拾わない
        return name.Length == 0 || name.EndsWith(':') ? null : name;
    }

    /// <summary>重みの載ったバックエンド名から GPU 実行かどうかを決める。</summary>
    internal static bool IsGpuInUse(string? modelBackend)
        => modelBackend != null && !modelBackend.Equals("CPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// モデル読み込み中のネイティブログを覗いて、GPU の実態を拾う。
    /// 購読は <see cref="LoadModel"/> の読み込み区間だけに限る。
    /// </summary>
    private sealed class NativeLoadObserver
    {
        private int? _backendCount;
        private string? _modelBackend;

        public void OnLog(WhisperLogLevel level, string? message)
        {
            _ = level;
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            _backendCount ??= ParseBackendCount(message);
            _modelBackend ??= ParseModelBackend(message);
        }

        /// <summary>GPU バックエンドが登録されたか。解析できなければ <c>null</c>（不明）。</summary>
        public bool? HasGpuBackend => _backendCount is int count ? count >= 2 : null;

        /// <summary>いま GPU で動いているか。解析できなければ <c>null</c>（不明）。</summary>
        public bool? GpuInUse => _modelBackend != null ? IsGpuInUse(_modelBackend) : null;
    }

    // GPU利用可否は実際にランタイムを読み込んでみないと判定できないため、GPU 優先順で読み込む。
    //
    // ここで RuntimeLibraryOrder が効くのは「プロセス内で最初の読み込み」だけである（REQ-TRX-02）。
    // Whisper.net は WhisperFactory.LibraryLoaded を static な Lazy<LoadResult> で持ち、
    // ネイティブランタイムをプロセスで 1 度しか読み込まない。Factory を破棄しても
    // アンロードされないため、順序を CPU 限定に差し替えて読み込み直しても空振りする（T119）。
    // したがって CPU 実行への切り替えは WhisperFactoryOptions.UseGpu で行う（REQ-TRX-03）。
    public (bool Success, bool GpuAvailable) LoadModel(string modelPath, bool useGpu)
    {
        DisposeProcessor();

        if (!File.Exists(modelPath))
        {
            return (false, true);
        }

        try
        {
            RuntimeOptions.RuntimeLibraryOrder = GpuPreferredOrder;

            var observer = new NativeLoadObserver();
            using (LogProvider.AddLogger(observer.OnLog))
            {
                _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });

                // FromPath はモデルの読み込みをその場で行うが、失敗しても例外を投げずに
                // ファクトリを返す。失敗が表面化するのは最初の CreateBuilder() なので、
                // ここで 1 度呼んで確定させる（読み込み済みのため追加コストは無い。実測 0ms）。
                // これが無いと壊れたモデルでも「読み込み完了」と表示され、録音開始や
                // ファイル文字起こしまで失敗が判明しない（T122）。
                _ = _factory.CreateBuilder();
            }

            var loaded = RuntimeOptions.LoadedLibrary;

            // GPU 版ランタイムを読み込めただけでは GPU が使えるとは限らない（T123）。
            // ログを解析できなかった場合は従来どおりランタイム種別だけで判定する。
            var gpuAvailable = IsGpuLibrary(loaded) && observer.HasGpuBackend != false;
            var gpuInUse = observer.GpuInUse ?? (useGpu && gpuAvailable);

            RuntimeInfo?.Invoke(DescribeRuntime(loaded, gpuInUse));
            return (true, gpuAvailable);
        }
        // CA1031: Whisper のネイティブランタイム読み込みは DllNotFoundException 等、
        //         環境依存の任意の例外を投げる。失敗は Error イベントに変換して継続する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            Error?.Invoke($"Whisperモデル読み込み失敗: {ex.Message}");
            DisposeProcessor();
            return (false, true);
        }
#pragma warning restore CA1031
    }

    public void RegisterSource(AudioSourceType type, string label, int sourceRate, int sourceChannels)
    {
        // 既存のプロセッサがあれば破棄（登録はセッション開始前なのでワーカーは動いていない）
        if (_sources.TryGetValue(type, out var existing))
        {
            DisposeProcessorSafely(existing, workerExited: true);
        }

        // α = 2π·fc / (2π·fc + sourceRate),  fc = TargetRate / 2
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        _sources[type] = new SourceState
        {
            SourceRate = sourceRate,
            SourceChannels = sourceChannels,
            Label = label,
            Processor = _factory!.CreateBuilder().WithLanguage("ja").Build(),
            LpfAlpha = alpha
        };
    }

    public void StartSession(string mp3FilePath, DateTime startTime)
    {
        if (_factory == null)
        {
            throw new InvalidOperationException("Whisperモデルが読み込まれていません。");
        }
        if (_sources.Count == 0)
        {
            throw new InvalidOperationException("音声ソースが登録されていません。先にRegisterSourceを呼び出してください。");
        }

        _outputPath = Path.ChangeExtension(mp3FilePath, ".txt");
        _sessionStartTime = startTime;

        foreach (var state in _sources.Values)
        {
            lock (state.BufferLock)
            {
                state.Pcm16kBuffer.Clear();
                state.Ready.Clear();
                state.BufferEndElapsed = TimeSpan.Zero;
            }
            state.ResamplePos = 0;
            state.LpfPrev = 0f;
        }

        _sessionClock.Restart();
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _thread = new Thread(TranscriptionLoop) { IsBackground = true, Name = "WhisperTranscription" };
        _thread.Start();
    }

    public void AddSamples(AudioSourceType type, float[] samples, int sampleCount)
    {
        if (!_isRunning || !_sources.TryGetValue(type, out var state))
        {
            return;
        }

        // このパケットの音声が「いつ録音されたか」を実時間で押さえる。
        // ミュート中や再生停止中はこのメソッド自体が呼ばれないため、
        // 呼ばれた時刻の差分がそのまま供給の途切れ（ギャップ）になる。
        var nowElapsed = _sessionClock.Elapsed;
        int frames = sampleCount / state.SourceChannels;
        var audioStart = nowElapsed - TimeSpan.FromSeconds((double)frames / state.SourceRate);

        lock (state.BufferLock)
        {
            if (ShouldSplitOnGap(audioStart, state.BufferEndElapsed, state.Pcm16kBuffer.Count, GapThreshold))
            {
                // ここまでを 1 チャンクとして確定し、以降は新しい基準時刻で積み直す。
                state.Ready.Enqueue(new PendingChunk(
                    state.Pcm16kBuffer.ToArray(),
                    ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count)));
                state.Pcm16kBuffer.Clear();

                // 不連続な音声を地続きとして扱わないよう、リサンプラと LPF の状態も切る
                state.ResamplePos = 0;
                state.LpfPrev = 0f;
            }

            double resamplePos = state.ResamplePos;
            float lpfPrev = state.LpfPrev;
            DownmixResampleAppend(
                samples, sampleCount, state.SourceChannels, state.SourceRate,
                state.LpfAlpha, ref resamplePos, ref lpfPrev, state.Pcm16kBuffer);
            state.ResamplePos = resamplePos;
            state.LpfPrev = lpfPrev;

            state.BufferEndElapsed = nowElapsed;
        }
    }

    /// <summary>
    /// 新しく届いた音声の開始時刻がバッファ末尾から <paramref name="threshold"/> 以上離れていれば、
    /// 供給が途切れたとみなしてチャンクを分割する。バッファが空なら分割対象が無いので false。
    /// </summary>
    internal static bool ShouldSplitOnGap(
        TimeSpan audioStart, TimeSpan bufferEndElapsed, int bufferedSampleCount, TimeSpan threshold)
        => bufferedSampleCount > 0 && audioStart - bufferEndElapsed > threshold;

    /// <summary>
    /// バッファ先頭サンプルの経過時間を、末尾の経過時間とバッファ長から逆算する。
    /// チャンク内にギャップが無いことが前提（<see cref="ShouldSplitOnGap"/> が保証する）。
    /// 末尾を常に実時間へ再アンカーするため、リサンプル誤差が累積しない。
    /// </summary>
    internal static TimeSpan ChunkStartElapsed(TimeSpan bufferEndElapsed, int bufferedSampleCount)
    {
        var start = bufferEndElapsed - TimeSpan.FromSeconds((double)bufferedSampleCount / TargetRate);
        return start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

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

    // ステレオ→モノ変換 + 1次IIRローパス + 線形リサンプル (sourceRate → 16kHz)
    // 状態 (resamplePos / lpfPrev) は呼び出し側が保持する
    private static void DownmixResampleAppend(
        float[] input, int sampleCount, int channels, int sourceRate,
        float alpha, ref double resamplePos, ref float lpfPrev,
        List<float> output)
    {
        int frames = sampleCount / channels;
        double ratio = (double)sourceRate / TargetRate;
        float prev = lpfPrev;

        for (; resamplePos < frames; resamplePos += ratio)
        {
            int idx = (int)resamplePos;
            if (idx >= frames)
            {
                break;
            }

            float sample = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                sample += input[idx * channels + ch];
            }
            sample /= channels;

            prev = prev + alpha * (sample - prev);
            output.Add(prev);
        }
        resamplePos -= frames;
        lpfPrev = prev;
    }

    /// <summary>
    /// 音声ファイルを文字起こしする。
    /// </summary>
    /// <param name="startOffset">
    /// 出力行のタイムスタンプの起点（REQ-TRX-FILE-10）。ファイル先頭がこの時刻に録音されたものとして扱う。
    /// 未指定なら <see cref="TimeSpan.Zero"/> を渡す（＝ファイル先頭からの経過時間になる）。
    /// </param>
    /// <remarks>
    /// <paramref name="startOffset"/> は進捗（<paramref name="progress"/>）には足さない。
    /// 進捗は残りの目安であって時刻ではないため、常にファイル先頭基準で報告する。
    /// </remarks>
    public async Task<bool> TranscribeFileAsync(
        string audioFilePath,
        TimeSpan startOffset,
        SpeakerDiarizationService? diarization,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        if (_factory == null)
        {
            Error?.Invoke("Whisperモデルが読み込まれていません。");
            return false;
        }

        string outputPath = BuildTranscriptPath(audioFilePath);
        try
        {
            // diarization が null なら従来どおりストリーミングで処理する（REQ-TRX-DIA-03）。
            // 非 null のときだけ、音声全体をメモリへ載せる経路へ分岐する（NFR-07）。
            var work = diarization == null
                ? TranscribeFileCoreAsync(audioFilePath, outputPath, startOffset, progress, ct)
                : TranscribeFileWithDiarizationAsync(
                    audioFilePath, outputPath, startOffset, diarization, progress, ct);
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 部分出力ファイルは削除する（ユーザーが完結したと誤認しないように）。
            // Core を抜ける時点で using が StreamWriter を破棄済みのため、ここで削除できる。
            DeletePartialOutput(outputPath, diarization);
            throw;
        }
        // CA1031: キャンセル判定のため全例外をいったん見る必要がある（次行のコメント参照）。
        //         キャンセル以外は Error イベントに変換して false を返す。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            // Whisper のネイティブ処理はキャンセル時に OperationCanceledException 以外を
            // 投げることがあるため、トークンがキャンセル済みなら中止として扱う
            if (ct.IsCancellationRequested)
            {
                DeletePartialOutput(outputPath, diarization);
                throw new OperationCanceledException(ct);
            }
            Error?.Invoke($"ファイル文字起こしエラー: {ex.Message}");
            return false;
        }
#pragma warning restore CA1031
    }

    // 破棄対象（reader / writer / processor）を using で束ねるために本体を切り出している。
    // 例外は using による破棄が完了してから呼び出し元へ伝播する。
    private async Task<bool> TranscribeFileCoreAsync(
        string audioFilePath,
        string outputPath,
        TimeSpan startOffset,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        await using var reader = new AudioFileReader(audioFilePath);
        int sourceRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        TimeSpan totalTime = reader.TotalTime;
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
        await using var processor = _factory!.CreateBuilder().WithLanguage("ja").Build();

        // ファイル読み込みバッファ（約1秒分）
        var readBuffer = new float[sourceRate * channels];
        var pcm16kBuffer = new List<float>(BufferThresholdSamples + TargetRate);
        double resamplePos = 0;
        float lpfPrev = 0f;
        TimeSpan chunkOffset = TimeSpan.Zero;
        const string label = FileSourceLabel;

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, TimeSpan.Zero, totalTime));

        int samplesRead;
        while ((samplesRead = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            DownmixResampleAppend(
                readBuffer, samplesRead, channels, sourceRate,
                alpha, ref resamplePos, ref lpfPrev, pcm16kBuffer);

            while (pcm16kBuffer.Count >= BufferThresholdSamples)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new float[BufferThresholdSamples];
                pcm16kBuffer.CopyTo(0, chunk, 0, BufferThresholdSamples);
                pcm16kBuffer.RemoveRange(0, BufferThresholdSamples);

                await ProcessFileChunkAsync(
                        processor, chunk, startOffset + chunkOffset, label, writer, ct)
                    .ConfigureAwait(false);
                chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
                progress?.Report(new FileTranscriptionProgress(TranscribePhase, chunkOffset, totalTime));
            }
        }

        // 残りバッファ（末尾の短い発話を落とさないよう 0.2 秒以上あれば処理）
        if (pcm16kBuffer.Count >= MinTailSamples)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pcm16kBuffer.ToArray();
            await ProcessFileChunkAsync(
                    processor, chunk, startOffset + chunkOffset, label, writer, ct)
                .ConfigureAwait(false);
            chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
        }

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, totalTime, totalTime));
        return true;
    }

    /// <summary>進捗表示に出すフェーズ名（REQ-TRX-FILE-06）。</summary>
    private const string TranscribePhase = "処理中";

    /// <summary>進捗表示に出すフェーズ名（話者ダイアライゼーション有効時のみ現れる）。</summary>
    private const string DiarizePhase = "話者識別中";

    /// <summary>ファイル文字起こしの出力行に付けるラベル。</summary>
    internal const string FileSourceLabel = "ファイル";

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
        string audioFilePath,
        string outputPath,
        TimeSpan startOffset,
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
        var speakerSegments = diarization.Diarize(pcm, diarizeProgress, ct);
        ct.ThrowIfCancellationRequested();

        // ② Whisper は同じ音声を独立に解析する。Diarization の結果で音声を切り分けない
        //    （切り分けると Whisper の認識コンテキストが失われる）。
        var transcriptSegments = await CollectTranscriptSegmentsAsync(pcm, totalTime, progress, ct)
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
        using var reader = new AudioFileReader(audioFilePath);
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
        float[] pcm,
        TimeSpan totalTime,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        var segments = new List<TranscriptSegment>();
        await using var processor = _factory!.CreateBuilder().WithLanguage("ja").Build();

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
                    var text = segment.Text?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    segments.Add(new TranscriptSegment(
                        regionOffset + segment.Start, regionOffset + segment.End, text));
                }
            }

            progress?.Report(new FileTranscriptionProgress(
                TranscribePhase,
                TimeSpan.FromSeconds((double)(offset + take) / TargetRate),
                totalTime));
        }

        return segments;
    }

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

    // {入力ファイル名}.transcript.txt を同じフォルダに配置
    // 例: audio.mp3 → audio.transcript.txt
    // （録音時に生成される audio.txt と名前衝突しないように）
    internal static string BuildTranscriptPath(string audioFilePath)
    {
        return Path.ChangeExtension(audioFilePath, ".transcript.txt");
    }

    /// <summary>
    /// 中止時に、この実行が作りかけた出力ファイルだけを消す（REQ-TRX-FILE-07）。
    /// </summary>
    /// <remarks>
    /// 従来経路（<paramref name="diarization"/> が <c>null</c>）は開始時点で出力ファイルを
    /// <c>append: false</c> で開いて中身を捨てているため、中止したら消すのが正しい。
    /// Diarization 経路はマージが終わるまで出力ファイルを開かない。中止時点では未作成なので、
    /// ここで無条件に消すと **前回成功したときの `.transcript.txt` を巻き添えで削除してしまう**。
    /// 消してよいのは「この実行が作ったもの」だけである。
    /// </remarks>
    private static void DeletePartialOutput(string path, SpeakerDiarizationService? diarization)
    {
        if (diarization != null)
        {
            return;
        }

        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除失敗は無視（ロック中など）
        }
    }

    /// <param name="chunkStart">
    /// このチャンク先頭のタイムスタンプ。開始時刻の指定（REQ-TRX-FILE-10）を含んだ値が渡る。
    /// 未指定ならファイル先頭からの経過時間そのもの。
    /// </param>
    private async Task ProcessFileChunkAsync(
        WhisperProcessor processor, float[] samples, TimeSpan chunkStart,
        string label, StreamWriter writer, CancellationToken ct)
    {
        foreach (var region in SplitVoicedRegions(samples, SilenceCut))
        {
            ct.ThrowIfCancellationRequested();

            var regionOffset = RegionStart(chunkStart, region.Start);
            var regionSamples = PadToMinimum(SliceRegion(samples, region), MinWhisperSamples);

            await foreach (var segment in processor.ProcessAsync(regionSamples, ct).ConfigureAwait(false))
            {
                var text = segment.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var startTime = regionOffset + segment.Start;
                var endTime = regionOffset + segment.End;
                var line = $"[{startTime:hh\\:mm\\:ss} - {endTime:hh\\:mm\\:ss}] [{label}] {text}";
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                SegmentTranscribed?.Invoke(line);
            }
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    private void TranscriptionLoop()
    {
        var token = _cts!.Token;

        while (_isRunning)
        {
            try
            {
                token.WaitHandle.WaitOne(1000);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            foreach (var (_, state) in _sources)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                // ギャップで確定したチャンクが複数溜まっていることがあるため、
                // 1 tick で 1 つではなく取り出せるだけ処理する。
                // ただし _isRunning を必ず見ること。これが無いと停止要求後も
                // バックログを全部捌き切るまで抜けず、StopSession がタイムアウトする（T117）。
                PendingChunk? chunk;
                while (_isRunning && !token.IsCancellationRequested
                       && (chunk = TakeNextChunk(state, _sessionClock.Elapsed, SilenceCut)) != null)
                {
                    // 通常運転中。停止要求が来たらチャンクの途中（区間の切れ目）で抜ける。
                    ProcessChunk(chunk, state, interruptible: true, token);
                }
            }
        }

        // 残りバッファを処理（キャンセルされていなければ）
        if (!token.IsCancellationRequested)
        {
            foreach (var (_, state) in _sources)
            {
                PendingChunk? chunk;
                while (!token.IsCancellationRequested
                       && (chunk = TakeNextChunk(state, _sessionClock.Elapsed, SilenceCut)) != null)
                {
                    // 排出処理。ここは _isRunning が false の状態で走るため打ち切ってはならない。
                    // 打ち切りたいときは token をキャンセルする（StopSession の 2 段目）。
                    ProcessChunk(chunk, state, interruptible: false, token);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                PendingChunk? tail = null;
                lock (state.BufferLock)
                {
                    if (state.Pcm16kBuffer.Count >= MinTailSamples)
                    {
                        tail = new PendingChunk(
                            state.Pcm16kBuffer.ToArray(),
                            ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count));
                    }
                    state.Pcm16kBuffer.Clear();
                }

                if (tail != null)
                {
                    ProcessChunk(tail, state, interruptible: false, token);
                }
            }
        }
    }

    /// <summary>
    /// 確定済みチャンクを 1 つ取り出す。無ければバッファから 1 チャンク切り出す。
    /// どちらも無ければ <c>null</c>。
    /// </summary>
    private static PendingChunk? TakeNextChunk(
        SourceState state, TimeSpan nowElapsed, SilenceCutOptions options)
    {
        lock (state.BufferLock)
        {
            if (state.Ready.Count > 0)
            {
                return state.Ready.Dequeue();
            }

            if (state.Pcm16kBuffer.Count == 0)
            {
                return null;
            }

            var start = ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count);
            int take = ChunkTakeCount(
                state.Pcm16kBuffer.Count,
                nowElapsed - state.BufferEndElapsed,
                TrailingSilenceSamples(state.Pcm16kBuffer, options.RmsThreshold),
                SecondsToSamples(options.MergeGapSeconds));
            if (take == 0)
            {
                return null;
            }

            var samples = new float[take];
            state.Pcm16kBuffer.CopyTo(0, samples, 0, take);
            state.Pcm16kBuffer.RemoveRange(0, take);
            return new PendingChunk(samples, start);
        }
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
    /// バッファから今回切り出すサンプル数を決める。<c>0</c> ならまだ切り出さない。
    /// </summary>
    /// <param name="bufferedSampleCount">バッファに溜まっているサンプル数（16kHz）。</param>
    /// <param name="supplyIdle">最後にサンプルを受け取ってからの経過時間。</param>
    /// <param name="trailingSilenceSamples">
    /// 末尾の連続無音サンプル数。バッファ全体が無音なら <c>null</c>
    /// （<see cref="TrailingSilenceSamples"/> の戻り値）。
    /// </param>
    /// <param name="endpointSilenceSamples">発話が終わったとみなす末尾無音の長さ。</param>
    /// <remarks>
    /// 契機は 3 つあり、この優先順で判定する。
    /// <list type="number">
    /// <item>20 秒分たまった → 20 秒分だけ切り出す（1 回の Whisper 呼び出しを
    /// 際限なく長くしないための上限。T117）。</item>
    /// <item>末尾に <paramref name="endpointSilenceSamples"/> 以上の無音が積まれ、かつ
    /// バッファ内に有声窓がある → 発話が終わったとみなしてバッファ全部を切り出す（T129）。</item>
    /// <item>供給が <see cref="StaleSupplyIdle"/> 以上途絶えている → バッファ全部を切り出す（T120）。</item>
    /// </list>
    /// <para>
    /// 2 が無いと、マイクは無音でも WASAPI がサンプルを供給し続けるため
    /// ギャップ分割（<see cref="ShouldSplitOnGap"/>）も 3 も発火せず、出力粒度が 20 秒固定になる。
    /// 保持時間に <see cref="SilenceCutOptions.MergeGapSeconds"/> と同じ値を渡すのは、
    /// それが <see cref="SplitVoicedRegions"/> で「発話の切れ目」を定義している値そのものだからで、
    /// 揃えれば確定チャンクは有声区間ちょうど 1 個を含む形になり、Whisper の呼び出し回数は
    /// この契機の導入前と変わらない。
    /// </para>
    /// <para>
    /// バッファ全体が無音（<paramref name="trailingSilenceSamples"/> が <c>null</c>）なら
    /// 2 では確定しない。1 で切り出され、有声区間 0 件として Whisper を呼ばずに捨てられる。
    /// </para>
    /// <para>
    /// 3 が無いと、ミュートや再生停止で供給が止まったソースのバッファは
    /// 「次のパケットが来てギャップ分割が発火するまで」書き出されない。
    /// 実測では 16 秒分の音声が 57 秒間放置され、その間に他ソースが書き進んだため
    /// 出力行の時刻が前後して見えていた。
    /// </para>
    /// </remarks>
    internal static int ChunkTakeCount(
        int bufferedSampleCount,
        TimeSpan supplyIdle,
        int? trailingSilenceSamples,
        int endpointSilenceSamples)
    {
        if (bufferedSampleCount >= BufferThresholdSamples)
        {
            return BufferThresholdSamples;
        }

        // null（＝全体が無音）はここで確定しない。1 で切り出され、有声区間 0 件として捨てられる。
        // 末尾無音が測れている＝有声窓が 1 つ以上あるので、最小長は自動的に満たす
        // （有声 100ms + 無音 > 0.2 秒）ため MinTailSamples は見ない。
        // 0 サンプルを弾くのは、MergeGapSeconds に 0 を設定されたときに
        // 有声のまま（発話の途中で）毎ポーリング確定してしまうのを防ぐため。
        if (trailingSilenceSamples is int trailingSilence
            && trailingSilence > 0 && trailingSilence >= endpointSilenceSamples)
        {
            return bufferedSampleCount;
        }

        // 0.2 秒未満の断片は 1 回の推論に見合わないため、セッション終了まで持ち越す
        if (supplyIdle >= StaleSupplyIdle && bufferedSampleCount >= MinTailSamples)
        {
            return bufferedSampleCount;
        }

        return 0;
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

    /// <summary>
    /// 区間ループを次の区間へ進めてよいかを判定する。
    /// キャンセル済みなら常に打ち切り、停止要求は <paramref name="interruptible"/> のときだけ見る。
    /// </summary>
    internal static bool ShouldStopRegionLoop(bool cancelled, bool isRunning, bool interruptible)
        => cancelled || (interruptible && !isRunning);

    private bool ShouldStopRegionLoop(bool interruptible, CancellationToken token)
        => ShouldStopRegionLoop(token.IsCancellationRequested, _isRunning, interruptible);

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

    /// <summary>
    /// チャンクを有声区間に分けて Whisper に掛ける。
    /// </summary>
    /// <param name="interruptible">
    /// <c>true</c> なら停止要求（<see cref="_isRunning"/> が false）で区間ループを打ち切る。
    /// 通常のポーリングループからは <c>true</c>、停止時の残バッファ排出からは <c>false</c> を渡す。
    /// </param>
    /// <remarks>
    /// T112 で 1 チャンクが複数区間に分かれるようになり、1 チャンクあたりの Whisper 呼び出しが
    /// 最大 10 回になった（区間は 2.0 秒以上離れ、各区間は 0.2 秒以上あるため 20 秒に 10 個が上限）。
    /// 停止要求が来たあとも全区間を回し切ると <see cref="StopSession"/> の猶予 30 秒を超え、
    /// T117 が直した「停止がタイムアウトして WhisperProcessor の破棄を見送る」経路に戻ってしまう。
    /// <para>
    /// ここで <see cref="_isRunning"/> を無条件に見てはいけない。停止時の排出処理は
    /// <see cref="_isRunning"/> が false の状態で走るため、無条件に見ると
    /// 排出すべき最後のチャンクを 1 区間も処理せずに捨ててしまう（T120 の書き出し遅延対策が無効になる）。
    /// だから呼び出し元ごとに <paramref name="interruptible"/> で切り替える。
    /// </para>
    /// </remarks>
    private void ProcessChunk(
        PendingChunk chunk, SourceState state, bool interruptible, CancellationToken token)
    {
        // results は try の外で宣言する。中で宣言すると、Whisper がキャンセル例外を投げたときに
        // 確定済みの区間の行まで一緒に捨てられる。それらの行は TranscribeRegion の中で
        // SegmentTranscribed により画面へ出た後なので、捨てると画面と .txt が食い違う（T126）。
        var results = new List<string>();
        try
        {
            // 無音は Whisper に渡さない（ハルシネーション防止）。
            // 時刻は区間自身が持つため、無音を捨てても後続の時刻はずれない。
            foreach (var region in SplitVoicedRegions(chunk.Samples, SilenceCut))
            {
                if (ShouldStopRegionLoop(interruptible, token))
                {
                    break;
                }

                var regionStart = RegionStart(chunk.StartElapsed, region.Start);
                var samples = PadToMinimum(SliceRegion(chunk.Samples, region), MinWhisperSamples);
                TranscribeRegion(state, samples, regionStart, results, token);
            }
        }
        catch (OperationCanceledException)
        {
            // キャンセルによる中断は正常終了扱い
        }
        // CA1031: ワーカースレッド境界＋Whisper のネイティブ処理。例外を漏らすとプロセスごと
        //         落ちて録音中のセッションを失うため、全例外を Error イベントに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            Error?.Invoke($"文字起こしエラー: {ex.Message}");
        }
#pragma warning restore CA1031

        // 追記は catch の後ろに置く（finally ではない）。finally に置くと、ここで起きた
        // IOException が上の catch 節を通らずに TranscriptionLoop まで抜け、
        // ワーカースレッドごと落ちる。
        var failure = AppendTranscriptLines(_outputPath, results);
        if (failure != null)
        {
            Error?.Invoke($"文字起こし結果の書き出しに失敗しました: {failure}");
        }
    }

    /// <summary>
    /// 確定した行をテキストファイルへ追記する（REQ-TRX-07）。
    /// </summary>
    /// <returns>成功したら <c>null</c>。失敗したらユーザー向けの理由。</returns>
    /// <remarks>
    /// 失敗を例外ではなく戻り値で返すのは、呼び出し元（<see cref="ProcessChunk"/>）が
    /// キャンセル・Whisper の例外を処理し終えた **後** にここを通るためである。
    /// 例外で返すとワーカースレッドの境界を越えてしまう。
    /// </remarks>
    internal static string? AppendTranscriptLines(string outputPath, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        try
        {
            File.AppendAllLines(outputPath, lines, Encoding.UTF8);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 有声区間 1 つを Whisper に掛け、整形した行を results へ積む。
    /// regionStart は区間先頭の、セッション開始からの経過時間。
    /// </summary>
    private void TranscribeRegion(
        SourceState state, float[] samples, TimeSpan regionStart,
        List<string> results, CancellationToken token)
    {
        // ProcessAsync を同期的に消費
        var asyncEnum = state.Processor!.ProcessAsync(samples, token);
        var enumerator = asyncEnum.GetAsyncEnumerator(token);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var segment = enumerator.Current;
                var text = segment.Text?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var startTime = _sessionStartTime + regionStart + segment.Start;
                var endTime = _sessionStartTime + regionStart + segment.End;
                var line = $"[{startTime:HH:mm:ss} - {endTime:HH:mm:ss}] [{state.Label}] {text}";
                results.Add(line);
                SegmentTranscribed?.Invoke(line);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>停止要求後、ワーカーが残りを処理して自然に抜けるのを待つ時間。</summary>
    internal static readonly TimeSpan StopGraceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>キャンセル後、Whisper のネイティブ処理が抜けるのを待つ時間。</summary>
    internal static readonly TimeSpan StopCancelTimeout = TimeSpan.FromSeconds(10);

    public void StopSession()
    {
        _isRunning = false;

        // まず残りバッファ処理の完了を待つ
        bool workerExited = true;
        if (_thread != null)
        {
            workerExited = _thread.Join(StopGraceTimeout);
            if (!workerExited)
            {
                // タイムアウト時はキャンセルして終了を待ち直す
                _cts?.Cancel();
                workerExited = _thread.Join(StopCancelTimeout);
            }
        }
        _thread = null;

        _cts?.Dispose();
        _cts = null;

        foreach (var state in _sources.Values)
        {
            DisposeProcessorSafely(state, workerExited);
        }
        _sources.Clear();
    }

    /// <summary>
    /// <see cref="WhisperProcessor"/> を、プロセスを落とさずに破棄する。
    /// </summary>
    /// <remarks>
    /// ネイティブ処理の実行中に <c>Dispose()</c> を呼ぶと Whisper.net が
    /// <c>"Cannot dispose while processing, please use DisposeAsync instead."</c> を投げる。
    /// これは <c>Task.Run</c> 上で発生すると <c>AsyncRelayCommand</c> 経由で
    /// Dispatcher に再スローされ、未処理例外としてプロセスごと終了させる（T117 で実際に発生）。
    /// ワーカーが抜けていない場合は破棄を見送る。ネイティブリソースはプロセス終了時に
    /// 解放されるため、アプリを落とすよりリークを選ぶ。
    /// </remarks>
    private void DisposeProcessorSafely(SourceState state, bool workerExited)
    {
        var processor = state.Processor;
        if (processor == null)
        {
            return;
        }
        state.Processor = null;

        if (!workerExited)
        {
            Error?.Invoke(
                "文字起こしスレッドの停止がタイムアウトしました。Whisper リソースの解放を見送ります。");
            return;
        }

        try
        {
            processor.Dispose();
        }
        // CA1031: Whisper.net は状態不正を型付けされていない Exception で通知する。
        //         破棄の失敗でアプリを落とさない（ここが T117 のクラッシュ地点だった）。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            Error?.Invoke($"Whisper プロセッサの解放に失敗しました: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private void DisposeProcessor()
    {
        foreach (var state in _sources.Values)
        {
            // ここに来る時点でセッションは停止済み（ワーカーは動いていない）
            DisposeProcessorSafely(state, workerExited: true);
        }
        _sources.Clear();
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // アンマネージドリソースを直接は保持しない（Whisper.net 側が保持する）ため
    // ファイナライザーは持たず、disposing == false のときは何もしない。
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        StopSession();
        DisposeProcessor();
    }
}