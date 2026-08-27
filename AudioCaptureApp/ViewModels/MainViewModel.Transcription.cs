using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、Whisper モデルの読み込み、GPU 切り替え、言語の選択、話者識別の状態表示を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D）。
public partial class MainViewModel
{
    // --- 話者識別の状態表示 (T152 / REQ-TRX-DIA-15) ---

    /// <summary>話者ダイアライゼーションが使える状態か。</summary>
    internal enum DiarizationAvailability
    {
        /// <summary>設定で無効（<c>SpeakerDiarizationEnabled = false</c>）。</summary>
        Disabled,

        /// <summary>有効だがモデルファイルが揃っていない。</summary>
        ModelMissing,

        /// <summary>有効で、モデル 2 ファイルが揃っている。</summary>
        Available
    }

    /// <summary>
    /// ステータスバーに常時出す話者識別の状態（REQ-TRX-DIA-15）。
    /// <see cref="StatusMessage"/> とは別の欄に出す。あちらは起動直後に
    /// Whisper のランタイム情報で上書きされるため、ここに書くと消えてしまう。
    /// </summary>
    [ObservableProperty]
    private string _speakerDiarizationStatus = "";

    /// <summary>話者識別の状態表示のツールチップ（REQ-TRX-DIA-15）。</summary>
    [ObservableProperty]
    private string _speakerDiarizationTooltip = "";

    /// <summary>
    /// 3 状態を決める（REQ-TRX-DIA-15）。<paramref name="modelFilesExist"/> は
    /// **存在検査の結果**であって、読み込めることの保証ではない。
    /// </summary>
    internal static DiarizationAvailability DiarizationAvailabilityFor(bool enabled, bool modelFilesExist)
    {
        if (!enabled)
        {
            return DiarizationAvailability.Disabled;
        }

        return modelFilesExist ? DiarizationAvailability.Available : DiarizationAvailability.ModelMissing;
    }

    /// <summary>
    /// 状態を利用者向けの文言にする（REQ-TRX-DIA-15）。
    /// 「有効」は<b>モデルが置いてある</b>ことまでしか言えないため、断定しすぎない語にする。
    /// </summary>
    internal static string DiarizationStatusTextFor(DiarizationAvailability availability) => availability switch
    {
        DiarizationAvailability.Available => "話者識別: 有効",
        DiarizationAvailability.ModelMissing => "話者識別: モデル未配置",
        _ => "話者識別: 無効"
    };

    /// <summary>状態表示のツールチップ。状態ごとに次の一手が分かるようにする。</summary>
    internal static string DiarizationTooltipFor(DiarizationAvailability availability) => availability switch
    {
        DiarizationAvailability.Available =>
            "ファイル文字起こしの結果に [話者N] が付きます（モデルの配置を確認した結果であり、"
            + "読み込みに成功するかは実行時に分かります）。",
        DiarizationAvailability.ModelMissing =>
            "有効に設定されていますが、モデルファイルが見つかりません。"
            + "settings.json の SpeakerSegmentationModelPath / SpeakerEmbeddingModelPath を確認してください。",
        _ => "settings.json の SpeakerDiarizationEnabled を true にすると有効になります。"
    };

    // --- 文字起こしの言語 (T153 / REQ-TRX-10) ---

    /// <summary>ライブ文字起こしの選択肢（REQ-TRX-LIVE-14）。自動判定は含まない。</summary>
    public IReadOnlyList<TranscriptionLanguage> LiveLanguageOptions { get; } = TranscriptionLanguages.ForLive;

    /// <summary>ファイル文字起こしの選択肢（REQ-TRX-FILE-16）。自動判定を含む。</summary>
    public IReadOnlyList<TranscriptionLanguage> FileLanguageOptions { get; } = TranscriptionLanguages.ForFile;

    /// <summary>
    /// ライブ文字起こしの言語。**変更は次に録音を開始したときから効く**
    /// （<c>WhisperProcessor</c> は録音開始時に作られるため。REQ-TRX-LIVE-14）。
    /// </summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedLiveLanguage = TranscriptionLanguages.ForLive[0];

    /// <summary>ファイル文字起こしの言語（REQ-TRX-FILE-16）。ライブ用とは独立。</summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedFileLanguage = TranscriptionLanguages.ForFile[0];

    partial void OnSelectedLiveLanguageChanged(TranscriptionLanguage value)
    {
        _transcriptionService.LiveLanguage = value.Code;
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    partial void OnSelectedFileLanguageChanged(TranscriptionLanguage value)
    {
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    /// <summary>正規化済みのコードから選択肢の実体を引く。見つからなければ先頭（日本語）。</summary>
    private static TranscriptionLanguage FindLanguage(
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

    // --- 文字起こし設定 ---
    [ObservableProperty]
    private bool _transcriptionEnabled;

    partial void OnTranscriptionEnabledChanged(bool value)
    {
        // このチェックボックスは「録音中のライブ文字起こし」の ON/OFF のみを司る
        // モデルのロード自体はパスが設定されていれば常に行う
        if (value)
        {
            if (_transcriptionService.IsModelLoaded)
            {
                _audioCaptureService.SetTranscriptionService(_transcriptionService);
            }
            else
            {
                TryLoadWhisperModel();
            }
        }
        else
        {
            _audioCaptureService.SetTranscriptionService(null);
        }
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    [ObservableProperty]
    private string _whisperModelPath = string.Empty;

    [ObservableProperty]
    private string _transcriptionStatus = "";

    // --- 文字起こしGPU使用設定 ---
    [ObservableProperty]
    private bool _useGpuForTranscription = true;

    [ObservableProperty]
    private bool _gpuAvailable = true;

    public bool CanToggleGpu => IsNotBusy && GpuAvailable;

    partial void OnGpuAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleGpu));
    }

    partial void OnUseGpuForTranscriptionChanged(bool value)
    {
        if (_initializing || _suppressUseGpuWriteBack)
        {
            return;
        }
        SaveSettings();
        TryLoadWhisperModel();
    }

    private bool CanSelectWhisperModel => !IsRecording && !IsStopping && !IsTranscribingFile;

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

    private bool _isLoadingModel;

    private async void TryLoadWhisperModel()
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
            var (success, gpuAvailable) = await Task.Run(() => _transcriptionService.LoadModel(modelPath, requestGpu));
            if (success)
            {
                GpuAvailable = gpuAvailable;
                if (!gpuAvailable && requestGpu)
                {
                    // GPUが利用不可と判明した場合は設定を強制的にOFFにする
                    _suppressUseGpuWriteBack = true;
                    try { UseGpuForTranscription = false; }
                    finally { _suppressUseGpuWriteBack = false; }
                    SaveSettings();
                }

                TranscriptionStatus = "モデル読み込み完了";
                // ライブ文字起こしが ON のときのみ、録音サービスにワイヤする
                if (TranscriptionEnabled)
                {
                    _audioCaptureService.SetTranscriptionService(_transcriptionService);
                }
            }
            else
            {
                TranscriptionStatus = "モデル読み込み失敗";
                _audioCaptureService.SetTranscriptionService(null);
            }
        }
        // CA1031: async void（例外を漏らすとプロセスごと落ちる）かつ Whisper のネイティブ
        //         読み込み境界のため、全例外を画面のステータスに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            TranscriptionStatus = $"モデル読み込みエラー: {ex.Message}";
            _audioCaptureService.SetTranscriptionService(null);
        }
#pragma warning restore CA1031
        finally
        {
            _isLoadingModel = false;
            TranscribeFromFileCommand.NotifyCanExecuteChanged();
        }
    }
}