using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、録音の自動開始（マイク音量の監視）を担当する部分。
// MainViewModel はファイルを機能単位で partial に割っている（ADR-0008 が引き継ぐ ADR-0005 規則 2）。
public partial class MainViewModel
{
    // --- 録音の自動開始 (T168 / REQ-REC-12 / REQ-CFG-10) ---

    /// <summary>
    /// 判定器。閾値・継続時間・クールダウンは起動時の設定で固定する（UI からは変更できない）。
    /// 生成はコンストラクター（<c>MainViewModel.cs</c>）で設定を読んだ直後に行う。
    /// </summary>
    private readonly AutoStartTrigger _autoStartTrigger;

    /// <summary>レベルメーターのタイマー間隔。判定器に渡す経過時間として使う。</summary>
    private static readonly TimeSpan MeterTick = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 判定の積算を 0 に戻す。「マイクの音量で録音を自動で開始する」（REQ-SETWIN-03 ⑥）の ON/OFF は
    /// 設定ウィンドウの ViewModel（<see cref="SettingsViewModel.AutoStartRecordingEnabled"/>）が持ち、
    /// 変わるたびにここを呼ぶ — OFF → ON にした瞬間に、以前の積算で即発火しないよう 0 から数え直すため。
    /// </summary>
    internal void ResetAutoStart() => _autoStartTrigger.Reset();

    /// <summary>
    /// いまの録音が自動開始によるものか（REQ-REC-12）。録音状態の文言を「自動録音中」にする。
    /// 録音が止まったら false に戻す。
    /// </summary>
    [ObservableProperty]
    private bool _isAutoStartedRecording;

    partial void OnIsAutoStartedRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
    }

    /// <summary>
    /// モーダルダイアログ（設定・ファイル文字起こし・終了確認）が開いているか。
    /// View が <c>ShowDialog</c> の前後で立て下げする（View → ViewModel の向き）。
    /// 開いている間は自動開始しない — 設定ウィンドウは録音中には開けない前提（REQ-SETWIN-05）であり、
    /// ダイアログの背後で録音が始まると操作の前提が崩れるためである。
    /// </summary>
    public bool IsModalDialogOpen { get; set; }

    /// <summary>自動開始してよい状態か（REQ-REC-12 の「発火させない条件」の①②③）。</summary>
    internal static bool CanAutoStartFor(
        bool enabled, bool isNotBusy, bool hasMicrophone, bool isModalDialogOpen)
        => enabled && isNotBusy && hasMicrophone && !isModalDialogOpen;

    /// <summary>
    /// レベルメーターの更新に相乗りして判定する（REQ-REC-12）。UI スレッドの 50ms タイマーから呼ばれる。
    /// </summary>
    private void ObserveAutoStart(double micLevelDb)
    {
        var canStart = CanAutoStartFor(
            Settings.AutoStartRecordingEnabled, IsNotBusy, SelectedCaptureDevice != null, IsModalDialogOpen);
        if (!_autoStartTrigger.Observe(micLevelDb, MeterTick, canStart))
        {
            return;
        }

        // 手動と同じ処理で始める。失敗（REQ-REC-01 / 10）は StartRecording がステータスに出す。
        StartRecording();
        if (IsRecording)
        {
            IsAutoStartedRecording = true;
            StatusMessage = "音声を検知して録音を自動で開始しました";
        }
        else
        {
            _autoStartTrigger.Reset();
        }
    }
}