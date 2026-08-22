using System.Diagnostics;
using System.Globalization;
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
    private MMDevice? _micDevice;
    private WasapiCapture? _micCapture;
    private BufferedWaveProvider? _micBuffer;

    // ループバック常時キャプチャ（マイクと同じく録音と独立したライフサイクル）
    private MMDevice? _loopbackDevice;
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

    // ミュート
    private volatile bool _isMicMuted;
    private volatile bool _isSpeakerMuted;
    // REQ-MUTE-08: マイクとループバックは別スレッドのコールバックで動作するため、
    //              無音バッファを共有すると再確保と読み取りが競合する。必ず分ける。
    private byte[]? _micSilenceBuffer;
    private byte[]? _loopbackSilenceBuffer;

    // ハードウェアミュート同期
    private AudioEndpointVolume? _micEndpointVolume;
    private AudioEndpointVolumeNotificationDelegate? _micVolumeHandler;
    // アプリ→OS 書き込み中フラグ。OS からの折り返し通知を弾く
    private volatile bool _suppressMuteNotification;

    // ピークレベル測定
    private volatile float _micPeakLevel;
    private volatile float _loopbackPeakLevel;

    /// <summary>
    /// ループバックのピークを 0 とみなすまでの無音許容時間 (ms)。
    /// WASAPI のループバックキャプチャは再生エンドポイントがアイドルのとき
    /// パケットを生成せず <c>DataAvailable</c> が発火しないため、これが無いと
    /// メーターが最後の値で固着する（REQ-LVL-05）。
    /// </summary>
    internal const int LoopbackSilenceTimeoutMs = 200;

    /// <summary>最後にループバックのデータを受け取った時刻 (<see cref="Environment.TickCount64"/>)。</summary>
    private long _loopbackLastDataTicks;

    public bool IsRecording => _isWriting;
    public RecordingSession? CurrentSession => _currentSession;
    public bool IsMicMuted
    {
        get => _isMicMuted;
        set
        {
            if (_isMicMuted == value) return;
            _isMicMuted = value;
            TryApplyMuteToEndpoint(value);
        }
    }

    private void TryApplyMuteToEndpoint(bool mute)
    {
        var vol = _micEndpointVolume;
        if (vol == null) return;
        try
        {
            if (vol.Mute == mute) return;
            _suppressMuteNotification = true;
            vol.Mute = mute;
        }
        // CA1031: AudioEndpointVolume は COM 経由で、権限不足等に対して投げる型を列挙できない。
        //         OS 側ミュートの反映に失敗してもソフトミュートで動作を継続する。
#pragma warning disable CA1031
        catch { }
#pragma warning restore CA1031
        finally
        {
            _suppressMuteNotification = false;
        }
    }

    public bool IsSpeakerMuted
    {
        get => _isSpeakerMuted;
        set => _isSpeakerMuted = value;
    }

    public float MicPeakLevel => _micPeakLevel;

    /// <summary>
    /// スピーカー（ループバック）のピークレベル。無音でコールバックが途絶えた場合は 0 を返す
    /// （REQ-LVL-05）。
    /// </summary>
    public float LoopbackPeakLevel => ApplySilenceTimeout(
        _loopbackPeakLevel,
        Interlocked.Read(ref _loopbackLastDataTicks),
        Environment.TickCount64,
        LoopbackSilenceTimeoutMs);

    /// <summary>
    /// 最後にデータを受け取ってから <paramref name="timeoutMs"/> を超えて経過していれば 0 を、
    /// そうでなければ <paramref name="peak"/> をそのまま返す。
    /// </summary>
    internal static float ApplySilenceTimeout(float peak, long lastDataTicks, long nowTicks, int timeoutMs)
        => nowTicks - lastDataTicks > timeoutMs ? 0f : peak;

    public event Action<string>? RecordingError;

    /// <summary>OS 側からマイクミュート状態が変わった時に発火（非UIスレッド）</summary>
    public event Action<bool>? MicMuteChangedExternally;

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
        try
        {
            using var defaultDevice = _enumerator.GetDefaultAudioEndpoint(dataFlow, role);
            defaultId = defaultDevice.ID;
        }
        // CA1031: 既定デバイスが無い状態は正常系であり、COM 由来の例外型を列挙できない。
        //         既定が取れなければ IsDefault が全て false になるだけで列挙は続行する。
