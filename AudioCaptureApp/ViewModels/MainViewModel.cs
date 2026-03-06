using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly TranscriptionService _transcriptionService = new();
    private readonly SettingsService _settingsService = new();
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _clockTimer;
    private DateTime _recordingStartTime;

    public MainViewModel()
    {
        // dBメーター用タイマー（常時動作、50ms間隔）
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        // 経過時間用タイマー（録音中のみ、1秒間隔）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            ElapsedTime = elapsed.ToString(@"hh\:mm\:ss");
        };

        _audioCaptureService.RecordingError += OnRecordingError;
        _transcriptionService.Error += msg =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"文字起こしエラー: {msg}");

        var settings = _settingsService.Load();
        OutputFolder = settings.OutputFolder;
        TranscriptionEnabled = settings.TranscriptionEnabled;
        WhisperModelPath = settings.WhisperModelPath;

        // 文字起こし有効時、モデルを読み込む
        if (TranscriptionEnabled)
            TryLoadWhisperModel();

        RefreshDevicesInternal();

        // 前回のマイク選択を復元
        if (settings.LastSelectedDeviceId != null)
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == settings.LastSelectedDeviceId);
        SelectedCaptureDevice ??= CaptureDevices.FirstOrDefault(d => d.IsDefault) ?? CaptureDevices.FirstOrDefault();

        // 前回のスピーカー選択を復元
        if (settings.LastSelectedLoopbackDeviceId != null)
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == settings.LastSelectedLoopbackDeviceId);
    }

    // --- マイク入力デバイス ---
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedCaptureDevice;

    partial void OnSelectedCaptureDeviceChanged(AudioDevice? value)
    {
        if (value != null)
            _audioCaptureService.StartMicMonitor(value);
        else
            _audioCaptureService.StopMicMonitor();
    }

    // --- スピーカー（ループバック）デバイス ---
    public ObservableCollection<AudioDevice> RenderDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedRenderDevice;

    // --- 共通プロパティ ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectWhisperModelCommand))]
    private bool _isRecording;

    public string RecordingStatusText => IsRecording ? "録音中" : "停止中";
    public string RecordingStatusColor => IsRecording ? "#FFCC0000" : "#FF000000";

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
        OnPropertyChanged(nameof(RecordingStatusColor));
    }

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private string _statusMessage = "待機中";

    // --- 文字起こし設定 ---
    [ObservableProperty]
    private bool _transcriptionEnabled;

    partial void OnTranscriptionEnabledChanged(bool value)
    {
        if (value)
            TryLoadWhisperModel();
        else
            _audioCaptureService.SetTranscriptionService(null);
        SaveSettings();
    }

    [ObservableProperty]
    private string _whisperModelPath = string.Empty;

    [ObservableProperty]
    private string _transcriptionStatus = "";

    [ObservableProperty]
    private double _micLevelDb = -60.0;

    [ObservableProperty]
    private double _loopbackLevelDb = -60.0;

    // --- コマンド ---

    private bool CanStartRecording =>
        (SelectedCaptureDevice != null || SelectedRenderDevice != null) && !IsRecording;

    private bool CanStopRecording => IsRecording;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        try
        {
            _audioCaptureService.StartRecording(SelectedCaptureDevice, SelectedRenderDevice, OutputFolder);
            IsRecording = true;
            _recordingStartTime = DateTime.Now;
            ElapsedTime = "00:00:00";
            _clockTimer.Start();
            StatusMessage = "録音中...";
            SaveSettings();
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = $"エラー: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private void StopRecording()
    {
        _clockTimer.Stop();
        _audioCaptureService.StopRecording();
        IsRecording = false;
        LoopbackLevelDb = -60.0;

        var session = _audioCaptureService.CurrentSession;
        if (session != null)
        {
            var txtPath = System.IO.Path.ChangeExtension(session.FilePath, ".txt");
            var hasTxt = TranscriptionEnabled && System.IO.File.Exists(txtPath);
            StatusMessage = hasTxt
                ? $"保存完了: {session.FilePath} (文字起こし: {txtPath})"
                : $"保存完了: {session.FilePath}";
        }
        else
        {
            StatusMessage = "録音データなし（ファイルは作成されませんでした）";
        }
    }

    private bool CanSelectOutputFolder => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanSelectOutputFolder))]
    private void SelectOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存先フォルダを選択" };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
            SaveSettings();
        }
    }

    private bool CanRefreshDevices => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private void RefreshDevices()
    {
        RefreshDevicesInternal();
        StatusMessage = $"デバイス一覧を更新しました (マイク {CaptureDevices.Count} / スピーカー {RenderDevices.Count})";
    }

    private void RefreshDevicesInternal()
    {
        _audioCaptureService.RefreshDevices();

        var prevCapture = SelectedCaptureDevice?.DeviceId;
        var prevRender = SelectedRenderDevice?.DeviceId;

        CaptureDevices.Clear();
        foreach (var d in _audioCaptureService.GetCaptureDevices())
            CaptureDevices.Add(d);

        RenderDevices.Clear();
        foreach (var d in _audioCaptureService.GetRenderDevices())
            RenderDevices.Add(d);

        if (prevCapture != null)
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == prevCapture);
        if (prevRender != null)
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == prevRender);
    }

    private void UpdateMeters()
    {
        MicLevelDb = PeakToDb(_audioCaptureService.MicPeakLevel);
        LoopbackLevelDb = PeakToDb(_audioCaptureService.LoopbackPeakLevel);
    }

    private static double PeakToDb(float peak)
    {
        if (peak <= 0f) return -60.0;
        double db = 20.0 * Math.Log10(peak);
        return Math.Clamp(db, -60.0, 3.0);
    }

    private void OnRecordingError(string message)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _clockTimer.Stop();
            IsRecording = false;
            StatusMessage = $"エラー: {message}";
        });
    }

    private bool CanSelectWhisperModel => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanSelectWhisperModel))]
    private void SelectWhisperModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Whisperモデルファイルを選択",
            Filter = "GGMLモデル (*.bin)|*.bin|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            WhisperModelPath = dialog.FileName;
            TryLoadWhisperModel();
            SaveSettings();
        }
    }

    private void TryLoadWhisperModel()
    {
        if (string.IsNullOrEmpty(WhisperModelPath))
        {
            TranscriptionStatus = "モデルパス未設定";
            _audioCaptureService.SetTranscriptionService(null);
            return;
        }

        if (!System.IO.File.Exists(WhisperModelPath))
        {
            TranscriptionStatus = "モデルファイルが見つかりません";
            _audioCaptureService.SetTranscriptionService(null);
            return;
        }

        TranscriptionStatus = "モデル読み込み中...";
        if (_transcriptionService.LoadModel(WhisperModelPath))
        {
            TranscriptionStatus = "モデル読み込み完了";
            _audioCaptureService.SetTranscriptionService(_transcriptionService);
        }
        else
        {
            TranscriptionStatus = "モデル読み込み失敗";
            _audioCaptureService.SetTranscriptionService(null);
        }
    }

    private void SaveSettings()
    {
        _settingsService.Save(new AppSettings
        {
            OutputFolder = OutputFolder,
            LastSelectedDeviceId = SelectedCaptureDevice?.DeviceId,
            LastSelectedLoopbackDeviceId = SelectedRenderDevice?.DeviceId,
            TranscriptionEnabled = TranscriptionEnabled,
            WhisperModelPath = WhisperModelPath
        });
    }

    public void Dispose()
    {
        _meterTimer.Stop();
        _clockTimer.Stop();
        _audioCaptureService.Dispose();
        _transcriptionService.Dispose();
    }
}
