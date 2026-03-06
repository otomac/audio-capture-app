using System.Diagnostics;
using System.IO;
using AudioCaptureApp.Models;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioCaptureApp.Services;

public class AudioCaptureService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private List<AudioDevice> _captureDevices = new();
    private List<AudioDevice> _renderDevices = new();

    // マイク常時キャプチャ（録音と独立したライフサイクル）
    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _micBuffer;

    // ループバックキャプチャ（録音時のみ）
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider? _loopbackBuffer;

    private ISampleProvider? _mixerSource;
    private WaveFormat? _outputFormat;
    private LameMP3FileWriter? _mp3Writer;
    private RecordingSession? _currentSession;
    private bool _hasWrittenData;

    // 文字起こし
    private TranscriptionService? _transcriptionService;

    private Thread? _writerThread;
    private volatile bool _isWriting;

    // ピークレベル測定
    private volatile float _micPeakLevel;
    private volatile float _loopbackPeakLevel;

    public bool IsRecording => _isWriting;
    public RecordingSession? CurrentSession => _currentSession;
    public float MicPeakLevel => _micPeakLevel;
    public float LoopbackPeakLevel => _loopbackPeakLevel;

    public TranscriptionService? TranscriptionService => _transcriptionService;

    public event Action<string>? RecordingError;

    public IReadOnlyList<AudioDevice> GetCaptureDevices() => _captureDevices;
    public IReadOnlyList<AudioDevice> GetRenderDevices() => _renderDevices;

    public void RefreshDevices()
    {
        _captureDevices = EnumerateDevices(DataFlow.Capture, Role.Communications);
        _renderDevices = EnumerateDevices(DataFlow.Render, Role.Multimedia);
    }

    private List<AudioDevice> EnumerateDevices(DataFlow dataFlow, Role role)
    {
        string? defaultId = null;
        try { defaultId = _enumerator.GetDefaultAudioEndpoint(dataFlow, role).ID; }
        catch { }

        var devices = new List<AudioDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
        {
            devices.Add(new AudioDevice
            {
                DeviceId = device.ID,
                FriendlyName = device.FriendlyName,
                IsDefault = device.ID == defaultId
            });
        }
        return devices;
    }

    // --- マイク常時モニター ---

    public void StartMicMonitor(AudioDevice device)
    {
        StopMicMonitor();

        var mmDevice = _enumerator.GetDevice(device.DeviceId);
        _micCapture = new WasapiCapture(mmDevice) { ShareMode = AudioClientShareMode.Shared };
        _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };
        _micCapture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
            {
                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _micPeakLevel = CalculatePeak(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
            }
        };
        _micCapture.StartRecording();
    }

    public void StopMicMonitor()
    {
        if (_micCapture != null)
        {
            try { _micCapture.StopRecording(); } catch { }
            _micCapture.Dispose();
            _micCapture = null;
        }
        _micBuffer = null;
        _micPeakLevel = 0f;
    }

    // --- 録音制御 ---

    public void SetTranscriptionService(TranscriptionService? service)
    {
        _transcriptionService = service;
    }

    public void StartRecording(AudioDevice? micDevice, AudioDevice? loopbackDevice, string outputFolder)
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");
        if (micDevice == null && loopbackDevice == null)
            throw new InvalidOperationException("少なくとも1つのデバイスを選択してください。");

        try
        {
            // マイクは既に常時キャプチャ中。ループバックのみ新規作成。
            SetupLoopbackCapture(loopbackDevice);
            SetupMixer();
        }
        catch (Exception ex)
        {
            CleanupLoopback();
            throw new InvalidOperationException(
                $"録音の開始に失敗しました: {ex.Message}", ex);
        }

        var now = DateTime.Now;
        var fileName = now.ToString("yyyyMMdd_HHmmss") + ".mp3";
        Directory.CreateDirectory(outputFolder);
        var filePath = Path.Combine(outputFolder, fileName);

        _currentSession = new RecordingSession
        {
            FilePath = filePath,
            StartedAt = now,
            DeviceId = micDevice?.DeviceId ?? loopbackDevice!.DeviceId
        };

        try
        {
            _mp3Writer = new LameMP3FileWriter(filePath, _outputFormat!, LAMEPreset.STANDARD);
        }
        catch (Exception ex)
        {
            CleanupLoopback();
            _currentSession = null;
            throw new InvalidOperationException($"MP3ファイルの作成に失敗しました: {ex.Message}", ex);
        }

        _hasWrittenData = false;

        // 文字起こしセッション開始
        if (_transcriptionService is { IsModelLoaded: true })
        {
            _transcriptionService.StartSession(filePath, _outputFormat!.SampleRate, _outputFormat.Channels);
        }

        // マイクバッファをクリアして録音開始時点からの音声のみ使う
        _micBuffer?.ClearBuffer();

        _loopbackCapture?.StartRecording();

        _isWriting = true;
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "AudioMixerWriter" };
        _writerThread.Start();
    }

    private void SetupLoopbackCapture(AudioDevice? loopbackDevice)
    {
        if (loopbackDevice != null)
        {
            var mmDevice = _enumerator.GetDevice(loopbackDevice.DeviceId);
            _loopbackCapture = new WasapiLoopbackCapture(mmDevice);
            _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };
            _loopbackCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _loopbackPeakLevel = CalculatePeak(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);
                }
            };
        }
    }

    private void SetupMixer()
    {
        // 出力フォーマット決定
        var micFmt = _micCapture?.WaveFormat;
        var loopFmt = _loopbackCapture?.WaveFormat;
        int sampleRate = Math.Max(micFmt?.SampleRate ?? 0, loopFmt?.SampleRate ?? 0);
        int channels = Math.Max(micFmt?.Channels ?? 0, loopFmt?.Channels ?? 0);
        _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        var sources = new List<ISampleProvider>();

        if (_micBuffer != null)
            sources.Add(MatchFormat(_micBuffer.ToSampleProvider()));
        if (_loopbackBuffer != null)
            sources.Add(MatchFormat(_loopbackBuffer.ToSampleProvider()));

        if (sources.Count == 1)
        {
            _mixerSource = sources[0];
        }
        else
        {
            var mixer = new MixingSampleProvider(sources);
            mixer.ReadFully = true;
            _mixerSource = mixer;
        }
    }

    private ISampleProvider MatchFormat(ISampleProvider source)
    {
        // チャンネル変換（モノ → ステレオ）
        if (source.WaveFormat.Channels == 1 && _outputFormat!.Channels == 2)
            source = new MonoToStereoSampleProvider(source);

        // サンプルレート変換
        if (source.WaveFormat.SampleRate != _outputFormat!.SampleRate)
            source = new WdlResamplingSampleProvider(source, _outputFormat.SampleRate);

        return source;
    }

    // --- Writer thread ---

    private void WriterLoop()
    {
        // 20ms 分のチャンクサイズ
        int chunkFrames = _outputFormat!.SampleRate / 50;
        int chunkSamples = chunkFrames * _outputFormat.Channels;
        var sampleBuf = new float[chunkSamples];
        var byteBuf = new byte[chunkSamples * 4];

        // ループバックの初回コールバックを待機
        if (_loopbackCapture != null)
            Thread.Sleep(200);

        // Stopwatch ベースの精密タイミングで 20ms 間隔読み取り
        var sw = Stopwatch.StartNew();
        long ticksPer20ms = Stopwatch.Frequency / 50;
        long nextReadTick = sw.ElapsedTicks + ticksPer20ms;

        try
        {
            while (_isWriting)
            {
                long now = sw.ElapsedTicks;
                if (now < nextReadTick)
                {
                    // 残り時間が 2ms 以上なら Sleep、それ以下ならスピンウェイト
                    long remainMs = (nextReadTick - now) * 1000 / Stopwatch.Frequency;
                    if (remainMs >= 2)
                        Thread.Sleep((int)(remainMs - 1));
                    else
                        Thread.SpinWait(100);
                    continue;
                }

                // 遅延が溜まりすぎた場合はリセット（60ms以上遅れたらスキップ）
                if (now - nextReadTick > ticksPer20ms * 3)
                    nextReadTick = now;

                nextReadTick += ticksPer20ms;

                // データが無い側は ReadFully=true によりゼロパディング（無音）される
                int read = _mixerSource!.Read(sampleBuf, 0, chunkSamples);
                if (read <= 0) continue;

                Buffer.BlockCopy(sampleBuf, 0, byteBuf, 0, read * 4);

                _mp3Writer!.Write(byteBuf, 0, read * 4);
                _hasWrittenData = true;

                // 文字起こしサービスにサンプルを渡す
                _transcriptionService?.AddSamples(sampleBuf, read);
            }

            // 残りデータをフラッシュ
            int remaining;
            do
            {
                remaining = _mixerSource!.Read(sampleBuf, 0, chunkSamples);
                if (remaining > 0)
                {
                    Buffer.BlockCopy(sampleBuf, 0, byteBuf, 0, remaining * 4);
                    _mp3Writer!.Write(byteBuf, 0, remaining * 4);
                }
            } while (remaining > 0 && (_micBuffer?.BufferedBytes ?? 0) + (_loopbackBuffer?.BufferedBytes ?? 0) > 0);
        }
        catch (Exception ex)
        {
            RecordingError?.Invoke($"録音エラー: {ex.Message}");
        }
    }

    private static float CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        float peak = 0f;
        int bytesPerSample = format.BitsPerSample / 8;
        int sampleCount = bytesRecorded / bytesPerSample;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(buffer, i * 4);
                float abs = Math.Abs(sample);
                if (abs > peak) peak = abs;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(buffer, i * 2);
                float abs = Math.Abs(sample / 32768f);
                if (abs > peak) peak = abs;
            }
        }

        return peak;
    }

    // --- Stop / Cleanup ---

    public void StopRecording()
    {
        if (!_isWriting) return;

        _isWriting = false;
        _writerThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _writerThread = null;

        // 文字起こしセッション停止（残りバッファを処理してから終了）
        _transcriptionService?.StopSession();

        CleanupLoopback();

        _mp3Writer?.Dispose();
        _mp3Writer = null;
        _mixerSource = null;

        // マイクは常時キャプチャのため停止しない。ループバックのピークのみリセット。
        _loopbackPeakLevel = 0f;

        if (_currentSession != null)
        {
            _currentSession.StoppedAt = DateTime.Now;

            if (!_hasWrittenData && File.Exists(_currentSession.FilePath))
            {
                File.Delete(_currentSession.FilePath);
                _currentSession = null;
            }
        }
    }

    private void CleanupLoopback()
    {
        if (_loopbackCapture != null)
        {
            try { _loopbackCapture.StopRecording(); } catch { }
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }
        _loopbackBuffer = null;
    }

    public void Dispose()
    {
        StopRecording();
        StopMicMonitor();
        _enumerator.Dispose();
    }
}
