using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

/// <summary>
/// 設定ウィンドウ（<c>SettingsWindow</c>）の ViewModel（REQ-SETWIN-01〜06、ADR-0008）。
/// 保存先フォルダ・録音の自動開始・ライブ文字起こしの言語・GPU の使用・Whisper モデルの登録一覧と
/// ライブ用の選択（とその読み込み）を持つ。生成と保持は <see cref="MainViewModel"/> が行い、
/// <see cref="MainViewModel.Settings"/> で公開する。
/// </summary>
/// <remarks>
/// ここの値を親が読む（録音の保存先、自動開始の ON/OFF、モデルの読み込み状態）。
/// 親の状態の変化を受けるのはコンストラクターの <see cref="OnMainPropertyChanged"/> 1 か所だけにしてある
/// （処理中フラグ <see cref="MainViewModel.IsNotBusy"/> の中継）。
/// モデル管理ダイアログ（REQ-MODELWIN-01）の ViewModel は <see cref="WhisperModelsManager"/> として持つ。
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    /// <summary>コンストラクターで設定値を写している間、保存と読み込み直しを抑止する。</summary>
    private readonly bool _initializing;

    internal SettingsViewModel(MainViewModel main)
    {
        _main = main;
        WhisperModelsManager = new WhisperModelsViewModel(main, this);

        var settings = main.AppSettings;
        _initializing = true;
        try
        {
            OutputFolder = settings.OutputFolder;
            // REQ-CFG-08: 旧バージョンの設定（一覧なし・パスだけ）を一覧へ移行し、選択を実パスから引く。
            foreach (var entry in MigrateWhisperModelList(settings.WhisperModelList, settings.WhisperModelPath))
            {
                WhisperModels.Add(entry);
            }
            SelectedWhisperModel = FindWhisperModel(WhisperModels, settings.WhisperModelPath);
            WhisperModelPath = SelectedWhisperModel?.ModelPath ?? string.Empty;
            UseGpuForTranscription = settings.UseGpuForTranscription;
            AutoStartRecordingEnabled = settings.AutoStartRecordingEnabled;
            // REQ-TRX-10: settings.json は手編集され得るので、必ず正規化してから使う。
            // 一覧に無いコードや、ライブ側の "auto" は日本語へ倒れる。
            SelectedLiveLanguage = FindLanguage(
                TranscriptionLanguages.ForLive,
                TranscriptionLanguages.NormalizeForLive(settings.LiveTranscriptionLanguage));
        }
        finally
        {
            _initializing = false;
        }

        main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>
    /// 親の処理中フラグの変化を、このウィンドウの表示と操作の可否へ中継する（REQ-REC-09 / REQ-GPU-04）。
    /// </summary>
    /// <remarks>
    /// 設定ウィンドウは処理中には開けない（REQ-SETWIN-05）ので、開いている間にこの値は通常変わらない。
    /// それでも中継するのは、バインドした値が親と食い違う瞬間を作らないためである。
    /// </remarks>
    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsNotBusy))
        {
            return;
        }

        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanToggleGpu));
        SelectOutputFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>録音中・停止処理中・ファイル文字起こし中でないか（親の値。REQ-REC-09）。</summary>
    public bool IsNotBusy => _main.IsNotBusy;

    /// <summary>話者識別の状態（REQ-TRX-DIA-15。起動時に決まり、以後変わらない）。</summary>
    public string SpeakerDiarizationStatus => _main.SpeakerDiarizationStatus;

    /// <summary>話者識別の状態の説明（REQ-TRX-DIA-15）。</summary>
    public string SpeakerDiarizationTooltip => _main.SpeakerDiarizationTooltip;

    /// <summary>モデル管理ダイアログの ViewModel（REQ-MODELWIN-01）。</summary>
    public WhisperModelsViewModel WhisperModelsManager { get; }

    // --- 保存先フォルダ (REQ-CFG-03) ---

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    private bool CanSelectOutputFolder => _main.IsNotBusy;

    [RelayCommand(CanExecute = nameof(CanSelectOutputFolder))]
    private void SelectOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存先フォルダを選択" };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
            _main.SaveSettings();
        }
    }

    // --- 録音の自動開始 (T168 / REQ-REC-12 / REQ-CFG-10) ---

    /// <summary>「マイクの音量で録音を自動で開始する」（REQ-SETWIN-03 ⑥）。変えた時点で保存する。</summary>
    [ObservableProperty]
    private bool _autoStartRecordingEnabled;

    partial void OnAutoStartRecordingEnabledChanged(bool value)
    {
        // OFF → ON にした瞬間に、以前の積算で即発火しないよう 0 から数え直す
        _main.ResetAutoStart();
        if (!_initializing)
        {
            _main.SaveSettings();
        }
    }

    // --- 文字起こしの言語 (T153 / REQ-TRX-10) ---

    /// <summary>ライブ文字起こしの選択肢（REQ-TRX-LIVE-14）。自動判定は含まない。</summary>
    public IReadOnlyList<TranscriptionLanguage> LiveLanguageOptions { get; } = TranscriptionLanguages.ForLive;

    /// <summary>
    /// ライブ文字起こしの言語。**変更は次に録音を開始したときから効く**
    /// （<c>WhisperProcessor</c> は録音開始時に作られるため。REQ-TRX-LIVE-14）。
    /// </summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedLiveLanguage = TranscriptionLanguages.ForLive[0];

    partial void OnSelectedLiveLanguageChanged(TranscriptionLanguage value)
    {
        _main.TranscriptionService.LiveLanguage = value.Code;
        if (!_initializing)
        {
            _main.SaveSettings();
        }
    }

    /// <summary>正規化済みのコードから選択肢の実体を引く。見つからなければ先頭（日本語）。</summary>
    internal static TranscriptionLanguage FindLanguage(
        IReadOnlyList<TranscriptionLanguage> options, string code)
    {
        foreach (var option in options)
        {
            if (string.Equals(option.Code, code, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return options[0];
    }

    // --- 登録一覧とライブ用の選択 (T162 / REQ-CFG-08 / REQ-MODELWIN-07) ---

    /// <summary>
    /// 登録済み Whisper モデル（REQ-CFG-08）。このウィンドウのドロップダウン、モデル管理ダイアログ、
    /// ファイル文字起こしのダイアログが**同じインスタンス**を見る。
    /// </summary>
    public ObservableCollection<WhisperModelEntry> WhisperModels { get; } = new();

    /// <summary>
    /// ライブ文字起こしに使うモデル（REQ-MODELWIN-07）。変えると <see cref="WhisperModelPath"/> を更新して
    /// 保存し、読み込み直す。<c>null</c> は「モデル未設定」。
    /// </summary>
    [ObservableProperty]
    private WhisperModelEntry? _selectedWhisperModel;

    partial void OnSelectedWhisperModelChanged(WhisperModelEntry? value)
    {
        if (_initializing || _suppressWhisperModelSelectionWriteBack)
        {
            return;
        }

        // D1: 選択は実パスで表す。旧バージョンの WhisperModelPath と同じキーに書き続ける。
        WhisperModelPath = value?.ModelPath ?? string.Empty;
        _main.SaveSettings();
        if (value == null)
        {
            // REQ-MODELWIN-04: 別のモデルへ勝手に乗り換えず「モデル未設定」へ倒す
            _main.TranscriptionService.UnloadModel();
            _main.AudioCaptureService.SetTranscriptionService(null);
            TranscriptionStatus = "モデル未設定";
            _main.TranscribeFromFileCommand.NotifyCanExecuteChanged();
        }
        else
        {
            TryLoadWhisperModel();
        }
    }

    /// <summary>一覧の差し替えで選択を動かすとき、書き戻し（保存・再読み込み）を抑止する。</summary>
    private bool _suppressWhisperModelSelectionWriteBack;

    /// <summary>
    /// 一覧を並べ替え・差し替えする間、ライブ用の選択の書き戻し（保存・再読み込み・破棄）を抑止する。
    /// モデル管理ダイアログ（<see cref="WhisperModelsViewModel"/>）が使う。
    /// </summary>
    /// <remarks>
    /// `Move` や差し替え（Replace）の通知で、設定ウィンドウの ComboBox が選択をいったん <c>null</c> にしうるため、
    /// 抑止は変更の前から掛ける。
    /// </remarks>
    internal void ChangeWhisperModelsWithoutWriteBack(Action change)
    {
        _suppressWhisperModelSelectionWriteBack = true;
        try
        {
            change();
        }
        finally
        {
            _suppressWhisperModelSelectionWriteBack = false;
        }
    }

    /// <summary>
    /// 旧バージョンの設定（一覧が無く <c>WhisperModelPath</c> だけ）を一覧へ移行する（REQ-CFG-08）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>一覧が無く（<c>null</c> または空）パスがある → ファイル名（拡張子なし）を名前にして 1 件</item>
    /// <item>一覧はあるがパスがどの要素とも一致しない（手編集）→ 同じく 1 件を足す</item>
    /// <item>パスが空 → 何も足さない</item>
    /// </list>
    /// 足す名前が既存と重複したら <c>名前 (2)</c> のように連番を付ける（起動時に検証エラーで止まれないため。D5）。
    /// 入力の要素はそのまま返す（コピーしない）。
    /// </remarks>
    internal static List<WhisperModelEntry> MigrateWhisperModelList(
        IEnumerable<WhisperModelEntry>? list, string? selectedPath)
    {
        var result = new List<WhisperModelEntry>();
        if (list != null)
        {
            foreach (var entry in list)
            {
                if (entry != null && !string.IsNullOrWhiteSpace(entry.ModelPath))
                {
                    result.Add(entry);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(selectedPath) || result.Any(e => SamePath(e.ModelPath, selectedPath)))
        {
            return result;
        }

        var baseName = System.IO.Path.GetFileNameWithoutExtension(selectedPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = selectedPath;
        }

        var name = baseName;
        for (int n = 2; result.Any(e => string.Equals(e.ModelName, name, StringComparison.Ordinal)); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({n})");
        }

        result.Add(new WhisperModelEntry { ModelName = name, ModelPath = selectedPath });
        return result;
    }

    /// <summary>パスの同一性。同じファイルを別の表記で 2 度登録させない（REQ-CFG-08）。</summary>
    internal static bool SamePath(string a, string b)
        => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>一覧から実パスに一致する要素を引く。無ければ <c>null</c>。</summary>
    private static WhisperModelEntry? FindWhisperModel(IEnumerable<WhisperModelEntry> list, string path)
        => string.IsNullOrWhiteSpace(path) ? null : list.FirstOrDefault(e => SamePath(e.ModelPath, path));

    // --- モデル管理ダイアログ (T162 / REQ-MODELWIN-01) ---

    /// <summary>
    /// モデル管理ダイアログを開いてほしい、という要求（REQ-MODELWIN-01）。
    /// 購読するのは <c>SettingsWindow</c> のコードビハインド（モーダルの上に出すため。ADR-0006 規則 4 を ADR-0008 が引き継ぐ）。
    /// </summary>
    public event Action? WhisperModelsRequested;

    [RelayCommand]
    private void ShowWhisperModels()
    {
        WhisperModelsManager.ResetForm();
        WhisperModelsRequested?.Invoke();
    }

    // --- Whisper モデルの読み込みと GPU の使用 (REQ-TRX-01〜04 / REQ-GPU-01〜05) ---

    [ObservableProperty]
    private string _whisperModelPath = string.Empty;

    [ObservableProperty]
    private string _transcriptionStatus = "";

    [ObservableProperty]
    private bool _useGpuForTranscription = true;

    [ObservableProperty]
    private bool _gpuAvailable = true;

    public bool CanToggleGpu => _main.IsNotBusy && GpuAvailable;

    partial void OnGpuAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleGpu));
    }

    private bool _suppressUseGpuWriteBack;

    partial void OnUseGpuForTranscriptionChanged(bool value)
    {
        if (_initializing || _suppressUseGpuWriteBack)
        {
            return;
        }
        _main.SaveSettings();
        TryLoadWhisperModel();
    }

    // モデルの選択は登録一覧のドロップダウン（REQ-MODELWIN-07）で行う。
    // パスを直接選ぶ「選択」ボタンは T162 で廃止した。

    private bool _isLoadingModel;

    /// <summary>
    /// 選択中のモデルを読み込む。起動時、選択の変更、GPU の切り替え、ライブ文字起こしを ON にしたときに呼ばれる。
    /// </summary>
    internal async void TryLoadWhisperModel()
    {
        if (string.IsNullOrEmpty(WhisperModelPath))
        {
            TranscriptionStatus = "モデルパス未設定";
            _main.AudioCaptureService.SetTranscriptionService(null);
            return;
        }

        if (!System.IO.File.Exists(WhisperModelPath))
        {
            TranscriptionStatus = "モデルファイルが見つかりません";
            _main.AudioCaptureService.SetTranscriptionService(null);
            return;
        }

        if (_isLoadingModel)
        {
            return;
        }

        try
        {
            _isLoadingModel = true;
            TranscriptionStatus = "モデル読み込み中...";
            var modelPath = WhisperModelPath;
            var requestGpu = UseGpuForTranscription;
            var transcriptionService = _main.TranscriptionService;
            var (success, gpuAvailable) = await Task.Run(() => transcriptionService.LoadModel(modelPath, requestGpu));
            if (success)
            {
                GpuAvailable = gpuAvailable;
                if (!gpuAvailable && requestGpu)
                {
                    // GPUが利用不可と判明した場合は設定を強制的にOFFにする
                    _suppressUseGpuWriteBack = true;
                    try { UseGpuForTranscription = false; }
                    finally { _suppressUseGpuWriteBack = false; }
                    _main.SaveSettings();
                }

                TranscriptionStatus = "モデル読み込み完了";
                // ライブ文字起こしが ON のときのみ、録音サービスにワイヤする
                if (_main.TranscriptionEnabled)
                {
                    _main.AudioCaptureService.SetTranscriptionService(transcriptionService);
                }
            }
            else
            {
                TranscriptionStatus = "モデル読み込み失敗";
                _main.AudioCaptureService.SetTranscriptionService(null);
            }
        }
        // CA1031: async void（例外を漏らすとプロセスごと落ちる）かつ Whisper のネイティブ
        //         読み込み境界のため、全例外を画面のステータスに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            TranscriptionStatus = $"モデル読み込みエラー: {ex.Message}";
            _main.AudioCaptureService.SetTranscriptionService(null);
        }
#pragma warning restore CA1031
        finally
        {
            _isLoadingModel = false;
            _main.TranscribeFromFileCommand.NotifyCanExecuteChanged();
        }
    }
}