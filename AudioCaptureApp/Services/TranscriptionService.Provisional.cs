using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Whisper.net;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、速報パス（REQ-TRX-LIVE-15〜17、ADR-0007 案 B）を担当する部分。
// 速報は CPU 専用の別スレッド ProvisionalTranscription で、確定パスと並列に回す。GPU には触れない（REQ-GPU-06）。
// 速報に関わるコードはこのファイルにまとめ、確定パス（TranscriptionService.cs）からは写しへの積み込み・確定位置・停止だけを呼ぶ。
public partial class TranscriptionService
{
    /// <summary>
    /// 速報の対象の音源 1 つ分の状態。確定パスのバッファとは別の写しを持つ（T186 D2）。
    /// </summary>
    internal sealed class ProvisionalSource(double maxPhraseSeconds)
    {
        /// <summary><see cref="Audio"/> と <see cref="AudioStart"/> を守る錠。キャプチャのコールバックと速報スレッドが取る。</summary>
        public readonly Lock Lock = new();

        /// <summary>16kHz モノラルの写し。今の句の先頭より前は速報スレッドが捨てる。</summary>
        public readonly List<float> Audio = [];

        /// <summary><see cref="Audio"/>[0] の絶対位置（REQ-TRX-LIVE-18）。</summary>
        public long AudioStart;

        /// <summary>確定済みの位置（REQ-TRX-LIVE-18 の通知と同じ値）。確定パスのワーカーが書き、速報スレッドが読む。</summary>
        public long ConfirmedEnd;

        /// <summary>句の区切り。速報スレッドだけが触る。</summary>
        public readonly ProvisionalPhrasePlanner Planner = new(maxPhraseSeconds);
    }

    /// <summary>1 セッション分の速報の実行条件（録音開始時に固定する）。</summary>
    private sealed record ProvisionalRun(
        WhisperFactory Factory,
        IReadOnlyList<SourceState> Targets,
        ProvisionalOptions Options,
        string Language,
        double RmsThreshold,
        long SessionId,
        CancellationToken Token);

    /// <summary>速報の最大トークン数（REQ-TRX-LIVE-17。T182 の E4）。</summary>
    internal const int ProvisionalMaxTokens = 80;

    /// <summary>
    /// 速報を通知する（REQ-TRX-LIVE-15）。**速報スレッドから発火する。** 受け手は UI に触れる前に Dispatcher を経由すること（NFR-01）。
    /// </summary>
    public event Action<ProvisionalUpdate>? ProvisionalTranscribed;

    /// <summary>速報を有効にするか（REQ-CFG-12 の <c>LiveProvisionalEnabled</c>）。読まれるのは <see cref="StartSession"/> だけ。</summary>
    public bool ProvisionalEnabled { get; set; }

    /// <summary>速報の調整値（REQ-CFG-12）。読まれるのは <see cref="StartSession"/> だけ。</summary>
    public ProvisionalOptions Provisional { get; set; } = ProvisionalOptions.Default;

    /// <summary>速報用モデルが読み込み済みか。</summary>
    public bool IsProvisionalModelLoaded => _provisionalFactory != null;

    /// <summary>速報用モデル（CPU。REQ-GPU-06）。録音していないときに読み込み、常駐させる（REQ-TRX-04）。</summary>
    private WhisperFactory? _provisionalFactory;

    private string? _provisionalModelPath;
    private Thread? _provisionalThread;
    private CancellationTokenSource? _provisionalCts;

    /// <summary>
    /// 速報のワーカーが停止のタイムアウトで残っている。残っている間は速報用モデルを破棄しない（T117 と同じ規則。T186 D8）。
    /// </summary>
    private bool _provisionalWorkerStuck;

    /// <summary>このセッションで速報のエラーを通知済みか（0 / 1。T186 D7）。</summary>
    private int _provisionalErrorReported;

