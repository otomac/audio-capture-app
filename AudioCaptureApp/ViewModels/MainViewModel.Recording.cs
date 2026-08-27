using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、録音の開始／停止、録音状態の表示、終了時の確認と後始末を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D）。
public partial class MainViewModel
{
    private static readonly SolidColorBrush RecordingBrush = new(Color.FromRgb(0xCC, 0x00, 0x00));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(0x26, 0x30, 0x3F));

    static MainViewModel()
    {
        RecordingBrush.Freeze();
        StoppedBrush.Freeze();
    }

    public string RecordingStatusText => IsStopping ? "停止処理中" : IsRecording ? "録音中" : "停止中";
    public SolidColorBrush RecordingStatusColor => IsRecording ? RecordingBrush : StoppedBrush;

    // --- コマンド ---

    private bool CanStartRecording =>
        (SelectedCaptureDevice != null || SelectedRenderDevice != null)
        && !IsRecording && !IsStopping && !IsTranscribingFile;

    private bool CanStopRecording => IsRecording && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        try
        {
            _recordingStartTime = _audioCaptureService.StartRecording(SelectedCaptureDevice, SelectedRenderDevice, OutputFolder);

            // REQ-LIVEVIEW-08: 前のセッションの行が新しいセッションの行に混ざらないようにする。
            // 開始に成功したあとで消すこと。失敗したのに消すと、失敗の前後を見比べられなくなる。
            // 引き取り待ちのキュー（REQ-LIVEVIEW-09）も一緒に空にする。消し忘れると
            // 前のセッションの行がクリアの直後に画面へ現れる。
            _pendingTranscriptLines.Clear();
            LiveTranscriptLines.Clear();

            IsRecording = true;
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

    /// <summary>
    /// 実行中の停止処理（REQ-REC-11）。終了確認の「はい」で完了を待つために保持する。
    /// </summary>
    private Task? _stopRecordingTask;

    /// <summary>
    /// 実行中のファイル文字起こし（REQ-TRX-FILE-14）。同じく完了を待つために保持する。
    /// </summary>
    private Task? _fileTranscriptionTask;

    // 停止処理そのものは Core 側にある。ここを薄いラッパーにしているのは、
    // 走っている Task を掴んでおかないと終了確認（REQ-REC-11）が完了を待てないためである。
    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private Task StopRecordingAsync()
    {
        _stopRecordingTask = StopRecordingCoreAsync();
        return _stopRecordingTask;
    }

    private async Task StopRecordingCoreAsync()
    {
        _clockTimer.Stop();
        IsStopping = true;
        // REQ-LVL-04: スピーカーも常時モニタのため、ここでレベルをリセットしない
        StatusMessage = "停止処理中...";

        var transcriptionEnabled = TranscriptionEnabled;
        try
        {
            await Task.Run(() => _audioCaptureService.StopRecording());
        }
        // CA1031: 停止処理は録音・文字起こし・ネイティブリソース解放をまたぐ。
        //         ここで例外を漏らすと AsyncRelayCommand が Dispatcher に再スローし、
        //         未処理例外としてプロセスごと終了する（T117）。画面表示に変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            StatusMessage = $"停止処理でエラーが発生しました: {ex.Message}";
        }
#pragma warning restore CA1031

        IsRecording = false;
        IsStopping = false;

        var session = _audioCaptureService.CurrentSession;
        if (session != null)
        {
            var txtPath = System.IO.Path.ChangeExtension(session.FilePath, ".txt");
            var hasTxt = transcriptionEnabled && System.IO.File.Exists(txtPath);
            StatusMessage = hasTxt
                ? $"保存完了: {session.FilePath} (文字起こし: {txtPath})"
                : $"保存完了: {session.FilePath}";
            // REQ-OPEN-01: 文字起こしがあれば .txt、無ければ録音した .mp3 を対象にする
            LastResultPath = hasTxt ? txtPath : session.FilePath;
        }
        else
        {
            StatusMessage = "録音データなし（ファイルは作成されませんでした）";
        }
    }

    private void OnRecordingError(string message)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _clockTimer.Stop();
            IsRecording = false;
            IsStopping = false;
            StatusMessage = $"エラー: {message}";
        });
    }

    // --- 終了時の確認と後始末 (T149) ---

    /// <summary>
    /// ウィンドウを閉じる前に見せる確認文言（REQ-REC-11 / REQ-TRX-FILE-14）。
    /// 進行中の作業が無ければ <c>null</c> を返し、確認せずに閉じてよいことを表す。
    /// </summary>
    /// <remarks>
    /// 停止処理中は <see cref="IsRecording"/> も <c>true</c> のままなので、
    /// **停止処理中の判定を先に置く**こと。逆にすると停止を待つ場面で
    /// 「録音を停止しますか」と聞くことになる。
    /// 録音とファイル文字起こしは排他（互いの <c>CanExecute</c> が相手を除外する）なので、
    /// どちらか一方しか成り立たない。
    /// </remarks>
    internal static string? CloseConfirmationMessage(
        bool isRecording, bool isStopping, bool isTranscribingFile)
    {
        if (isStopping)
        {
            return "録音の停止処理中です。完了を待って終了しますか？";
        }
        if (isRecording)
        {
            return "録音中ですが終了しますか？\n録音を停止し、ファイルを保存してから終了します。";
        }
        if (isTranscribingFile)
        {
            return "文字起こし中ですが中止して終了しますか？\n作成中の出力ファイルは削除されます。";
        }
        return null;
    }

    /// <summary>
    /// 進行中の作業を畳んでから戻る（REQ-REC-11 / REQ-TRX-FILE-14）。
    /// 呼び出し元（<c>MainWindow.MainWindow_Closing</c>）はこれを待ってから <c>Close()</c> を呼び直す。
    /// </summary>
    /// <remarks>
    /// 停止・中止のいずれも内部で全例外を <see cref="StatusMessage"/> へ変換するため、
    /// ここから例外は出ない（決定 D7: 失敗しても終了は続行する）。
    /// </remarks>
    public async Task ShutdownAsync()
    {
        if (IsTranscribingFile)
        {
            CancelFileTranscription();
            if (_fileTranscriptionTask is { } fileTask)
            {
                await fileTask;
            }
        }

        if (IsStopping)
        {
            // 既に停止処理が走っている。二重に止めず、走っているものの完了を待つ。
            if (_stopRecordingTask is { } stopTask)
            {
                await stopTask;
            }
        }
        else if (IsRecording)
        {
            await StopRecordingAsync();
        }
    }
}