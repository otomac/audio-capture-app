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

/// <summary>文字起こしの言語の選択肢 1 件（REQ-TRX-10）。</summary>
/// <param name="Code">Whisper へ渡す言語コード。<c>auto</c> は自動判定を表す。</param>
/// <param name="DisplayName">ドロップダウンに出す表示名。</param>
public sealed record TranscriptionLanguage(string Code, string DisplayName);

/// <summary>
/// 文字起こしの言語の一覧と正規化（REQ-TRX-10）。
/// </summary>
/// <remarks>
/// Whisper が扱う 99 言語をすべて並べることはしない。
/// **自動判定はファイル側にのみ置く** — ライブ経路は 20 秒チャンク（REQ-TRX-LIVE-10）で
/// 言語検出が誤りやすいと考えられるが、その誤検出率を実測していないためである。
/// 設定値は手編集され得るので、`SilenceCutOptions` などと同じく必ず正規化して使う。
/// </remarks>
public static class TranscriptionLanguages
{
    public const string Japanese = "ja";
    public const string English = "en";

    /// <summary>自動判定。Whisper へは言語コードではなく検出の有効化として渡す。</summary>
    public const string Auto = "auto";

    /// <summary>ライブ文字起こしの選択肢（REQ-TRX-LIVE-14）。自動判定は含めない。</summary>
    public static IReadOnlyList<TranscriptionLanguage> ForLive { get; } =
    [
        new(Japanese, "日本語"),
        new(English, "英語")
    ];

    /// <summary>ファイル文字起こしの選択肢（REQ-TRX-FILE-16）。自動判定を含む。</summary>
    public static IReadOnlyList<TranscriptionLanguage> ForFile { get; } =
    [
        new(Japanese, "日本語"),
        new(English, "英語"),
        new(Auto, "自動判定")
    ];

    /// <summary>ライブ用に正規化する。未知のコードと <c>auto</c> は日本語へ倒す。</summary>
    public static string NormalizeForLive(string? code) => Normalize(code, ForLive);

    /// <summary>ファイル用に正規化する。未知のコードは日本語へ倒す。</summary>
    public static string NormalizeForFile(string? code) => Normalize(code, ForFile);

    private static string Normalize(string? code, IReadOnlyList<TranscriptionLanguage> allowed)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Japanese;
        }

        var trimmed = code.Trim();
        foreach (var language in allowed)
        {
            if (string.Equals(language.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return language.Code;
            }
        }

        return Japanese;
    }
}

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

/// <summary>
/// ファイル文字起こし 1 回分の指定。オプション指定ダイアログ（REQ-TRX-FILE-09）で「開始」を押した時点の値。
/// </summary>
/// <param name="StartOffset">
/// 出力行のタイムスタンプの起点（REQ-TRX-FILE-10）。ファイル先頭がこの時刻に録音されたものとして扱う。
/// 未指定なら <see cref="TimeSpan.Zero"/>（＝ファイル先頭からの経過時間になる）。
/// </param>
/// <param name="Language">
/// Whisper へ渡す言語（REQ-TRX-FILE-16）。<c>auto</c> なら自動判定。話者識別の有無にかかわらず同じ値を使う。
/// </param>
/// <param name="KnownSpeakerCount">
/// ダイアログで選んだ話者人数（REQ-TRX-DIA-17）。<c>null</c> なら未選択で、設定値に倒れる（REQ-TRX-DIA-07）。
/// 話者識別を通さないときは使われない。
/// </param>
/// <param name="ModelPath">
/// この実行で使う Whisper モデルの実パス（REQ-TRX-FILE-17）。ライブ用に読み込み済みのモデルと同じなら共有し、
/// 違えば実行の間だけ 2 つ目の <c>WhisperFactory</c> を作る。<c>null</c> ならライブ用をそのまま使う。
/// </param>
/// <param name="UseGpu">2 つ目の factory を作るときの GPU 使用有無。ライブ側の設定に従う（REQ-GPU-01 / 02）。</param>
/// <param name="MeetingName">
/// 会議名（REQ-TRX-FILE-18）。空でなければ出力ファイル名に `_会議名` を挟む（REQ-TRX-FILE-05 / REQ-META-02）。
/// </param>
public sealed record FileTranscriptionOptions(
    TimeSpan StartOffset,
    string Language,
    int? KnownSpeakerCount,
    string? ModelPath = null,
    bool UseGpu = true,
    string? MeetingName = null);