    /// <summary>
    /// 速報用モデルを CPU で読み込む（REQ-TRX-04 / REQ-GPU-06）。同じパスが読み込み済みなら何もしない。
    /// 録音していないときに、ライブ用モデルの読み込みの後で呼ぶこと（ネイティブログの観測を重ねない）。
    /// </summary>
    /// <returns>成功なら <c>null</c>、失敗なら理由。</returns>
    public string? LoadProvisionalModel(string modelPath)
    {
        if (_provisionalFactory != null && _provisionalModelPath != null
            && string.Equals(_provisionalModelPath.Trim(), modelPath.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        UnloadProvisionalModel();
        var (factory, error) = TryCreateFactory(modelPath, useGpu: false);
        if (factory == null)
        {
            return error;
        }

        _provisionalFactory = factory;
        _provisionalModelPath = modelPath;
        return null;
    }

    /// <summary>
    /// 速報用モデルを破棄する（速報を無効にした・選択を外した・一覧から消したとき）。録音していないときに呼ぶこと。
    /// </summary>
    public void UnloadProvisionalModel()
    {
        var factory = _provisionalFactory;
        _provisionalFactory = null;
        _provisionalModelPath = null;
        if (factory == null)
        {
            return;
        }

        if (_provisionalWorkerStuck)
        {
            // 推論中かもしれない factory を破棄するとプロセスが落ちうる（T117）。リークを選ぶ
            return;
        }

        factory.Dispose();
    }

    /// <summary>
    /// 録音開始時に速報を始める（REQ-TRX-LIVE-15）。条件が揃わなければ何もしない（T186 D10）。
    /// <see cref="StartSession"/> から、ソースの状態を初期化した後・確定パスのスレッドを起こす前に呼ぶ。
    /// </summary>
    private void StartProvisional(long sessionId)
    {
        foreach (var state in _sources.Values)
        {
            state.Provisional = null;
        }

        var factory = _provisionalFactory;
        if (!ProvisionalEnabled || factory == null || _provisionalWorkerStuck)
        {
            return;
        }

        var options = Provisional;
        var targets = new List<SourceState>();
        foreach (var state in _sources.Values)
        {
            if (options.Targets(state.Type))
            {
                state.Provisional = new ProvisionalSource(options.MaxPhraseSeconds);
                targets.Add(state);
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        _provisionalErrorReported = 0;
        _provisionalCts = new CancellationTokenSource();
        var run = new ProvisionalRun(
            factory, targets, options, LiveLanguage, SilenceCut.RmsThreshold, sessionId, _provisionalCts.Token);
        _provisionalThread = new Thread(() => ProvisionalLoop(run))
        {
            IsBackground = true,
            Name = "ProvisionalTranscription",
            Priority = ThreadPriority.BelowNormal
        };
        _provisionalThread.Start();
    }

    /// <summary>
    /// 速報を止める（REQ-TRX-LIVE-11）。確定の吐き切りより先に呼ぶ。キャンセルして <see cref="StopCancelTimeout"/> まで待つ。
    /// </summary>
    private void StopProvisional()
    {
        var thread = _provisionalThread;
        if (thread == null)
        {
            return;
        }

        _provisionalCts?.Cancel();
        bool exited = thread.Join(StopCancelTimeout);
        _provisionalThread = null;
        if (exited)
        {
            _provisionalCts?.Dispose();
        }
        else
        {
            // 抜けていないワーカーが CancellationToken と factory を使い続けうるので、どちらも破棄しない
            _provisionalWorkerStuck = true;
            Error?.Invoke("速報スレッドの停止がタイムアウトしました。速報用モデルの解放を見送ります。");
        }
        _provisionalCts = null;
    }

    /// <summary>
    /// キャプチャのコールバック（<see cref="AddSamples"/>）から、確定パスのバッファに積んだ分を写しへコピーする。
    /// 判定はしない（20-architecture-standards.md §3 規則 2）。呼び出し側は <c>BufferLock</c> を持っている。
    /// </summary>
    private static void AppendProvisionalCopy(ProvisionalSource provisional, List<float> buffer, int from)
    {
        if (from >= buffer.Count)
        {
            return;
        }

        lock (provisional.Lock)
        {
            provisional.Audio.AddRange(CollectionsMarshal.AsSpan(buffer)[from..]);
        }
    }

    /// <summary>確定パスがチャンクを処理し終えた位置を写しへ伝える（T186 D4）。確定パスのワーカーから呼ぶ。</summary>
    private static void MarkProvisionalConfirmed(SourceState state, long endPosition)
    {
        if (state.Provisional is { } provisional)
        {
            Interlocked.Exchange(ref provisional.ConfirmedEnd, endPosition);
        }
    }

    /// <summary>速報スレッドの本体。間隔は duty の上限で伸ばす（REQ-TRX-LIVE-16）。</summary>
    private void ProvisionalLoop(ProvisionalRun run)
    {
        var token = run.Token;
        double interval = run.Options.IntervalSeconds;
        var next = _sessionClock.Elapsed + TimeSpan.FromSeconds(interval);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var wait = next - _sessionClock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    token.WaitHandle.WaitOne(wait);
                    continue;
                }

                var tickStart = _sessionClock.Elapsed;
                double busy = 0;
                foreach (var state in run.Targets)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    busy += TickProvisional(run, state, interval);
                }

                interval = NextProvisionalInterval(busy, run.Options);
                next = tickStart + TimeSpan.FromSeconds(interval);
            }
        }
        // CA1031: ワーカースレッドの境界。例外を漏らすとプロセスごと落ちて録音中のセッションを失うため、
        //         全例外を Error イベントに変換して速報だけを止める（確定パスは続く）。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ReportProvisionalError(ex);
            }
        }
