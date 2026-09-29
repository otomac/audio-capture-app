using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、話者識別の状態表示とライブ文字起こしの ON/OFF を担当する部分。
// モデルの読み込み・GPU の切り替え・言語の選択は設定ウィンドウの ViewModel（SettingsViewModel）が持つ（ADR-0008）。
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

    /// <summary>
    /// 起動時に判定した話者識別の状態（REQ-TRX-DIA-15）。以後変わらない。
    /// ダイアログの「話者識別を行う」を操作できるかの根拠にも使う（REQ-TRX-DIA-16）。
    /// </summary>
    private readonly DiarizationAvailability _diarizationAvailability;

    /// <summary>
    /// ダイアログで「話者識別を行う」を操作できるか（REQ-TRX-DIA-16）。
    /// **①有効のときだけ** true。②モデル未配置・③無効は OFF 固定にする。
    /// </summary>
    internal static bool IsDiarizationSelectable(DiarizationAvailability availability)
        => availability == DiarizationAvailability.Available;

    /// <summary>「話者識別を行う」の操作可否（REQ-TRX-DIA-16）。起動後は変わらない。</summary>
    public bool CanChooseFileDiarization => IsDiarizationSelectable(_diarizationAvailability);

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
                Settings.TryLoadWhisperModel();
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
}