/// <summary>ファイル文字起こしの結末（REQ-TRX-FILE-17）。</summary>
public enum FileTranscriptionOutcome
{
    /// <summary>最後まで書き出した。</summary>
    Completed,

    /// <summary>モデルを読み込めず、処理を始めなかった。ダイアログは閉じない。</summary>
    ModelLoadFailed,

    /// <summary>処理中に失敗した（理由は <c>Error</c> イベントにも出している）。</summary>
    Failed
}

/// <summary>
/// <see cref="TranscriptionService.TranscribeFileAsync"/> の戻り値。
/// </summary>
/// <param name="Outcome">結末。</param>
/// <param name="Message"><see cref="FileTranscriptionOutcome.ModelLoadFailed"/> のときの理由。それ以外は <c>null</c>。</param>
public sealed record FileTranscriptionResult(FileTranscriptionOutcome Outcome, string? Message = null)
{
    /// <summary>最後まで書き出せたか。</summary>
    public bool Success => Outcome == FileTranscriptionOutcome.Completed;
}

public class TranscriptionService : IDisposable
{
    /// <summary>
    /// Whisper へ渡す 1 チャンク。<paramref name="StartElapsed"/> はセッション開始からの
    /// 経過時間で、チャンク先頭サンプルが録音された時刻を指す。
    /// </summary>
    internal sealed record PendingChunk(float[] Samples, TimeSpan StartElapsed);

    internal sealed class SourceState
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

    /// <summary>
    /// <see cref="_factory"/> に読み込んだモデルの実パス（REQ-TRX-FILE-17 の共有判定に使う）。
    /// 未読み込みなら <c>null</c>。
    /// </summary>
    private string? _loadedModelPath;

    private readonly Dictionary<AudioSourceType, SourceState> _sources = new();

    /// <summary>
    /// <see cref="_sources"/> の要素の増減（登録・セッション停止・破棄）を <see cref="PendingSeconds"/> の
    /// 列挙と競合させないための錠。ワーカーは要素を増減しないので、ワーカーの列挙は錠を取らない。
    /// </summary>
    private readonly Lock _sourcesLock = new();
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
    /// ライブ文字起こしで、遅れていないときのチャンクの上限（10 秒分。REQ-TRX-LIVE-10、T190）。
    /// 遅れているときは <see cref="BufferThresholdSamples"/>（20 秒分）に戻す。
    /// </summary>
    /// <remarks>
    /// 話し続けていると末尾無音の契機（REQ-TRX-LIVE-13）が来ないため、確定はこの上限ごとにしか出ない。
    /// 20 秒では遅すぎるので 10 秒にする。1 回の呼び出しが短いほど割高になる（音声 1 秒あたり 20 秒入力 0.181 秒 /
    /// 5 秒入力 0.381 秒）ので、遅れているときは処理能力を優先して 20 秒に戻す。
    /// </remarks>
    internal const int LiveChunkSamples = TargetRate * 10;

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

    /// <summary>
    /// ライブ文字起こしの言語（REQ-TRX-LIVE-14）。MainViewModel が設定から反映する。
    /// **読まれるのは録音開始時の <see cref="RegisterSource"/> だけ**なので、
    /// 変更は次に録音を開始したときから効く。
    /// </summary>
    public string LiveLanguage { get; set; } = TranscriptionLanguages.Japanese;

    /// <summary>
    /// 言語の指定を <see cref="WhisperProcessorBuilder"/> へ載せる（REQ-TRX-10）。
    /// 自動判定は言語コードではなく専用の API で有効化する。
    /// </summary>
    private static WhisperProcessorBuilder WithLanguageOption(WhisperProcessorBuilder builder, string language)
        => string.Equals(language, TranscriptionLanguages.Auto, StringComparison.OrdinalIgnoreCase)
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(language);

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
            _loadedModelPath = modelPath;

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