#pragma warning restore CA1031
    }

    /// <summary>1 音源の 1 回分。速報に使った時間（秒）を返す。</summary>
    private double TickProvisional(ProvisionalRun run, SourceState state, double interval)
    {
        var provisional = state.Provisional!;
        var planner = provisional.Planner;
        planner.SkipBefore(Interlocked.Read(ref provisional.ConfirmedEnd));

        // 新しく届いた窓の有声判定（キャプチャのコールバックでは判定しない）
        lock (provisional.Lock)
        {
            long audioEnd = provisional.AudioStart + provisional.Audio.Count;
            while ((planner.KnownWindows + 1) * ProvisionalPhrasePlanner.WindowSamples <= audioEnd)
            {
                long offset = (planner.KnownWindows * ProvisionalPhrasePlanner.WindowSamples) - provisional.AudioStart;
                planner.AppendWindow(offset >= 0
                    && WindowRms(provisional.Audio, (int)offset, ProvisionalPhrasePlanner.WindowSamples) >= run.RmsThreshold);
            }
        }

        double busy = 0;
        foreach (var request in planner.Plan())
        {
            if (run.Token.IsCancellationRequested)
            {
                break;
            }

            float[] samples;
            lock (provisional.Lock)
            {
                long from = Math.Max(request.Start, provisional.AudioStart) - provisional.AudioStart;
                long to = Math.Min(request.End - provisional.AudioStart, provisional.Audio.Count);
                samples = to > from ? provisional.Audio.GetRange((int)from, (int)(to - from)).ToArray() : [];
            }

            if (samples.Length > 0)
            {
                busy += DecodeProvisional(run, state, request, samples, interval);
            }
        }

        // 要らなくなった写しを捨てる（今の句の先頭より前。窓の境界で切る。T186 D3）
        lock (provisional.Lock)
        {
            long keep = Math.Min(planner.KeepFrom, planner.KnownWindows * ProvisionalPhrasePlanner.WindowSamples);
            keep = keep / ProvisionalPhrasePlanner.WindowSamples * ProvisionalPhrasePlanner.WindowSamples;
            long drop = Math.Clamp(keep - provisional.AudioStart, 0, provisional.Audio.Count);
            if (drop > 0)
            {
                provisional.Audio.RemoveRange(0, (int)drop);
                provisional.AudioStart += drop;
            }
            planner.TrimBefore(provisional.AudioStart);
        }

        return busy;
    }

    /// <summary>句 1 つを速報用モデルに掛けて通知する。所要時間（秒）を返す。</summary>
    private double DecodeProvisional(
        ProvisionalRun run, SourceState state, ProvisionalRequest request, float[] samples, double interval)
    {
        long started = Stopwatch.GetTimestamp();
        string? text = null;
        int segments = 0;
        try
        {
            int audioContext = AudioContextFor((double)samples.Length / TargetRate);
            using var processor = BuildProvisionalProcessor(run.Factory, run.Language, audioContext, run.Options.Threads);
            text = RunToText(processor, PadToMinimum(samples, MinWhisperSamples), run.Token, out segments);
        }
        // CA1031: Whisper のネイティブ処理は任意の例外を投げうる。速報の失敗で確定パスを止めないため、
        //         最初の 1 回だけ Error に変換して続ける（T186 D7）。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            // キャンセル後の例外は通知しない（20-architecture-standards.md §3 規則 7）。Whisper.net は推論の途中で
            // キャンセルされると OperationCanceledException ではなく WhisperProcessingException を投げることがある（T182 の実測）。
            if (run.Token.IsCancellationRequested)
            {
                return Stopwatch.GetElapsedTime(started).TotalSeconds;
            }
            ReportProvisionalError(ex);
        }