#pragma warning disable CA1031
        catch { }
#pragma warning restore CA1031

        var devices = new List<AudioDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
        {
            devices.Add(new AudioDevice
            {
                DeviceId = device.ID,
                FriendlyName = device.FriendlyName,
                IsDefault = device.ID == defaultId
            });
            device.Dispose();
        }
        return devices;
    }

    // --- マイク常時モニター ---

    /// <summary>
    /// マイクの常時キャプチャを開始する（REQ-DEV-03）。
    /// 失敗した場合は内部状態を後始末して <c>false</c> を返す（REQ-DEV-08）。
    /// </summary>
    /// <remarks>
    /// ここで例外を投げてはならない。本メソッドは
    /// <c>MainViewModel.SelectedCaptureDevice</c> の setter から呼ばれ、その setter は
    /// コンストラクターからも実行されるため、例外が漏れると
    /// <c>MainWindow</c> の生成が失敗して**アプリが起動できずクラッシュする**
    /// （実際に `WasapiCapture.StartRecording()` の `E_HANDLE` で発生した）。
    /// </remarks>
    public bool StartMicMonitor(AudioDevice device)
    {
        StopMicMonitor();

        try
        {
            SetupMicCapture(device);
            return true;
        }
        // CA1031: ドライバー都合で COM 由来の例外（E_HANDLE 等）が出るが型を列挙できない。
        //         デバイス選択・アプリ起動を失敗させずに機能低下として扱う。
#pragma warning disable CA1031
        catch
        {
            StopMicMonitor();
            return false;
        }
#pragma warning restore CA1031
    }

    private void SetupMicCapture(AudioDevice device)
    {
        _micDevice = _enumerator.GetDevice(device.DeviceId);

        // ハードウェアミュート同期セットアップ
        try
        {
            _micEndpointVolume = _micDevice.AudioEndpointVolume;
            // 起動時 / デバイス切替時の初期同期。setter ではなくフィールドに直接書く
            // （setter 経由だと OS へ無駄な書き戻しが発生する）
            _isMicMuted = _micEndpointVolume.Mute;

            _micVolumeHandler = OnMicVolumeNotification;
            _micEndpointVolume.OnVolumeNotification += _micVolumeHandler;
        }
        // CA1031: AudioEndpointVolume を取得できないデバイスがあり、COM 由来の例外型を
        //         列挙できない。取得できなければソフトミュートのみで動作を継続する。
#pragma warning disable CA1031
        catch
        {
            // AudioEndpointVolume が取れないデバイスはソフトミュートのみで動作
            _micEndpointVolume = null;
            _micVolumeHandler = null;
        }
#pragma warning restore CA1031

        _micCapture = new WasapiCapture(_micDevice) { ShareMode = AudioClientShareMode.Shared };
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
                if (_isMicMuted)
                {
                    // ミュート時はバッファに無音を書き込む
                    if (_micSilenceBuffer == null || _micSilenceBuffer.Length < e.BytesRecorded)
                        _micSilenceBuffer = new byte[e.BytesRecorded];
                    _micBuffer.AddSamples(_micSilenceBuffer, 0, e.BytesRecorded);
                    _micPeakLevel = 0f;
                }
                else
                {
                    _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _micPeakLevel = CalculatePeak(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);

                    // 録音中は文字起こしサービスにマイク音声を渡す
                    if (_isWriting && _transcriptionService != null)
                    {
                        var floats = BytesToFloats(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat);
                        if (floats != null)
                            _transcriptionService.AddSamples(AudioSourceType.Mic, floats, floats.Length);
                    }
                }
            }
        };
        _micCapture.StartRecording();
    }

    public void StopMicMonitor()
    {
        // 先にイベント購読を外す（コールバック競合の最小化）
        if (_micEndpointVolume != null && _micVolumeHandler != null)
        {
            // CA1031: 停止処理は何が起きても後続の破棄まで進める必要がある（COM 由来のため型を列挙できない）
#pragma warning disable CA1031
            try { _micEndpointVolume.OnVolumeNotification -= _micVolumeHandler; } catch { }
#pragma warning restore CA1031
        }
        _micVolumeHandler = null;
        _micEndpointVolume = null; // MMDevice の Dispose で解放される

        if (_micCapture != null)
        {
            // CA1031: 停止に失敗しても Dispose まで進める必要がある（NAudio 内部の例外型を列挙できない）
#pragma warning disable CA1031
            try { _micCapture.StopRecording(); } catch { }
#pragma warning restore CA1031
            _micCapture.Dispose();
            _micCapture = null;
        }
        _micBuffer = null;
        _micSilenceBuffer = null;
        _micDevice?.Dispose();
        _micDevice = null;
        _micPeakLevel = 0f;
    }

    private void OnMicVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_suppressMuteNotification) return; // アプリ発の書き込みによる通知ならスキップ
        var newMute = data.Muted;
        if (_isMicMuted == newMute) return;
        _isMicMuted = newMute;
        MicMuteChangedExternally?.Invoke(newMute);
    }

    // --- 録音制御 ---

    public void SetTranscriptionService(TranscriptionService? service)
    {
        _transcriptionService = service;
    }

    public DateTime StartRecording(AudioDevice? micDevice, AudioDevice? loopbackDevice, string outputFolder)
    {
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");
        if (micDevice == null && loopbackDevice == null)
            throw new InvalidOperationException("少なくとも1つのデバイスを選択してください。");

        try
        {
            // マイク・ループバックとも既に常時キャプチャ中。ここではミキサーだけを組む。
            SetupMixer();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"録音の開始に失敗しました: {ex.Message}", ex);
        }

        var now = DateTime.Now;
        // ファイル名は機械可読な文字列のためカルチャー非依存にする
        var fileName = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".mp3";
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
            _currentSession = null;
            throw new InvalidOperationException($"MP3ファイルの作成に失敗しました: {ex.Message}", ex);
        }

        _hasWrittenData = false;

        // 文字起こしセッション開始
        if (_transcriptionService is { IsModelLoaded: true })
        {
            if (_micCapture != null)
                _transcriptionService.RegisterSource(AudioSourceType.Mic, "マイク",
                    _micCapture.WaveFormat.SampleRate, _micCapture.WaveFormat.Channels);
            if (_loopbackCapture != null)
                _transcriptionService.RegisterSource(AudioSourceType.Speaker, "スピーカー",
                    _loopbackCapture.WaveFormat.SampleRate, _loopbackCapture.WaveFormat.Channels);
            _transcriptionService.StartSession(filePath, now);
        }

        // REQ-REC-05: 常時モニタのバッファには録音開始前の音声が溜まっている。
        //             両方クリアして録音開始時点以降の音声のみ使う。
        _micBuffer?.ClearBuffer();
        _loopbackBuffer?.ClearBuffer();

        _isWriting = true;
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "AudioMixerWriter" };
        _writerThread.Start();

        return now;
    }

    // --- スピーカー（ループバック）常時モニター ---

    /// <summary>
    /// スピーカーの常時ループバックキャプチャを開始する（REQ-DEV-06）。
    /// 失敗した場合は内部状態を後始末して <c>false</c> を返す（REQ-DEV-08）。
    /// デバイス選択の途中で例外を投げるとアプリが落ちるため、ここで throw しない。
    /// </summary>
    public bool StartLoopbackMonitor(AudioDevice device)
    {
        StopLoopbackMonitor();

        try
        {
            _loopbackDevice = _enumerator.GetDevice(device.DeviceId);
            _loopbackCapture = new WasapiLoopbackCapture(_loopbackDevice);
            _loopbackBuffer = new BufferedWaveProvider(_loopbackCapture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(5),
                ReadFully = true,
                DiscardOnBufferOverflow = true
            };
            _loopbackCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                {
                    return;
                }

                // 無音タイムアウト判定の基準時刻（REQ-LVL-05）
                Interlocked.Exchange(ref _loopbackLastDataTicks, Environment.TickCount64);

                if (_isSpeakerMuted)
                {
                    // ミュート時はバッファに無音を書き込む
                    if (_loopbackSilenceBuffer == null || _loopbackSilenceBuffer.Length < e.BytesRecorded)
                        _loopbackSilenceBuffer = new byte[e.BytesRecorded];
                    _loopbackBuffer.AddSamples(_loopbackSilenceBuffer, 0, e.BytesRecorded);
                    _loopbackPeakLevel = 0f;
                }
                else
                {
                    _loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    _loopbackPeakLevel = CalculatePeak(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);

                    // 録音中のみ文字起こしサービスにスピーカー音声を渡す
                    if (_isWriting && _transcriptionService != null)
                    {
                        var floats = BytesToFloats(e.Buffer, e.BytesRecorded, _loopbackCapture.WaveFormat);
                        if (floats != null)
                            _transcriptionService.AddSamples(AudioSourceType.Speaker, floats, floats.Length);
                    }
                }
            };
            _loopbackCapture.StartRecording();
            return true;
        }
        // CA1031: ループバック非対応デバイスやドライバー都合で COM 由来の例外（E_HANDLE 等）が
        //         出るが型を列挙できない。デバイス選択操作を失敗させずに機能低下として扱う。