    /// <summary>
    /// 読み込み済みのモデルを破棄して「未読み込み」へ戻す（REQ-MODELWIN-04）。
    /// 選択中のモデルが一覧から削除されたときに使う。セッション停止後にのみ呼ぶこと。
    /// </summary>
    public void UnloadModel() => DisposeProcessor();

    public void RegisterSource(AudioSourceType type, string label, int sourceRate, int sourceChannels)
    {
        // 既存のプロセッサがあれば破棄（登録はセッション開始前なのでワーカーは動いていない）
        if (_sources.TryGetValue(type, out var existing))
        {
            DisposeProcessorSafely(existing, workerExited: true);
        }

        // α = 2π·fc / (2π·fc + sourceRate),  fc = TargetRate / 2
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        var state = new SourceState
        {
            SourceRate = sourceRate,
            SourceChannels = sourceChannels,
            Label = label,
            Processor = WithLanguageOption(_factory!.CreateBuilder(), LiveLanguage).Build(),
            LpfAlpha = alpha
        };
        lock (_sourcesLock)
        {
            _sources[type] = state;
        }
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
        _abortRequested = false;
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
                // ここまでを確定し、以降は新しい基準時刻で積み直す。遅れているとバッファが
                // 数分分に膨らんでいることがあるため、20 秒分ずつに分けて積む（REQ-TRX-LIVE-10、T183）。
                var bufferStart = ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count);
                foreach (var chunk in SplitAtChunkLimit(state.Pcm16kBuffer, bufferStart))
                {
                    state.Ready.Enqueue(chunk);
                }
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
    /// ギャップで確定するバッファを、先頭から <see cref="BufferThresholdSamples"/>（20 秒分）ずつのチャンクに分ける。
    /// </summary>
    /// <param name="buffer">16kHz モノラルのバッファ。中にギャップは無い（<see cref="ShouldSplitOnGap"/> が保証する）。</param>
    /// <param name="bufferStart">バッファ先頭サンプルの経過時間。</param>
    /// <remarks>
    /// バッファ内は地続きなので、各チャンクの先頭時刻はバッファ先頭の時刻にチャンク内の位置を足せば求まる
    /// （<see cref="RegionStart"/> と同じ式）。20 秒分以下なら、従来どおりバッファ全体を 1 チャンクにする。
    /// </remarks>
    internal static List<PendingChunk> SplitAtChunkLimit(List<float> buffer, TimeSpan bufferStart)
    {
        var chunks = new List<PendingChunk>();
        for (int offset = 0; offset < buffer.Count; offset += BufferThresholdSamples)
        {
            int count = Math.Min(BufferThresholdSamples, buffer.Count - offset);
            var samples = new float[count];
            buffer.CopyTo(offset, samples, 0, count);
            chunks.Add(new PendingChunk(samples, RegionStart(bufferStart, offset)));
        }

        return chunks;
    }

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
    /// <param name="options">開始時刻・言語・話者人数（<see cref="FileTranscriptionOptions"/>）。</param>
    /// <remarks>
    /// <see cref="FileTranscriptionOptions.StartOffset"/> は進捗（<paramref name="progress"/>）には足さない。
    /// 進捗は残りの目安であって時刻ではないため、常にファイル先頭基準で報告する。
    /// </remarks>
    public async Task<FileTranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        FileTranscriptionOptions options,
        SpeakerDiarizationService? diarization,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        // REQ-TRX-FILE-17: ライブ用と同じモデルなら共有し、違えばこの実行の間だけ 2 つ目を作る。
        WhisperFactory factory;
        WhisperFactory? ownFactory = null;
        if (ShouldShareLiveFactory(options.ModelPath, _loadedModelPath, _factory != null))
        {
            factory = _factory!;
        }
        else
        {
            var modelPath = options.ModelPath ?? _loadedModelPath;
            if (string.IsNullOrEmpty(modelPath))
            {
                return new FileTranscriptionResult(
                    FileTranscriptionOutcome.ModelLoadFailed, "Whisperモデルが選ばれていません。");
            }

            progress?.Report(new FileTranscriptionProgress(LoadModelPhase, TimeSpan.Zero, TimeSpan.Zero));
            var (loaded, loadError) = TryCreateFactory(modelPath, options.UseGpu);
            if (loaded == null)
            {
                return new FileTranscriptionResult(FileTranscriptionOutcome.ModelLoadFailed, loadError);
            }
            ownFactory = loaded;
            factory = loaded;
        }

