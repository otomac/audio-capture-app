using System.IO;
using System.Text;
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
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Whisperモデル読み込み失敗: {ex.Message}");
            DisposeProcessor();
            return false;
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

        // ステレオ→モノ変換 + アンチエイリアシングLPF + ダウンサンプリング (sourceRate → 16kHz)
        int channels = state.SourceChannels;
        int frames = sampleCount / channels;
        double ratio = (double)state.SourceRate / TargetRate;
        float alpha = state.LpfAlpha;

        int estimatedCount = (int)(frames / ratio) + 1;
        var converted = new float[estimatedCount];
        int writeIndex = 0;

        lock (state.BufferLock)
        {
            float prev = state.LpfPrev;

            for (; state.ResamplePos < frames; state.ResamplePos += ratio)
            {
                int idx = (int)state.ResamplePos;
                if (idx >= frames)
                {
                    break;
                }

                float sample = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    sample += samples[idx * channels + ch];
                }
                sample /= channels;

                // 1次IIRローパスフィルタ適用
                prev = prev + alpha * (sample - prev);

                if (writeIndex < converted.Length)
                {
                    converted[writeIndex++] = prev;
                }
            }
            state.ResamplePos -= frames;
            state.LpfPrev = prev;

            for (int i = 0; i < writeIndex; i++)
            {
                state.Pcm16kBuffer.Add(converted[i]);
            }
        }
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

    private static bool IsSilent(float[] samples)
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