using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioCaptureApp.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、デバイスの選択・モニタリング・ミュート・レベルメーターを担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D）。
public partial class MainViewModel
{
    // --- マイク入力デバイス ---
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedCaptureDevice;

    partial void OnSelectedCaptureDeviceChanged(AudioDevice? value)
    {
        if (value != null)
        {
            if (_audioCaptureService.StartMicMonitor(value))
            {
                // 起動時／デバイス切替時に OS の現在ミュート値を ViewModel に反映
                _suppressMicMuteWriteBack = true;
                try { IsMicMuted = _audioCaptureService.IsMicMuted; }
                finally { _suppressMicMuteWriteBack = false; }
            }
            else
            {
                // REQ-DEV-08: 失敗しても選択操作自体は成功させ、機能低下として通知する。
                // REQ-DEV-09: OS 側のミュート状態は取得できないため反映しない。
                StatusMessage = $"マイクの音声を取得できません: {value.FriendlyName}";
            }
        }
        else
        {
            _audioCaptureService.StopMicMonitor();
        }
    }

    // --- スピーカー（ループバック）デバイス ---
    public ObservableCollection<AudioDevice> RenderDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedRenderDevice;

    partial void OnSelectedRenderDeviceChanged(AudioDevice? value)
    {
        // マイクと同じく、録音の有無に関わらずデバイス選択でモニタリングを開始する
        // （REQ-DEV-06 / REQ-DEV-07）
        if (value != null)
        {
            if (!_audioCaptureService.StartLoopbackMonitor(value))
            {
                // REQ-DEV-08: 失敗しても選択操作自体は成功させ、機能低下として通知する
                StatusMessage = $"スピーカーの音声を取得できません: {value.FriendlyName}";
            }
        }
        else
        {
            _audioCaptureService.StopLoopbackMonitor();
        }
    }

    [ObservableProperty]
    private bool _isMicMuted;

    partial void OnIsMicMutedChanged(bool value)
    {
        if (_suppressMicMuteWriteBack) return;
        _audioCaptureService.IsMicMuted = value;
    }

    [ObservableProperty]
    private bool _isSpeakerMuted;

    partial void OnIsSpeakerMutedChanged(bool value)
    {
        _audioCaptureService.IsSpeakerMuted = value;
    }

    [ObservableProperty]
    private double _micLevelDb = -60.0;

    [ObservableProperty]
    private double _loopbackLevelDb = -60.0;

    private bool CanRefreshDevices => !IsRecording && !IsStopping && !IsTranscribingFile;

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
        {
            CaptureDevices.Add(d);
        }

        RenderDevices.Clear();
        foreach (var d in _audioCaptureService.GetRenderDevices())
        {
            RenderDevices.Add(d);
        }

        if (prevCapture != null)
        {
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == prevCapture);
        }
        if (prevRender != null)
        {
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == prevRender);
        }
    }

    private void UpdateMeters()
    {
        MicLevelDb = PeakToDb(_audioCaptureService.MicPeakLevel);
        LoopbackLevelDb = PeakToDb(_audioCaptureService.LoopbackPeakLevel);
    }

    internal static double PeakToDb(float peak)
    {
        if (peak <= 0f)
        {
            return -60.0;
        }
        double db = 20.0 * Math.Log10(peak);
        return Math.Clamp(db, -60.0, 3.0);
    }

    private void OnMicMuteChangedExternally(bool newMute)
    {
        // OnVolumeNotification は非UIスレッドで発火するため Dispatcher 経由
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (IsMicMuted == newMute) return;
            _suppressMicMuteWriteBack = true;
            try { IsMicMuted = newMute; }
            finally { _suppressMicMuteWriteBack = false; }
        });
    }
}