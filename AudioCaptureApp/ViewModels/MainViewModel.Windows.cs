using AudioCaptureApp.Models;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、メインウィンドウから補助ウィンドウを開く入口を担当する部分。
// 補助ウィンドウの状態は各ウィンドウの ViewModel（子）が持ち、ここは子に対象を渡してから表示を要求するだけ（ADR-0008）。
// ウィンドウの生成は View 層（MainWindow のコードビハインド）が行う。ViewModel はイベントで要求を上げる（ADR-0002 規則 2・3）。
public partial class MainViewModel
{
    // --- 設定ウィンドウ (REQ-SETWIN-02 / 05) ---

    /// <summary>
    /// 設定ウィンドウを開いてほしい、という要求（REQ-SETWIN-02）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド。
    /// </summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 「設定…」の可否（REQ-SETWIN-05）。録音中・停止処理中・ファイル文字起こし中は無効にする。
    /// 設定ウィンドウの項目はいずれも REQ-REC-09 でその間は操作できず、開いても何もできない。
    /// 一方でモーダル（REQ-SETWIN-02）なので、開いている間は**録音の停止操作が塞がれる**。
    /// 得るものが無く塞ぐものがある以上、開かせない。
    /// </summary>
    private bool CanShowSettings => IsNotBusy;

    [RelayCommand(CanExecute = nameof(CanShowSettings))]
    private void ShowSettings() => SettingsRequested?.Invoke();

    // --- 文字起こし表示ウィンドウ (T114 / REQ-LIVEVIEW-01) ---

    /// <summary>
    /// 文字起こし表示ウィンドウを開いてほしい、という要求（REQ-LIVEVIEW-01）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド。
    /// </summary>
    public event Action? LiveTranscriptRequested;

    [RelayCommand]
    private void ShowLiveTranscript() => LiveTranscriptRequested?.Invoke();

    // --- ファイル文字起こしのオプション指定ダイアログ (T113 / REQ-TRX-FILE-01 / 02 / 09) ---

    /// <summary>
    /// オプション指定ダイアログを開いてほしい、という要求（REQ-TRX-FILE-09）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド。表示の前に <see cref="FileTranscriptionViewModel.Prepare"/> で対象を渡してある。
    /// </summary>
    public event Action? FileTranscriptionRequested;

    // REQ-TRX-FILE-01 / 17: 可否は「登録済みモデルが 1 つ以上ある」で決める。実際の読み込みは「開始」で行う。
    private bool CanTranscribeFromFile =>
        !IsRecording && !IsStopping && !IsTranscribingFile
        && Settings.WhisperModels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanTranscribeFromFile))]
    private void TranscribeFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "文字起こしする音声ファイルを選択",
            Filter = "音声ファイル (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        RequestFileTranscription(dialog.FileName);
    }

    public bool CanAcceptFileDrop => CanTranscribeFromFile;

    public void TranscribeDroppedFile(string filePath)
    {
        if (!CanTranscribeFromFile)
        {
            return;
        }

        if (!FileTranscriptionViewModel.IsSupportedAudioExtension(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath);
            StatusMessage = $"エラー: 対応していないファイル形式です ({ext})";
            return;
        }

        RequestFileTranscription(filePath);
    }

    /// <summary>
    /// 対象ファイルを子へ渡し、オプション指定ダイアログの表示を要求する（REQ-TRX-FILE-09）。
    /// ここでは処理を始めない。始めるのはダイアログの「開始」（<see cref="FileTranscriptionViewModel.StartFileTranscriptionAsync"/>）。
    /// </summary>
    private void RequestFileTranscription(string filePath)
    {
        FileTranscription.Prepare(filePath);
        FileTranscriptionRequested?.Invoke();
    }

    // --- 録音停止後のメタデータ入力ダイアログ (T169 / REQ-REC-13) ---

    /// <summary>
    /// メタデータ入力ダイアログを開いてほしい、という要求（REQ-REC-13）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド。
    /// **同期的に `ShowDialog` し、閉じたら <see cref="RecordingMetadataViewModel.Complete"/> を呼ぶ**こと —
    /// 停止処理の続き（`LastResultPath` の確定・終了確認の `Close()`）がその戻りを待っている。
    /// </summary>
    public event Action? RecordingMetadataRequested;

    /// <summary>
    /// 停止処理の完了後、録音データがあれば入力を促す（REQ-REC-13）。子に既定値を入れてから表示を要求する。
    /// </summary>
    private void RequestRecordingMetadata(RecordingSession session)
    {
        RecordingMetadata.Prepare(session);
        RecordingMetadataRequested?.Invoke();
    }
}