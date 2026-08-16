using System.Diagnostics;
using System.IO;
using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace AudioCaptureApp.Services;

public enum AudioSourceType { Mic, Speaker }

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

    /// <summary>無音判定の窓長（100ms）。チャンク全体の平均ではなく窓ごとに判定する。</summary>
    internal const int SilenceWindowSamples = TargetRate / 10;

    /// <summary>無音判定の RMS 閾値（-40dB 相当）。</summary>
    internal const double SilenceRmsThreshold = 0.01;

    /// <summary>Whisper へ渡す最小サンプル数（1 秒）。これ未満は無音で埋めて伸ばす。</summary>
    internal const int MinWhisperSamples = TargetRate;

    /// <summary>セッション終了時、残りバッファを処理する最小サンプル数（0.2 秒）。</summary>
    internal const int MinTailSamples = TargetRate / 5;

    public event Action<string>? Error;
    public event Action<string>? SegmentTranscribed;
    public event Action<string>? RuntimeInfo;

    public bool IsModelLoaded => _factory != null;

    // GPU優先順（CUDA > Vulkan > CoreML > OpenVino > CPU）と、CPU限定の順序
    private static readonly List<RuntimeLibrary> GpuPreferredOrder = new()
    {
        RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.CoreML,
        RuntimeLibrary.OpenVino, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx
    };
    private static readonly List<RuntimeLibrary> CpuOnlyOrder = new()
    {
        RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx
    };

    private static bool IsGpuLibrary(RuntimeLibrary? library) =>
        library is RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

    // GPU利用可否は実際にランタイムを読み込んでみないと判定できないため、
    // useGpu の指定に関わらず一度 GPU 優先順で読み込みを試して可否を判定する。
    // ユーザー設定が CPU 使用の場合、GPU が利用可能でも CPU 限定で読み込み直す。
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
            _factory = WhisperFactory.FromPath(modelPath);
            var loaded = RuntimeOptions.LoadedLibrary;
            var gpuAvailable = IsGpuLibrary(loaded);

            if (!useGpu && gpuAvailable)
            {
                _factory.Dispose();
                RuntimeOptions.RuntimeLibraryOrder = CpuOnlyOrder;
                _factory = WhisperFactory.FromPath(modelPath);
                loaded = RuntimeOptions.LoadedLibrary;
            }

            if (loaded != null)
            {
                RuntimeInfo?.Invoke(loaded.Value.ToString());
            }
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

    public async Task<bool> TranscribeFileAsync(
        string audioFilePath,
        IProgress<(TimeSpan processed, TimeSpan total)>? progress,
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
            return await TranscribeFileCoreAsync(audioFilePath, outputPath, progress, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 部分出力ファイルは削除する（ユーザーが完結したと誤認しないように）。
            // Core を抜ける時点で using が StreamWriter を破棄済みのため、ここで削除できる。
            TryDeleteFile(outputPath);
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
                TryDeleteFile(outputPath);
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
        IProgress<(TimeSpan processed, TimeSpan total)>? progress,
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
        const string label = "ファイル";

        progress?.Report((TimeSpan.Zero, totalTime));

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

                await ProcessFileChunkAsync(processor, chunk, chunkOffset, label, writer, ct)
                    .ConfigureAwait(false);
                chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
                progress?.Report((chunkOffset, totalTime));
            }
        }

        // 残りバッファ（末尾の短い発話を落とさないよう 0.2 秒以上あれば処理）
        if (pcm16kBuffer.Count >= MinTailSamples)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pcm16kBuffer.ToArray();
            await ProcessFileChunkAsync(processor, chunk, chunkOffset, label, writer, ct)
                .ConfigureAwait(false);
            chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
        }

        progress?.Report((totalTime, totalTime));
        return true;
    }

    // {入力ファイル名}.transcript.txt を同じフォルダに配置
    // 例: audio.mp3 → audio.transcript.txt
    // （録音時に生成される audio.txt と名前衝突しないように）
    internal static string BuildTranscriptPath(string audioFilePath)
    {
        return Path.ChangeExtension(audioFilePath, ".transcript.txt");
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

    private async Task ProcessFileChunkAsync(
        WhisperProcessor processor, float[] samples, TimeSpan chunkOffset,
        string label, StreamWriter writer, CancellationToken ct)
    {
        if (IsSilent(samples))
        {
            return;
        }

        samples = PadToMinimum(samples, MinWhisperSamples);

        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var startTime = chunkOffset + segment.Start;
            var endTime = chunkOffset + segment.End;
            var line = $"[{startTime:hh\\:mm\\:ss} - {endTime:hh\\:mm\\:ss}] [{label}] {text}";
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            SegmentTranscribed?.Invoke(line);
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
                while (_isRunning && !token.IsCancellationRequested && (chunk = TakeNextChunk(state)) != null)
                {
                    ProcessChunk(chunk, state, token);
                }
            }
        }

        // 残りバッファを処理（キャンセルされていなければ）
        if (!token.IsCancellationRequested)
        {
            foreach (var (_, state) in _sources)
            {
                PendingChunk? chunk;
                while (!token.IsCancellationRequested && (chunk = TakeNextChunk(state)) != null)
                {
                    ProcessChunk(chunk, state, token);
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
                    ProcessChunk(tail, state, token);
                }
            }
        }
    }

    /// <summary>
    /// 確定済みチャンクを 1 つ取り出す。無ければ、バッファが閾値に達していれば
    /// そこから 1 チャンク切り出す。どちらも無ければ <c>null</c>。
    /// </summary>
    private static PendingChunk? TakeNextChunk(SourceState state)
    {
        lock (state.BufferLock)
        {
            if (state.Ready.Count > 0)
            {
                return state.Ready.Dequeue();
            }

            if (state.Pcm16kBuffer.Count >= BufferThresholdSamples)
            {
                // バッファ全体ではなく閾値ぶんだけ切り出す。
                // 全部渡すと、文字起こしが追いつかず滞留したときに
                // 1 回の Whisper 呼び出しが数分ぶんの音声になり、
                // 停止要求から抜けられなくなる（T117）。
                var start = ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count);
                var samples = new float[BufferThresholdSamples];
                state.Pcm16kBuffer.CopyTo(0, samples, 0, BufferThresholdSamples);
                state.Pcm16kBuffer.RemoveRange(0, BufferThresholdSamples);
                return new PendingChunk(samples, start);
            }

            return null;
        }
    }

    internal static bool IsSilent(float[] samples)
        => IsSilent(samples, SilenceRmsThreshold, SilenceWindowSamples);

    /// <summary>
    /// チャンクを短い窓に区切り、**どの窓も** 閾値未満のときだけ無音と判定する。
    /// </summary>
    /// <remarks>
    /// チャンク全体の平均 RMS で判定すると、長い無音に埋もれた短い発話がならされて
    /// 無音扱いになり、チャンクごと Whisper に渡らなくなる。
    /// 発話区間 d 秒（その区間の RMS が r）を 20 秒チャンクで平均すると
    /// r * sqrt(d / 20) まで下がるため、通常会話（r ≒ 0.02〜0.05）では
    /// 発話が 2〜3 秒以下だと丸ごと捨てられていた。
    /// 窓ごとの判定なら希釈されないため、短い発話・小音量の発話を取りこぼさない。
    /// </remarks>
    internal static bool IsSilent(float[] samples, double threshold, int windowSamples)
    {
        if (samples.Length == 0)
        {
            return true;
        }

        int window = windowSamples > 0 ? windowSamples : samples.Length;

        for (int start = 0; start < samples.Length; start += window)
        {
            int length = Math.Min(window, samples.Length - start);

            double sumSquares = 0;
            for (int i = start; i < start + length; i++)
            {
                sumSquares += samples[i] * (double)samples[i];
            }

            if (Math.Sqrt(sumSquares / length) >= threshold)
            {
                return false;
            }
        }

        return true;
    }

    private void ProcessChunk(PendingChunk chunk, SourceState state, CancellationToken token)
    {
        try
        {
            // 無音チャンクはWhisperに渡さない（ハルシネーション防止）。
            // 時刻はチャンク自身が持つため、破棄しても後続の時刻はずれない。
            if (IsSilent(chunk.Samples))
            {
                return;
            }

            var samples = PadToMinimum(chunk.Samples, MinWhisperSamples);
            var results = new List<string>();

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

                    var startTime = _sessionStartTime + chunk.StartElapsed + segment.Start;
                    var endTime = _sessionStartTime + chunk.StartElapsed + segment.End;
                    var line = $"[{startTime:HH:mm:ss} - {endTime:HH:mm:ss}] [{state.Label}] {text}";
                    results.Add(line);
                    SegmentTranscribed?.Invoke(line);
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (results.Count > 0)
            {
                File.AppendAllLines(_outputPath, results, Encoding.UTF8);
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