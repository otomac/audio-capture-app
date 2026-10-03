using System.Globalization;
using System.Windows.Threading;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

/// <summary>
/// メインウィンドウの ViewModel であり、補助ウィンドウの ViewModel の親（ADR-0008）。
/// </summary>
/// <remarks>
/// 持つのは画面をまたいで共有する状態（処理中フラグ・ステータス表示・直近の成果物）と、
/// サービス・設定の実体、そしてメインウィンドウの操作（デバイス・録音・自動開始・ライブ文字起こしの ON/OFF）である。
/// 補助ウィンドウの ViewModel はここで 1 度だけ生成して保持し、プロパティで公開する
/// （<see cref="Settings"/> / <see cref="FileTranscription"/> / <see cref="RecordingMetadata"/> / <see cref="LiveTranscript"/>）。
/// 子は親への参照を持ち、共有する状態は親の値を読み書きする（同じ値を 2 か所に持たない）。
/// </remarks>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly TranscriptionService _transcriptionService = new();
    private readonly SettingsService _settingsService = new();

    /// <summary>
    /// 話者ダイアライゼーション（REQ-TRX-DIA-03）。設定で無効なら <c>null</c> のままにする。
    /// 生成と破棄はここが持ち、<see cref="TranscriptionService"/> へは引数で貸すだけである
    /// （[ADR-0003](../../docs/adr/0003-speaker-diarization-with-sherpa-onnx.md) の決定 D11）。
    /// </summary>
    private readonly SpeakerDiarizationService? _speakerDiarizationService;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _clockTimer;

    /// <summary>
    /// 読み込んだ設定そのもの。SaveSettings はこのインスタンスを更新して保存する。
    /// 毎回 new すると、UI を持たない設定項目（無音カットの調整値など）が
    /// 保存のたびに既定値へ戻ってしまうため。
    /// </summary>
    private readonly AppSettings _settings;

    private DateTime _recordingStartTime;
    private bool _initializing;

    /// <summary>
    /// 直近の <c>Error</c> イベントの内容（T134）。ファイル文字起こしの失敗表示と、録音停止の完了表示に併記する。
    /// </summary>
    /// <remarks>
    /// ワーカースレッドで代入するが、string の代入は原子的で、読むのは継続（UI スレッド）だけ。
    /// </remarks>
    internal string? LastTranscriptionError { get; set; }

    private bool _suppressMicMuteWriteBack;

    // --- 補助ウィンドウの ViewModel（ADR-0008）。生成はコンストラクターで 1 度だけ行う ---

    /// <summary>設定ウィンドウの ViewModel（REQ-SETWIN-01）。モデル管理ダイアログの ViewModel もここが持つ。</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>ファイル文字起こしのオプション指定ダイアログの ViewModel（REQ-TRX-FILE-09）。</summary>
    public FileTranscriptionViewModel FileTranscription { get; }

    /// <summary>録音停止後のメタデータ入力ダイアログの ViewModel（REQ-REC-13）。</summary>
    public RecordingMetadataViewModel RecordingMetadata { get; }

    /// <summary>文字起こし表示ウィンドウの ViewModel（REQ-LIVEVIEW-01）。</summary>
    public LiveTranscriptViewModel LiveTranscript { get; }

    // --- 子 ViewModel が使うサービスと設定の実体。生成と破棄はここが持つ ---

    internal AudioCaptureService AudioCaptureService => _audioCaptureService;

    internal TranscriptionService TranscriptionService => _transcriptionService;

    internal SpeakerDiarizationService? SpeakerDiarizationService => _speakerDiarizationService;

    /// <summary>読み込んだ設定の実体。子は自分の担当する項目だけを読み書きし、保存は <see cref="SaveSettings"/> で行う。</summary>
    internal AppSettings AppSettings => _settings;

    public MainViewModel()
    {
        _initializing = true;
        // dBメーター用タイマー（常時動作、50ms間隔）
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        // 経過時間用タイマー（録音中のみ、1秒間隔）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            ElapsedTime = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture);
        };

        _audioCaptureService.RecordingError += OnRecordingError;
        _audioCaptureService.MicMuteChangedExternally += OnMicMuteChangedExternally;
        _transcriptionService.Error += msg =>
        {
            // T134: 続く「失敗しました」の 1 行に理由を併記するため控えておく（REQ-TRX-FILE-12）。
            LastTranscriptionError = msg;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                StatusMessage = $"文字起こしエラー: {msg}");
        };
        _transcriptionService.RuntimeInfo += runtime =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                StatusMessage = $"Whisperランタイム: {runtime}");

        _settings = _settingsService.Load();

        // REQ-REC-12 / REQ-CFG-10: 閾値・継続・クールダウンは起動時に固定（UI 無し）。ON/OFF だけが UI にある。
        // 設定ウィンドウの ViewModel が ON/OFF を写すときに判定器を触るので、子より先に作る。
        _autoStartTrigger = new AutoStartTrigger(new AutoStartOptions(
            _settings.AutoStartThresholdDb,
            _settings.AutoStartSustainSeconds,
            _settings.AutoStartCooldownSeconds,
            _settings.AutoStartDipGraceSeconds));

        // 子 ViewModel は設定を読んだ直後に作る。以降の初期化（ライブ文字起こしの ON/OFF など）が子を使うため。
        Settings = new SettingsViewModel(this);
        FileTranscription = new FileTranscriptionViewModel(this);
        RecordingMetadata = new RecordingMetadataViewModel(this);
        LiveTranscript = new LiveTranscriptViewModel();
        // 文字起こしワーカースレッドから発火するため、必ず Dispatcher を経由する（NFR-01）
        _transcriptionService.SegmentTranscribed += LiveTranscript.QueueLine;

        _transcriptionService.SilenceCut = new SilenceCutOptions(
            _settings.SilenceRmsThreshold,
            _settings.SilenceMergeGapSeconds,
            _settings.VoicedPaddingSeconds);

        var diarizationOptions = new SpeakerDiarizationOptions(
            _settings.SpeakerSegmentationModelPath,
            _settings.SpeakerEmbeddingModelPath,
            _settings.SpeakerClusteringThreshold,
            _settings.KnownSpeakerCount,
            _settings.SpeakerDiarizationThreads);

        // REQ-TRX-DIA-15: 起動時に 1 度だけ状態を判定する。**読み込みはしない**
        // （存在検査だけ。ADR-0003 N2 の遅延読み込みを維持する）。
        var availability = DiarizationAvailabilityFor(
            _settings.SpeakerDiarizationEnabled,
            SpeakerDiarizationService.ModelFilesExist(diarizationOptions));
        _diarizationAvailability = availability;
        SpeakerDiarizationStatus = DiarizationStatusTextFor(availability);
        SpeakerDiarizationTooltip = DiarizationTooltipFor(availability);

        // 有効なときだけ生成する。モデルの読み込みは初回の実行まで遅らせるため、
        // ここで生成してもモデルが未配置なら起動を妨げない（エラーは実行時に出る）。
        if (_settings.SpeakerDiarizationEnabled)
        {
            _speakerDiarizationService = new SpeakerDiarizationService(diarizationOptions);
        }

        // ライブ文字起こしが ON なら、変更ハンドラーがここでモデルの読み込みを始める（読み込み中の二重起動は
        // SettingsViewModel.TryLoadWhisperModel が弾く）。サービスと話者識別の準備を済ませた後に置くこと。
        TranscriptionEnabled = _settings.TranscriptionEnabled;

        // モデルパスが設定されていれば常にロードする（ファイル文字起こしは
        // ライブ用チェックボックスと独立して動作する）
        if (!string.IsNullOrEmpty(Settings.WhisperModelPath))
        {
            Settings.TryLoadWhisperModel();
        }

        RefreshDevicesInternal();

        // 前回のマイク選択を復元
        if (_settings.LastSelectedDeviceId != null)
        {
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == _settings.LastSelectedDeviceId);
        }
        SelectedCaptureDevice ??= CaptureDevices.FirstOrDefault(d => d.IsDefault) ?? CaptureDevices.FirstOrDefault();

        // 前回のスピーカー選択を復元
        if (_settings.LastSelectedLoopbackDeviceId != null)
        {
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == _settings.LastSelectedLoopbackDeviceId);
        }

        _initializing = false;
    }

    // --- 共通プロパティ ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSettingsCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSettingsCommand))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSettingsCommand))]
    private bool _isTranscribingFile;

    public bool IsNotBusy => !IsRecording && !IsStopping && !IsTranscribingFile;

    // IsNotBusy の変化は子 ViewModel も見ている（設定ウィンドウの操作可否。SettingsViewModel が中継する）。
    // フラグを足したら、ここで IsNotBusy の通知を出すこと。
    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
        OnPropertyChanged(nameof(RecordingStatusColor));
        OnPropertyChanged(nameof(IsNotBusy));
    }

    partial void OnIsStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
        OnPropertyChanged(nameof(IsNotBusy));
    }

    /// <remarks>
    /// 書き手は <see cref="FileTranscriptionViewModel"/> だけである。ダイアログ側の表示の更新はそちらが行う。
    /// </remarks>
    partial void OnIsTranscribingFileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
    }

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private string _statusMessage = "待機中";

    /// <summary>
    /// 直近の成果物のパス（REQ-OPEN-01）。録音とファイル文字起こしで保存先が異なるため、
    /// 「設定上の保存先」ではなくここを開く対象にする。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    private string _lastResultPath = string.Empty;

    /// <summary>
    /// 「保存先を開く」の可否。成果物が未設定なら無効（REQ-OPEN-04）。加えて録音中・停止処理中・
    /// ファイル文字起こし中も無効にする（REQ-OPEN-05）。保持しているのは進行中の作業ではなく
    /// それ以前の成果物であり、開けてしまうと誤解を招くため。
    /// </summary>
    internal static bool CanOpenResultFolderFor(string lastResultPath, bool isNotBusy)
        => isNotBusy && !string.IsNullOrEmpty(lastResultPath);

    private bool CanOpenResultFolder => CanOpenResultFolderFor(LastResultPath, IsNotBusy);

    [RelayCommand(CanExecute = nameof(CanOpenResultFolder))]
    private void OpenResultFolder()
    {
        var arguments = BuildExplorerArguments(LastResultPath);
        if (arguments == null)
        {
            StatusMessage = $"保存先が見つかりません: {LastResultPath}";
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }
        // CA1031: シェル起動は環境依存で任意の例外を投げうる。フォルダを開けなくても
        //         アプリの動作には影響しないため、画面のステータスに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            StatusMessage = $"保存先を開けませんでした: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// エクスプローラーへ渡す引数を組み立てる。成果物が存在すれば選択状態で開き
    /// （REQ-OPEN-02）、無ければ親フォルダを開く（REQ-OPEN-03）。
    /// どちらも存在しなければ <c>null</c>。
    /// </summary>
    internal static string? BuildExplorerArguments(string resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return null;
        }

        if (System.IO.File.Exists(resultPath))
        {
            return $"/select,\"{resultPath}\"";
        }

        var folder = System.IO.Path.GetDirectoryName(resultPath);
        return !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder)
            ? $"\"{folder}\""
            : null;
    }

    /// <summary>
    /// 画面の値を設定へ写して保存する（REQ-CFG-05）。子 ViewModel の担当する項目もここで集める。
    /// </summary>
    /// <remarks>
    /// UI を持たない設定項目（無音カットの調整値など）を消さないため、
    /// 読み込んだインスタンスの UI 対応プロパティだけを更新して保存する。
    /// ファイル用のモデル名（REQ-CFG-09）だけは子が <see cref="AppSettings"/> へ直接書く（画面の値と 1 対 1 でないため）。
    /// </remarks>
    internal void SaveSettings()
    {
        _settings.OutputFolder = Settings.OutputFolder;
        _settings.LastSelectedDeviceId = SelectedCaptureDevice?.DeviceId;
        _settings.LastSelectedLoopbackDeviceId = SelectedRenderDevice?.DeviceId;
        _settings.TranscriptionEnabled = TranscriptionEnabled;
        _settings.WhisperModelPath = Settings.WhisperModelPath;
        _settings.WhisperModelList.Clear();
        foreach (var entry in Settings.WhisperModels)
        {
            _settings.WhisperModelList.Add(entry);
        }
        _settings.UseGpuForTranscription = Settings.UseGpuForTranscription;
        _settings.AutoStartRecordingEnabled = Settings.AutoStartRecordingEnabled;
        _settings.LiveTranscriptionLanguage = Settings.SelectedLiveLanguage.Code;
        _settings.FileTranscriptionLanguage = FileTranscription.SelectedFileLanguage.Code;
        _settingsService.Save(_settings);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // アンマネージドリソースを直接は保持しないため、ファイナライザーは持たず
    // disposing == false のときは何もしない。
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        _meterTimer.Stop();
        _clockTimer.Stop();
        FileTranscription.Dispose();
        _audioCaptureService.Dispose();
        _transcriptionService.Dispose();
        _speakerDiarizationService?.Dispose();
    }
}