#pragma warning restore CA1031

        var elapsed = Stopwatch.GetElapsedTime(started);
        _metrics?.WriteProvisional(
            _sessionClock.Elapsed, state.Type, request.Start, samples.Length,
            elapsed.TotalMilliseconds, segments, PendingSeconds, interval);

        if (SanitizeProvisionalText(text) is { } clean)
        {
            ProvisionalTranscribed?.Invoke(new ProvisionalUpdate(
                run.SessionId, state.Type, state.Label, request.PhraseId,
                request.Start, request.End, request.Closing, clean));
        }

        return elapsed.TotalSeconds;
    }

    private void ReportProvisionalError(Exception ex)
    {
        if (Interlocked.Exchange(ref _provisionalErrorReported, 1) == 0)
        {
            Error?.Invoke($"速報エラー: {ex.Message}");
        }
    }

    /// <summary>
    /// 速報の processor（REQ-TRX-LIVE-17）。greedy・温度 0・温度フォールバック無し・単一セグメント・前文脈無し・トークン上限。
    /// <c>audio_ctx</c> が句の長さで変わるので、呼び出しのたびに作る（T186 D5）。
    /// </summary>
    private static WhisperProcessor BuildProvisionalProcessor(
        WhisperFactory factory, string language, int audioContext, int threads)
        => WithLanguageOption(factory.CreateBuilder(), language)
            .WithGreedySamplingStrategy().ParentBuilder
            .WithTemperature(0f)
            .WithTemperatureInc(0f)
            .WithSingleSegment()
            .WithNoContext()
            .WithMaxTokensPerSegment(ProvisionalMaxTokens)
            .WithAudioContextSize(audioContext)
            .WithThreads(threads)
            .Build();

    /// <summary><c>ProcessAsync</c> を同期的に最後まで回し、本文を連結して返す（確定パスの <c>TranscribeRegion</c> と同じ消費の仕方）。</summary>
    private static string RunToText(WhisperProcessor processor, float[] samples, CancellationToken token, out int segments)
    {
        var text = new StringBuilder();
        segments = 0;
        var enumerator = processor.ProcessAsync(samples, token).GetAsyncEnumerator(token);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                text.Append(enumerator.Current.Text?.Trim());
                segments++;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return text.ToString();
    }

    /// <summary>窓 1 つの RMS（REQ-TRX-06 と同じ判定に使う）。</summary>
    private static double WindowRms(List<float> audio, int offset, int length)
    {
        double sumSquares = 0;
        for (int i = offset; i < offset + length; i++)
        {
            sumSquares += audio[i] * (double)audio[i];
        }

        return Math.Sqrt(sumSquares / length);
    }

    /// <summary>
    /// 速報の <c>audio_ctx</c>（REQ-TRX-LIVE-17）。（句の長さ＋1 秒）× 50 フレームを 128 フレーム刻みに切り上げる（最小 128・上限 1500）。
    /// </summary>
    /// <remarks>
    /// この値より長い音声は黙って切り捨てられるので、必ず句より長く取る。既定の 1500 と質がほぼ同じで、CPU では 3〜7 倍速い（T182 の E3）。
    /// </remarks>
    internal static int AudioContextFor(double seconds)
    {
        int frames = (int)Math.Ceiling((seconds + 1.0) * 50);
        int staged = (frames + 127) / 128 * 128;
        return Math.Clamp(staged, 128, 1500);
    }

    /// <summary>
    /// 次の呼び出しまでの間隔（REQ-TRX-LIVE-16）。その回に速報に使った時間 ÷ duty の上限を、間隔〜4 秒に収める。
    /// 対象の音源が複数あれば、1 回の予算を分け合う（使った時間は全音源の合計）。
    /// </summary>
    internal static double NextProvisionalInterval(double busySeconds, ProvisionalOptions options)
        => Math.Clamp(busySeconds / options.DutyCap, options.IntervalSeconds, ProvisionalOptions.MaxIntervalSeconds);

    /// <summary>無音で出やすい定型句（REQ-TRX-LIVE-17。T182 の計測ハーネスと同じ一覧）。</summary>
    private static readonly string[] ProvisionalBoilerplate =
    [
        "ご視聴ありがとうございました", "ご視聴ありがとうございます", "チャンネル登録", "最後までご視聴",
        "おやすみなさい", "ありがとうございました", "お疲れ様でした", "字幕", "提供"
    ];

    /// <summary>
    /// 速報の幻聴を除く（REQ-TRX-LIVE-17）。空・定型の幻聴だけ・同じ語句の繰り返しなら <c>null</c>、それ以外は前後の空白を除いた本文。
    /// **確定パスの行には使わない**（確定の出力は変えない）。
    /// </summary>
    internal static string? SanitizeProvisionalText(string? text)
    {
        if (text == null)
        {
            return null;
        }

        var normalized = NormalizeForComparison(text);
        if (normalized.Length == 0)
        {
            return null;
        }

        foreach (var phrase in ProvisionalBoilerplate)
        {
            var target = NormalizeForComparison(phrase);
            // 定型句だけ（前後に 3 文字以内しか無い）の出力を幻聴とみなす
            if (normalized.Contains(target, StringComparison.Ordinal) && normalized.Length <= target.Length + 3)
            {
                return null;
            }
        }

        return IsRepetition(normalized) ? null : text.Trim();
    }

    /// <summary>NFKC にして、文字と数字だけを残す（句読点・空白・記号を除く）。</summary>
    private static string NormalizeForComparison(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 同じ語句（1〜8 文字）が 4 回以上続く（1 文字なら 8 回以上）、または同じ語句の連続が全体の半分以上を占める（12 文字以上のとき）。
    /// </summary>
    internal static bool IsRepetition(string normalized)
    {
        for (int length = 1; length <= 8; length++)
        {
            for (int start = 0; start + (length * 4) <= normalized.Length; start++)
            {
                var unit = normalized.AsSpan(start, length);
                int repeats = 1;
                int position = start + length;
                while (position + length <= normalized.Length && normalized.AsSpan(position, length).SequenceEqual(unit))
                {
                    repeats++;
                    position += length;
                }

                if (repeats >= 4 && (length >= 2 || repeats >= 8))
                {
                    return true;
                }

                if (repeats * length * 2 >= normalized.Length && repeats >= 3 && normalized.Length >= 12)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>速報用モデルを破棄する（<see cref="Dispose(bool)"/> から）。ワーカーが残っていれば見送る。</summary>
    private void DisposeProvisionalModel() => UnloadProvisionalModel();
}