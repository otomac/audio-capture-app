using System.IO;
using System.Text;
using NAudio.Wave;
using Whisper.net;

namespace AudioCaptureApp.Services;

public enum AudioSourceType { Mic, Speaker }

public class TranscriptionService : IDisposable
{
    private class SourceState
    {
        public readonly List<float> Pcm16kBuffer = new(BufferThresholdSamples + TargetRate);
        public readonly object BufferLock = new();
        public TimeSpan ChunkOffset = TimeSpan.Zero;
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

    private const int TargetRate = 16000;
    private const int BufferThresholdSamples = TargetRate * 20; // 20秒分

    public event Action<string>? Error;
    public event Action<string>? SegmentTranscribed;
    public event Action<string>? RuntimeInfo;

    public bool IsModelLoaded => _factory != null;

    public bool LoadModel(string modelPath)
    {
        DisposeProcessor();

        if (!File.Exists(modelPath))
        {
            return false;
        }

        try
        {
            _factory = WhisperFactory.FromPath(modelPath);
            // ランタイムは FromPath の内部で初めてロードされるため、ここで選択結果を通知する
            var runtime = TryGetLoadedRuntimeName();
            if (runtime != null)
            {
                RuntimeInfo?.Invoke(runtime);
            }
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Whisperモデル読み込み失敗: {ex.Message}");
            DisposeProcessor();
            return false;
        }
    }

    // Whisper.net は RuntimeOptions.Instance.LoadedLibrary で選択済みランタイムを返すが、
    // バージョンによって型配置が変わる可能性があるためリフレクションで拾う
    private static string? TryGetLoadedRuntimeName()
    {
        try
        {
            var asm = typeof(WhisperFactory).Assembly;
            var optionsType = asm.GetType("Whisper.net.LibraryLoader.RuntimeOptions");
            if (optionsType == null) return null;

            var instanceProp = optionsType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var instance = instanceProp?.GetValue(null);
            if (instance == null) return null;

            var loadedProp = optionsType.GetProperty("LoadedLibrary");
            var value = loadedProp?.GetValue(instance);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    public void RegisterSource(AudioSourceType type, string label, int sourceRate, int sourceChannels)
    {
        // 既存のプロセッサがあれば破棄
        if (_sources.TryGetValue(type, out var existing))
        {
            existing.Processor?.Dispose();
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
                state.Pcm16kBuffer.Clear();
            state.ChunkOffset = TimeSpan.Zero;
            state.ResamplePos = 0;
            state.LpfPrev = 0f;
        }

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

        lock (state.BufferLock)
        {
            double resamplePos = state.ResamplePos;
            float lpfPrev = state.LpfPrev;
            DownmixResampleAppend(
                samples, sampleCount, state.SourceChannels, state.SourceRate,
                state.LpfAlpha, ref resamplePos, ref lpfPrev, state.Pcm16kBuffer);
            state.ResamplePos = resamplePos;
            state.LpfPrev = lpfPrev;
        }
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

        WhisperProcessor? processor = null;
        StreamWriter? writer = null;
        AudioFileReader? reader = null;
        string outputPath = BuildTranscriptPath(audioFilePath);
        try
        {
            reader = new AudioFileReader(audioFilePath);
            int sourceRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;
            TimeSpan totalTime = reader.TotalTime;
            float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

            writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);

            processor = _factory.CreateBuilder().WithLanguage("ja").Build();

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

            // 残りバッファ（1秒以上あれば処理）
            if (pcm16kBuffer.Count > TargetRate)
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
        catch (OperationCanceledException)
        {
            // 部分出力ファイルは削除する（ユーザーが完結したと誤認しないように）
            writer?.Dispose();
            writer = null;
            TryDeleteFile(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            // Whisper のネイティブ処理はキャンセル時に OperationCanceledException 以外を
            // 投げることがあるため、トークンがキャンセル済みなら中止として扱う
            if (ct.IsCancellationRequested)
            {
                writer?.Dispose();
                writer = null;
                TryDeleteFile(outputPath);
                throw new OperationCanceledException(ct);
            }
            Error?.Invoke($"ファイル文字起こしエラー: {ex.Message}");
            return false;
        }
        finally
        {
            processor?.Dispose();
            writer?.Dispose();
            reader?.Dispose();
        }
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
        catch
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
        await writer.FlushAsync().ConfigureAwait(false);
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

                float[]? chunk = null;
                lock (state.BufferLock)
                {
                    if (state.Pcm16kBuffer.Count >= BufferThresholdSamples)
                    {
                        chunk = state.Pcm16kBuffer.ToArray();
                        state.Pcm16kBuffer.Clear();
                    }
                }

                if (chunk != null)
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
                float[]? remaining;
                lock (state.BufferLock)
                {
                    remaining = state.Pcm16kBuffer.Count > TargetRate // 1秒以上あれば処理
                        ? state.Pcm16kBuffer.ToArray()
                        : null;
                    state.Pcm16kBuffer.Clear();
                }

                if (remaining != null)
                {
                    ProcessChunk(remaining, state, token);
                }
            }
        }
    }

    internal static bool IsSilent(float[] samples)
    {
        // RMS（二乗平均平方根）で音声エネルギーを測定
        double sumSquares = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * (double)samples[i];
        }
        double rms = Math.Sqrt(sumSquares / samples.Length);
        // RMS が -40dB 未満なら無音とみなす
        return rms < 0.01;
    }

    private void ProcessChunk(float[] samples, SourceState state, CancellationToken token)
    {
        try
        {
            // 無音チャンクはWhisperに渡さない（ハルシネーション防止）
            if (IsSilent(samples))
            {
                state.ChunkOffset += TimeSpan.FromSeconds((double)samples.Length / TargetRate);
                return;
            }

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

                    var startTime = _sessionStartTime + state.ChunkOffset + segment.Start;
                    var endTime = _sessionStartTime + state.ChunkOffset + segment.End;
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

            state.ChunkOffset += TimeSpan.FromSeconds((double)samples.Length / TargetRate);
        }
        catch (OperationCanceledException)
        {
            // キャンセルによる中断は正常終了扱い
        }
        catch (Exception ex)
        {
            Error?.Invoke($"文字起こしエラー: {ex.Message}");
        }
    }

    public void StopSession()
    {
        _isRunning = false;

        // まず残りバッファ処理の完了を待つ（最大30秒）
        if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(30)))
        {
            // タイムアウト時はキャンセルして強制終了
            _cts?.Cancel();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        _thread = null;

        _cts?.Dispose();
        _cts = null;

        foreach (var state in _sources.Values)
        {
            state.Processor?.Dispose();
        }
        _sources.Clear();
    }

    private void DisposeProcessor()
    {
        foreach (var state in _sources.Values)
        {
            state.Processor?.Dispose();
        }
        _sources.Clear();
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose()
    {
        StopSession();
        DisposeProcessor();
    }
}