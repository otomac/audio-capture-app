using System.IO;
using Whisper.net;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ライブ文字起こしのセッション（音源の登録・開始・サンプルの受け取り・停止。REQ-TRX-LIVE-11）を担当する部分。
// 確定パスのワーカー本体は TranscriptionService.LiveWorker.cs にある（T193 で切り出した）。
public partial class TranscriptionService
{
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
            Type = type,
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
                state.BufferStartPosition = 0;
            }
            state.ResamplePos = 0;
            state.LpfPrev = 0f;
        }

        Interlocked.Increment(ref _sessionId);
        _metrics = MetricsLogEnabled
            ? TranscriptionMetricsLog.Open(TranscriptionMetricsLog.PathFor(startTime), message => Error?.Invoke(message))
            : null;

        _sessionClock.Restart();
        // 速報（REQ-TRX-LIVE-15）は確定パスより先に用意する。AddSamples が写しへ積み始めるのは _isRunning の後
        StartProvisional(CurrentSessionId);
        // 文脈プロンプト（REQ-TRX-LIVE-19）。ワーカーを起こす前に用意する
        PrepareContext();
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
                foreach (var chunk in SplitAtChunkLimit(state.Pcm16kBuffer, bufferStart, state.BufferStartPosition))
                {
                    state.Ready.Enqueue(chunk);
                }
                state.BufferStartPosition += state.Pcm16kBuffer.Count;
                state.Pcm16kBuffer.Clear();

                // 不連続な音声を地続きとして扱わないよう、リサンプラと LPF の状態も切る
                state.ResamplePos = 0;
                state.LpfPrev = 0f;
            }

            double resamplePos = state.ResamplePos;
            float lpfPrev = state.LpfPrev;
            int appendedFrom = state.Pcm16kBuffer.Count;
            DownmixResampleAppend(
                samples, sampleCount, state.SourceChannels, state.SourceRate,
                state.LpfAlpha, ref resamplePos, ref lpfPrev, state.Pcm16kBuffer);
            state.ResamplePos = resamplePos;
            state.LpfPrev = lpfPrev;

            // 速報の対象なら、同じ音声を写しへ（確定パスは取り出すとバッファから消すため。T186 D2）
            if (state.Provisional is { } provisional)
            {
                AppendProvisionalCopy(provisional, state.Pcm16kBuffer, appendedFrom);
            }

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
    /// <param name="startPosition">バッファ先頭サンプルの絶対位置（REQ-TRX-LIVE-18）。</param>
    /// <remarks>
    /// バッファ内は地続きなので、各チャンクの先頭時刻はバッファ先頭の時刻にチャンク内の位置を足せば求まる
    /// （<see cref="RegionStart"/> と同じ式）。20 秒分以下なら、従来どおりバッファ全体を 1 チャンクにする。
    /// </remarks>
    internal static List<PendingChunk> SplitAtChunkLimit(List<float> buffer, TimeSpan bufferStart, long startPosition = 0)
    {
        var chunks = new List<PendingChunk>();
        for (int offset = 0; offset < buffer.Count; offset += BufferThresholdSamples)
        {
            int count = Math.Min(BufferThresholdSamples, buffer.Count - offset);
            var samples = new float[count];
            buffer.CopyTo(offset, samples, 0, count);
            chunks.Add(new PendingChunk(samples, RegionStart(bufferStart, offset), startPosition + offset));
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

        // REQ-TRX-LIVE-11: 速報を先に止める（確定の吐き切りと CPU を取り合わないため）
        StopProvisional();

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

        // ワーカーが抜けていなくても閉じてよい（閉じた後の書き込みは TranscriptionMetricsLog が捨てる）
        _metrics?.Dispose();
        _metrics = null;

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
}