#pragma warning disable CA1031
        catch
        {
            StopLoopbackMonitor();
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>スピーカーの常時ループバックキャプチャを停止する（REQ-DEV-07）。</summary>
    public void StopLoopbackMonitor()
    {
        if (_loopbackCapture != null)
        {
            // CA1031: 停止に失敗しても Dispose まで進める必要がある（NAudio 内部の例外型を列挙できない）
#pragma warning disable CA1031
            try { _loopbackCapture.StopRecording(); } catch { }
#pragma warning restore CA1031
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }
        _loopbackBuffer = null;
        _loopbackSilenceBuffer = null;
        _loopbackDevice?.Dispose();
        _loopbackDevice = null;
        _loopbackPeakLevel = 0f;
        Interlocked.Exchange(ref _loopbackLastDataTicks, 0);
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

        // ループバックの初回コールバックを待機。
        // 常時モニタ化により通常は録音開始前からデータが流れているが、
        // 「スピーカーを選択した直後に録音開始」した場合はまだ初回コールバックが
        // 来ていないことがあるため、この待機は残す。
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
        // CA1031: ワーカースレッド境界。例外を漏らすとプロセスごと落ちて録音データを失うため、
        //         全例外を RecordingError イベントに変換して UI へ通知する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            RecordingError?.Invoke($"録音エラー: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    internal static float[]? BytesToFloats(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int count = bytesRecorded / 4;
            var floats = new float[count];
            Buffer.BlockCopy(buffer, 0, floats, 0, bytesRecorded);
            return floats;
        }
        if (format.BitsPerSample == 16)
        {
            int count = bytesRecorded / 2;
            var floats = new float[count];
            for (int i = 0; i < count; i++)
                floats[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return floats;
        }
        return null;
    }

    internal static float CalculatePeak(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        float peak = 0f;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            int count = bytesRecorded / 4;
            for (int i = 0; i < count; i++)
            {
                float abs = Math.Abs(BitConverter.ToSingle(buffer, i * 4));
                if (abs > peak) peak = abs;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            int count = bytesRecorded / 2;
            for (int i = 0; i < count; i++)
            {
                float abs = Math.Abs(BitConverter.ToInt16(buffer, i * 2) / 32768f);
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

        // ライタースレッドの終了を待つ（_mp3Writer を安全に Dispose するため）
        if (_writerThread != null && !_writerThread.Join(timeout: TimeSpan.FromSeconds(5)))
        {
            // タイムアウト時でもスレッドは _isWriting=false で間もなく終了するため
            // ログだけ残して続行する
            RecordingError?.Invoke("録音スレッドの停止がタイムアウトしました");
        }
        _writerThread = null;

        // 文字起こしセッション停止（残りバッファを処理してから終了）
        _transcriptionService?.StopSession();

        _mp3Writer?.Dispose();
        _mp3Writer = null;
        _mixerSource = null;

        // REQ-LVL-04: マイク・ループバックとも常時キャプチャのため停止もリセットもしない。
        //             メーターは録音停止後もそのまま動き続ける。

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

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // アンマネージドリソースを直接は保持しない（NAudio 側が保持する）ため
    // ファイナライザーは持たず、disposing == false のときは何もしない。
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        StopRecording();
        StopMicMonitor();
        StopLoopbackMonitor();
        _enumerator.Dispose();
    }
}