        string outputPath = BuildTranscriptPath(audioFilePath, options.MeetingName);
        var startOffset = options.StartOffset;
        var language = options.Language;
        try
        {
            ct.ThrowIfCancellationRequested();

            // diarization が null なら従来どおりストリーミングで処理する（REQ-TRX-DIA-03）。
            // 非 null のときだけ、音声全体をメモリへ載せる経路へ分岐する（NFR-07）。
            var work = diarization == null
                ? TranscribeFileCoreAsync(factory, audioFilePath, outputPath, startOffset, language, progress, ct)
                : TranscribeFileWithDiarizationAsync(
                    factory, audioFilePath, outputPath, startOffset, language, options.KnownSpeakerCount,
                    diarization, progress, ct);
            var ok = await work.ConfigureAwait(false);
            return new FileTranscriptionResult(ok ? FileTranscriptionOutcome.Completed : FileTranscriptionOutcome.Failed);
        }
        catch (OperationCanceledException)
        {
            // 部分出力ファイルは削除する（ユーザーが完結したと誤認しないように）。
            // Core を抜ける時点で using が StreamWriter を破棄済みのため、ここで削除できる。
            DeletePartialOutput(outputPath, diarization);
            throw;
        }
        // CA1031: キャンセル判定のため全例外をいったん見る必要がある（次行のコメント参照）。
        //         キャンセル以外は Error イベントに変換して Failed を返す。
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
            return new FileTranscriptionResult(FileTranscriptionOutcome.Failed);
        }
