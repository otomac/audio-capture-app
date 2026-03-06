using System.IO;
using System.Text;
using Whisper.net;

namespace AudioCaptureApp.Services;

public class TranscriptionService : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly List<float> _pcm16kBuffer = new();
    private readonly object _bufferLock = new();
    private Thread? _thread;
    private volatile bool _isRunning;
    private string _outputPath = "";
    private TimeSpan _chunkOffset;
    private int _sourceRate;
    private int _sourceChannels;
    private double _resamplePos;

    private const int TargetRate = 16000;
    private const int BufferThresholdSamples = TargetRate * 20; // 20秒分

    public event Action<string>? Error;
    public event Action<string>? SegmentTranscribed;

    public bool IsModelLoaded => _processor != null;

    public bool LoadModel(string modelPath)
    {
        DisposeProcessor();

        if (!File.Exists(modelPath))
            return false;

        try
        {
            _factory = WhisperFactory.FromPath(modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("ja")
                .Build();
            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Whisperモデル読み込み失敗: {ex.Message}");
            DisposeProcessor();
            return false;
        }
    }

    public void StartSession(string mp3FilePath, int sourceRate, int sourceChannels)
    {
        if (_processor == null)
            throw new InvalidOperationException("Whisperモデルが読み込まれていません。");

        _outputPath = Path.ChangeExtension(mp3FilePath, ".txt");
        _sourceRate = sourceRate;
        _sourceChannels = sourceChannels;
        _chunkOffset = TimeSpan.Zero;
        _resamplePos = 0;

        lock (_bufferLock)
            _pcm16kBuffer.Clear();

        _isRunning = true;
        _thread = new Thread(TranscriptionLoop) { IsBackground = true, Name = "WhisperTranscription" };
        _thread.Start();
    }

    public void AddSamples(float[] samples, int sampleCount)
    {
        if (!_isRunning) return;

        // ステレオ→モノ変換 + ダウンサンプリング (sourceRate → 16kHz)
        int channels = _sourceChannels;
        int frames = sampleCount / channels;
        double ratio = (double)_sourceRate / TargetRate;

        var converted = new List<float>(frames / (int)ratio + 1);
        for (; _resamplePos < frames; _resamplePos += ratio)
        {
            int idx = (int)_resamplePos;
            if (idx >= frames) break;

            float sample = 0;
            for (int ch = 0; ch < channels; ch++)
                sample += samples[idx * channels + ch];
            sample /= channels;

            converted.Add(sample);
        }
        _resamplePos -= frames;

        if (converted.Count > 0)
        {
            lock (_bufferLock)
                _pcm16kBuffer.AddRange(converted);
        }
    }

    private void TranscriptionLoop()
    {
        while (_isRunning)
        {
            Thread.Sleep(1000);

            float[]? chunk = null;
            lock (_bufferLock)
            {
                if (_pcm16kBuffer.Count >= BufferThresholdSamples)
                {
                    chunk = _pcm16kBuffer.ToArray();
                    _pcm16kBuffer.Clear();
                }
            }

            if (chunk != null)
                ProcessChunk(chunk);
        }

        // 残りバッファを処理
        float[]? remaining;
        lock (_bufferLock)
        {
            remaining = _pcm16kBuffer.Count > TargetRate // 1秒以上あれば処理
                ? _pcm16kBuffer.ToArray()
                : null;
            _pcm16kBuffer.Clear();
        }

        if (remaining != null)
            ProcessChunk(remaining);
    }

    private void ProcessChunk(float[] samples)
    {
        try
        {
            var results = new List<string>();

            // ProcessAsync を同期的に消費
            var asyncEnum = _processor!.ProcessAsync(samples, CancellationToken.None);
            var enumerator = asyncEnum.GetAsyncEnumerator(CancellationToken.None);
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    var segment = enumerator.Current;
                    var text = segment.Text?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    var start = _chunkOffset + segment.Start;
                    var end = _chunkOffset + segment.End;
                    var line = $"[{start:hh\\:mm\\:ss} - {end:hh\\:mm\\:ss}] {text}";
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

            _chunkOffset += TimeSpan.FromSeconds((double)samples.Length / TargetRate);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"文字起こしエラー: {ex.Message}");
        }
    }

    public void StopSession()
    {
        _isRunning = false;
        // Whisper処理に時間がかかる可能性があるため長めのタイムアウト
        _thread?.Join(TimeSpan.FromSeconds(120));
        _thread = null;
    }

    private void DisposeProcessor()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose()
    {
        StopSession();
        DisposeProcessor();
    }
}
