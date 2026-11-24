using System.Diagnostics;
using Whisper.net;

namespace AudioCaptureApp.Services;

public enum AudioSourceType { Mic, Speaker }

/// <summary>
/// 確定パスがチャンクを 1 つ処理し終えたことの通知（REQ-TRX-LIVE-18）。
/// </summary>
/// <param name="SessionId">録音ごとのセッション ID（<see cref="TranscriptionService.CurrentSessionId"/>）。</param>
/// <param name="Source">音源。</param>
/// <param name="EndPosition">チャンク終端の絶対位置（音源ごとの 16kHz 換算の累計サンプル数）。ここまでの音声は確定済み。</param>
public readonly record struct LiveChunkCompletion(long SessionId, AudioSourceType Source, long EndPosition);

public partial class TranscriptionService : IDisposable
{
    /// <summary>
    /// Whisper へ渡す 1 チャンク。<paramref name="StartElapsed"/> はセッション開始からの
    /// 経過時間で、チャンク先頭サンプルが録音された時刻を指す。
    /// </summary>
    /// <param name="StartPosition">
    /// チャンク先頭サンプルの絶対位置（音源ごとの 16kHz 換算の累計サンプル数。REQ-TRX-LIVE-18）。
    /// 速報と確定の対応づけに使う。時刻ではないので、供給の途切れでは進まない。
    /// </param>
    internal sealed record PendingChunk(float[] Samples, TimeSpan StartElapsed, long StartPosition = 0)
    {
        /// <summary>チャンク終端（最後のサンプルの次）の絶対位置。</summary>
        public long EndPosition => StartPosition + Samples.Length;
    }

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

        /// <summary>
        /// <see cref="Pcm16kBuffer"/> の先頭サンプルの絶対位置（REQ-TRX-LIVE-18）。
        /// バッファからチャンクを取り出すたびに、取り出した長さだけ進める。
        /// </summary>
        public long BufferStartPosition;

        /// <summary>この状態がどの音源のものか（確定完了の通知に載せる）。</summary>
        public AudioSourceType Type;

        /// <summary>速報の写し（REQ-TRX-LIVE-15）。この音源が速報の対象でなければ <c>null</c>。録音開始時に決まる。</summary>
        public ProvisionalSource? Provisional;

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

    public event Action<string>? Error;
    public event Action<string>? SegmentTranscribed;
    public event Action<string>? RuntimeInfo;

    /// <summary>
    /// 確定パスがチャンクを 1 つ処理し終えた（REQ-TRX-LIVE-18）。0 行・キャンセル・例外で終わったときも発火する。
    /// **文字起こしワーカースレッドから発火する。** 受け手は UI に触れる前に Dispatcher を経由すること（NFR-01）。
    /// </summary>
    public event Action<LiveChunkCompletion>? LiveChunkCompleted;

    /// <summary>
    /// 録音ごとのセッション ID（REQ-TRX-LIVE-18）。<see cref="StartSession"/> のたびに 1 つ増える。
    /// 前のセッションの通知を受け手が捨てるために使う。
    /// </summary>
    public long CurrentSessionId => Interlocked.Read(ref _sessionId);

    private long _sessionId;

    /// <summary>
    /// 計測ログを書くか（REQ-CFG-12）。MainViewModel が設定から反映する。読まれるのは <see cref="StartSession"/> だけ。
    /// </summary>
    public bool MetricsLogEnabled { get; set; }

    /// <summary>録音中だけ開いている計測ログ。無効なら <c>null</c>。</summary>
    private TranscriptionMetricsLog? _metrics;

    public bool IsModelLoaded => _factory != null;

    /// <summary>無音カットの調整値。MainViewModel が設定から反映する。</summary>
    public SilenceCutOptions SilenceCut { get; set; } = SilenceCutOptions.Default;

    /// <summary>
    /// ライブの確定パスの区切り（遅れていないときの上限と発話終了の無音。REQ-CFG-15、ADR-0009）。
    /// MainViewModel が設定から反映する。ポーリングのたびに読む。
    /// </summary>
    /// <remarks>
    /// 話し続けていると発話終了の契機（REQ-TRX-LIVE-13）が来ないため、確定は上限ごとにしか出ない。
    /// 上限を短くすると確定は早く出るが、1 回の呼び出しが短いほど割高になる（音声 1 秒あたり 20 秒入力 0.181 秒 /
    /// 5 秒入力 0.381 秒）ので、遅れているときは処理能力を優先して 20 秒（<see cref="BufferThresholdSamples"/>）に戻す。
    /// </remarks>
    public LiveChunkOptions LiveChunking { get; set; } = LiveChunkOptions.Default;

    /// <summary>
    /// ライブ文字起こしの言語（REQ-TRX-LIVE-14）。MainViewModel が設定から反映する。
    /// **読まれるのは録音開始時の <see cref="RegisterSource"/> だけ**なので、
    /// 変更は次に録音を開始したときから効く。
    /// </summary>
    public string LiveLanguage { get; set; } = TranscriptionLanguages.Japanese;

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
        DisposeProvisionalModel();
    }
}