#pragma warning restore CA1031
        finally
        {
            // 2 つ目の factory はこの実行の間だけ（完了・失敗・中止のいずれでも破棄する）
            ownFactory?.Dispose();
        }
    }

    /// <summary>進捗表示に出すフェーズ名（REQ-TRX-FILE-17。2 つ目のモデルを読み込んでいる間）。</summary>
    internal const string LoadModelPhase = "モデル読み込み中";

    /// <summary>
    /// ライブ用に読み込み済みの factory を共有できるか（REQ-TRX-FILE-17）。
    /// 指定が無い（<c>null</c>）か、実パスが読み込み済みのモデルと同じで、かつ読み込み済みなら共有する。
    /// </summary>
    internal static bool ShouldShareLiveFactory(string? requestedPath, string? loadedPath, bool isLoaded)
    {
        if (!isLoaded || string.IsNullOrEmpty(loadedPath))
        {
            return false;
        }

        return requestedPath == null
            || string.Equals(requestedPath.Trim(), loadedPath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ファイル文字起こし用の 2 つ目の factory を作る（REQ-TRX-FILE-17）。
    /// ライブ用の <see cref="LoadModel"/> と同じく <c>CreateBuilder()</c> を 1 度呼んで失敗を確定させる（T122）。
    /// </summary>
    /// <returns>成功なら factory、失敗なら理由。</returns>
    private static (WhisperFactory? Factory, string? Error) TryCreateFactory(string modelPath, bool useGpu)
    {
        if (!File.Exists(modelPath))
        {
            return (null, $"モデルファイルが見つかりません: {modelPath}");
        }

        WhisperFactory? factory = null;
        try
        {
            factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
            _ = factory.CreateBuilder();
            return (factory, null);
        }
        // CA1031: Whisper のネイティブ読み込みは環境依存の任意の例外を投げる。
        //         失敗は理由として呼び出し元へ返し、ダイアログに表示する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            factory?.Dispose();
            return (null, $"Whisperモデル読み込み失敗 ({Path.GetFileName(modelPath)}): {ex.Message}");
        }
#pragma warning restore CA1031
    }

    // 破棄対象（reader / writer / processor）を using で束ねるために本体を切り出している。
    // 例外は using による破棄が完了してから呼び出し元へ伝播する。
    private async Task<bool> TranscribeFileCoreAsync(
        WhisperFactory factory,
        string audioFilePath,
        string outputPath,
        TimeSpan startOffset,
        string language,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        await using var reader = OpenAudioFile(audioFilePath);
        int sourceRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        TimeSpan totalTime = reader.TotalTime;
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
        await using var processor = WithLanguageOption(factory.CreateBuilder(), language).Build();

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

    // internal なのは、中止の注記をどちらの文言にするかを ViewModel がフェーズで
    // 決めるためである（REQ-TRX-FILE-07）。表示名をここだけで持ち、写しを作らない。

    /// <summary>進捗表示に出すフェーズ名（REQ-TRX-FILE-06）。</summary>
    internal const string TranscribePhase = "処理中";

    /// <summary>進捗表示に出すフェーズ名（話者ダイアライゼーション有効時のみ現れる）。</summary>
    internal const string DiarizePhase = "話者識別中";

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
                    var text = segment.Text?.Trim();
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

    /// <summary>
    /// 音声ファイルを開く（REQ-TRX-FILE-03）。3 か所のデコード経路はすべてここを通る。
    /// </summary>
    /// <remarks>
    /// <see cref="AudioFileReader"/> は `.wav` 以外を Media Foundation（OS 標準デコーダー）へ委ねる。
    /// Windows の N エディションでは AAC デコーダーが無く `.m4a` を開けないため、
    /// 失敗の原因が環境かファイルかを切り分けられるよう、**メッセージに形式（拡張子）を含める**（T161）。
    /// Media Foundation は形式ごとに異なる COM 例外を投げ、型を列挙できないため全例外を包む。
    /// </remarks>
    /// <exception cref="InvalidOperationException">開けなかった。原因は内部例外。</exception>
    internal static AudioFileReader OpenAudioFile(string audioFilePath)
    {
        try
        {
            return new AudioFileReader(audioFilePath);
        }
        // CA1031: 対象は利用者が選んだ任意のファイルで、NAudio / Media Foundation は形式ごとに
        //         異なる例外（COMException・FileNotFoundException・ArgumentException 等）を投げる。
        //         どれであっても「開けなかった」として形式名付きのメッセージに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            throw new InvalidOperationException(OpenFailureMessage(audioFilePath, ex.Message), ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>「音声ファイルを開けなかった」ときのメッセージ。形式（拡張子）を必ず含める。</summary>
    internal static string OpenFailureMessage(string audioFilePath, string reason)
    {
        var ext = Path.GetExtension(audioFilePath);
        var format = string.IsNullOrEmpty(ext) ? "拡張子なし" : ext;
        var hint = string.Equals(format, ".m4a", StringComparison.OrdinalIgnoreCase)
            ? " Windows の N エディションでは Media Feature Pack が必要です。"
            : "";
        return $"音声ファイルを開けませんでした (形式: {format}): {reason}{hint}";
    }

    // {入力ファイル名}.transcript.txt を同じフォルダに配置
    // 例: audio.mp3 → audio.transcript.txt
    // （録音時に生成される audio.txt と名前衝突しないように）
    internal static string BuildTranscriptPath(string audioFilePath)
    {
        return Path.ChangeExtension(audioFilePath, ".transcript.txt");
    }

    /// <summary>
    /// 会議名付きの出力パス（REQ-TRX-FILE-05 / REQ-META-02）。`audio.mp3` + `定例` → `audio_定例.transcript.txt`。
    /// 会議名が空（または整形して空）なら会議名なしと同じ。
    /// </summary>
    internal static string BuildTranscriptPath(string audioFilePath, string? meetingName)
        => RecordingMetadataFile.WithMeetingName(BuildTranscriptPath(audioFilePath), meetingName);

    /// <summary>
    /// 音声ファイルの作成日時・最終更新日時を読む（REQ-TRX-FILE-15 の②③）。
    /// </summary>
    /// <returns>読めたら <c>true</c>。ファイルが無い・アクセスできない場合は <c>false</c>。</returns>
    internal static bool TryGetAudioFileTimes(
        string audioFilePath, out DateTime creationTime, out DateTime lastWriteTime)
    {
        creationTime = default;
        lastWriteTime = default;
        try
        {
            var info = new FileInfo(audioFilePath);
            if (!info.Exists)
            {
                return false;
            }
            creationTime = info.CreationTime;
            lastWriteTime = info.LastWriteTime;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 音声ファイルの長さ（全長）を読む。開始時刻の推定（REQ-TRX-FILE-15 の③）で使う。
    /// </summary>
    /// <returns>読めたら <c>true</c>。開けない・壊れている場合は <c>false</c>（例外は投げない）。</returns>
    /// <remarks>
    /// **呼ぶのは③に落ちたときだけにすること。** <see cref="AudioFileReader"/> は MP3 の
    /// フレーム表を作るためにファイル全体を走査するため、長いファイルでは無視できない時間がかかる。
    /// 返すのは**無音カット前のファイル全長**である（録音終了時刻からの逆算に使うため）。
    /// </remarks>
    internal static bool TryGetAudioDuration(string audioFilePath, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            using var reader = OpenAudioFile(audioFilePath);
            duration = reader.TotalTime;
            return duration > TimeSpan.Zero;
        }
        // CA1031: 対象は利用者が選んだ任意のファイルで、NAudio は形式ごとに異なる例外を投げる。
        //         ここは「推定できたら入れる」だけの補助機能なので、失敗は静かに諦める
        //         （REQ-TRX-FILE-15: 推定できなくてもエラーにしない）。
#pragma warning disable CA1031
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
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
                       && (chunk = TakeNextChunk(state, _sessionClock.Elapsed, SilenceCut, PendingBufferedSamples())) != null)
                {
                    // 停止要求が来ても、取り出したチャンクは最後の区間まで処理する（T184）。
                    // バッファからは既に消えているので、途中で抜けると残りの区間を捨てることになる。
                    ProcessChunkCounted(chunk, state, token);
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
                       && (chunk = TakeNextChunk(state, _sessionClock.Elapsed, SilenceCut, PendingBufferedSamples())) != null)
                {
                    // 排出処理。打ち切りたいときは token をキャンセルする（StopSession の「打ち切り」）。
                    ProcessChunkCounted(chunk, state, token);
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
                    ProcessChunkCounted(tail, state, token);
                }
            }
        }
    }

    /// <summary>処理中のチャンクのサンプル数（<see cref="PendingSeconds"/> に含めるため）。</summary>
    private long _inFlightSamples;

    /// <summary><see cref="ProcessChunk"/> を、処理中のサンプル数を数えながら呼ぶ。</summary>
    private void ProcessChunkCounted(PendingChunk chunk, SourceState state, CancellationToken token)
    {
        Interlocked.Exchange(ref _inFlightSamples, chunk.Samples.Length);
        try
        {
            ProcessChunk(chunk, state, token);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlightSamples, 0);
        }
    }

    /// <summary>
    /// 確定済みチャンクを 1 つ取り出す。無ければバッファから 1 チャンク切り出す。
    /// どちらも無ければ <c>null</c>。
    /// </summary>
    /// <param name="pendingSamples">
    /// 全ソースの確定待ちの合計（<see cref="PendingBufferedSamples"/>）。遅れているかの判定に使う（REQ-TRX-LIVE-13）。
    /// </param>
    /// <remarks>
    /// 遅れの判定に <paramref name="state"/> 自身の分だけを使ってはいけない（T183）。確定済みチャンクは
    /// 先に返すので判定時には残っておらず、未確定バッファも上限（10 秒分または 20 秒分）以上なら先に上限で切り出すため、
    /// 判定時の自ソース分は常に 20 秒未満で、60 秒の閾値に届かない。
    /// <paramref name="pendingSamples"/> はこの錠の外で数えて渡す。中で数えると
    /// <see cref="PendingSeconds"/>（<c>_sourcesLock</c> → 各ソースの錠）と錠の順序が逆になる。
    /// </remarks>
    internal static PendingChunk? TakeNextChunk(
        SourceState state, TimeSpan nowElapsed, SilenceCutOptions options, long pendingSamples)
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

            // REQ-TRX-LIVE-13: 遅れているときは末尾無音での早期確定を使わない。
            // 20 秒に満たないチャンクは 1 回あたりの効率が悪く（実測: 音声 1 秒あたり
            // 20 秒入力 0.181 秒 / 5 秒入力 0.381 秒）、遅れをさらに広げるため。
            var start = ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count);
            int take = ChunkTakeCount(
                state.Pcm16kBuffer.Count,
                nowElapsed - state.BufferEndElapsed,
                TrailingSilenceSamples(state.Pcm16kBuffer, options.RmsThreshold),
                SecondsToSamples(options.MergeGapSeconds),
                pendingSamples >= BacklogSuppressEndpointingSamples);
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
    /// <param name="behind">
    /// 文字起こしが遅れているか（全ソースの滞留が <see cref="BacklogSuppressEndpointingSamples"/> 以上。REQ-TRX-LIVE-13）。
    /// </param>
    /// <remarks>
    /// 契機は 3 つあり、この優先順で判定する。
    /// <list type="number">
    /// <item>上限までたまった → 上限の分だけ切り出す（1 回の Whisper 呼び出しを
    /// 際限なく長くしないための上限。T117）。上限は遅れていなければ <see cref="LiveChunkSamples"/>（10 秒分）、
    /// 遅れていれば <see cref="BufferThresholdSamples"/>（20 秒分。T190）。</item>
    /// <item>末尾に <paramref name="endpointSilenceSamples"/> 以上の無音が積まれ、かつ
    /// バッファ内に有声窓がある → 発話が終わったとみなしてバッファ全部を切り出す（T129）。</item>
    /// <item>供給が <see cref="StaleSupplyIdle"/> 以上途絶えている → バッファ全部を切り出す（T120）。</item>
    /// </list>
    /// <para>
    /// <paramref name="behind"/>（遅れている）のときは、1 の上限を 20 秒分に戻し、2 を使わない（REQ-TRX-LIVE-13）。
    /// どちらも 1 回あたりの効率を優先するためである。
    /// </para>
    /// <para>
    /// 2 が無いと、マイクは無音でも WASAPI がサンプルを供給し続けるため
    /// ギャップ分割（<see cref="ShouldSplitOnGap"/>）も 3 も発火せず、出力粒度が上限で固定になる。
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
        int endpointSilenceSamples,
        bool behind = false)
    {
        // REQ-TRX-LIVE-10: 遅れていなければ 10 秒、遅れていれば 20 秒で切る（T190）。
        int limit = behind ? BufferThresholdSamples : LiveChunkSamples;
        if (bufferedSampleCount >= limit)
        {
            return limit;
        }

        // REQ-TRX-LIVE-13: 遅れているときは②を使わない。20 秒たまるまで待って効率を優先する。
        // ③（供給の途絶）は残す — 供給が止まったソースを無期限に抱え込まないための契機であり、
        // 遅れているかどうかとは関係がない。
        if (behind)
        {
            return supplyIdle >= StaleSupplyIdle && bufferedSampleCount >= MinTailSamples
                ? bufferedSampleCount
                : 0;
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
    /// <remarks>
    /// 区間ループは停止要求（<see cref="_isRunning"/> が false）では抜けず、キャンセルでだけ抜ける（T184）。
    /// チャンクは <see cref="TakeNextChunk"/> でバッファから取り除き済みなので、途中で抜けると
    /// 残りの区間はどこにも残らず捨てられる。かつて（T127）は <see cref="StopSession"/> の猶予 30 秒を
    /// 超えないよう停止要求で抜けていたが、T165 で猶予を無くして「捨てない」にしたため不要になった。
    /// </remarks>
    private void ProcessChunk(
        PendingChunk chunk, SourceState state, CancellationToken token)
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
                // ここで _isRunning を見てはいけない（T184。remarks を参照）。
                if (token.IsCancellationRequested)
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

    /// <summary>これ以上の滞留があれば「遅れている」とみなし、効率を優先する（REQ-TRX-LIVE-13）。</summary>
    internal const int BacklogSuppressEndpointingSamples = TargetRate * 60;

    /// <summary>打ち切り要求後、Whisper のネイティブ処理が抜けるのを待つ時間。</summary>
    internal static readonly TimeSpan StopCancelTimeout = TimeSpan.FromSeconds(10);

    /// <summary>停止処理中に「打ち切り」が要求されたか。ワーカーの終了待ちをこの間隔で見直す。</summary>
    private volatile bool _abortRequested;
    private static readonly TimeSpan AbortPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 停止処理の「打ち切り」（REQ-TRX-LIVE-11）。滞留分を捨ててキャンセルし、10 秒だけ待つ。
    /// 停止処理中でなければ何もしない。UI スレッドから呼ばれ、待っている <see cref="StopSession"/> が拾う。
    /// </summary>
    public void RequestAbort() => _abortRequested = true;

    /// <summary>
    /// セッションを停止する。**滞留分を吐き切るまで待つ**（REQ-TRX-LIVE-11）。
    /// <see cref="RequestAbort"/> が来たら、キャンセルして <see cref="StopCancelTimeout"/> だけ待つ。
    /// </summary>
    /// <remarks>
    /// かつては 30 秒で打ち切っていた（T117 のクラッシュ対策）が、文字起こしが遅延した状態で止めると
    /// 残りが黙って捨てられる（T165 の実測: 150 秒中 113.8 秒分）。上限を無くし、打ち切りは利用者の明示に限る。
    /// </remarks>
    public void StopSession()
    {
        _isRunning = false;

        // 残りバッファ処理の完了を待つ（打ち切り要求が来るまで上限なし）
        bool workerExited = true;
        if (_thread != null)
        {
            while (!(workerExited = _thread.Join(AbortPollInterval)))
            {
                if (_abortRequested)
                {
                    // 打ち切り: キャンセルして終了を待ち直す
                    _cts?.Cancel();
                    workerExited = _thread.Join(StopCancelTimeout);
                    break;
                }
            }
        }
        _thread = null;
        _abortRequested = false;

        _cts?.Dispose();
        _cts = null;

        foreach (var state in _sources.Values)
        {
            DisposeProcessorSafely(state, workerExited);
        }
        lock (_sourcesLock)
        {
            _sources.Clear();
        }
    }

    /// <summary>
    /// まだ Whisper に渡していない音声の長さ（秒。全ソースの確定済みチャンクと未確定バッファの合計）。
    /// 停止処理中に「文字起こしの残り」を表示するために UI スレッドから読む（REQ-TRX-LIVE-11）。
    /// </summary>
    public double PendingSeconds
        => (double)(PendingBufferedSamples() + Interlocked.Read(ref _inFlightSamples)) / TargetRate;

    /// <summary>
    /// 全ソースの確定済みチャンクと未確定バッファのサンプル数の合計（処理中のチャンクは含まない）。
    /// UI スレッド（<see cref="PendingSeconds"/>）とワーカー（遅れの判定。REQ-TRX-LIVE-13）から読む。
    /// </summary>
    private long PendingBufferedSamples()
    {
        long samples = 0;
        lock (_sourcesLock)
        {
            foreach (var state in _sources.Values)
            {
                samples += BufferedSamples(state);
            }
        }

        return samples;
    }

    /// <summary>1 ソースの確定済みチャンクと未確定バッファのサンプル数の合計。</summary>
    internal static long BufferedSamples(SourceState state)
    {
        lock (state.BufferLock)
        {
            long samples = state.Pcm16kBuffer.Count;
            foreach (var chunk in state.Ready)
            {
                samples += chunk.Samples.Length;
            }

            return samples;
        }
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
        lock (_sourcesLock)
        {
            _sources.Clear();
        }
        _factory?.Dispose();
        _factory = null;
        _loadedModelPath = null;
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
        // プロセス終了では滞留分を待たない（閉じる経路は ShutdownAsync が先に待っている）
        RequestAbort();
        StopSession();
        DisposeProcessor();
    }
}