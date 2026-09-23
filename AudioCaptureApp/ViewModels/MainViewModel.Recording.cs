using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
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

    // 「自動録音中」は 5 文字で、「停止処理中」と同じ幅に収まる（NFR-09 / REQ-REC-07）
    public string RecordingStatusText => IsStopping ? "停止処理中"
        : IsRecording ? (IsAutoStartedRecording ? "自動録音中" : "録音中")
        : "停止中";
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
            // 手動で始めた録音。自動開始のときは呼び出し元（ObserveAutoStart）が直後に true へ上書きする
            IsAutoStartedRecording = false;
            TranscriptionLagWarning = "";
            _lastStoppingStatusAt = DateTime.Now;
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
    /// ライブ文字起こしでまだ処理していない音声の長さ（秒）。停止処理中の残り表示と診断に使う。
    /// </summary>
    public double TranscriptionPendingSeconds => _transcriptionService.PendingSeconds;

    /// <summary>
    /// 録音中に出す遅れの警告（REQ-REC-07）。遅れが小さいうちは空文字列で、表示しない。
    /// </summary>
    [ObservableProperty]
    private string _transcriptionLagWarning = "";

    /// <summary>この秒数以上の遅れになったら警告を出す（REQ-REC-07）。</summary>
    internal const double LagWarningSeconds = 60.0;

    /// <summary>停止のときに「待つか打ち切るか」を確認する閾値（REQ-TRX-LIVE-11）。</summary>
    internal const double StopConfirmSeconds = 60.0;

    /// <summary>録音中の遅れの警告文（REQ-REC-07）。閾値未満なら空文字列。</summary>
    internal static string TranscriptionLagWarningFor(double pendingSeconds)
    {
        if (pendingSeconds < LagWarningSeconds)
        {
            return "";
        }

        return pendingSeconds < 120
            ? $"文字起こしが {Math.Floor(pendingSeconds)} 秒遅れています"
            : $"文字起こしが {Math.Floor(pendingSeconds / 60)} 分遅れています";
    }

    /// <summary>
    /// 停止の前に見せる確認文言（REQ-TRX-LIVE-11）。残りが小さければ <c>null</c>（確認せず待つ）。
    /// </summary>
    internal static string? StopConfirmationMessage(double pendingSeconds)
        => pendingSeconds < StopConfirmSeconds
            ? null
            : $"文字起こしが残り {Math.Ceiling(pendingSeconds):F0} 秒分あります。\n" +
              "すべて処理してから停止しますか？\n\n" +
              "「いいえ」を選ぶと、残りを文字起こしせずに停止します（音声ファイルは残ります）。";

    /// <summary>
    /// 停止の前に確認したいことを View へ伝える（REQ-TRX-LIVE-11）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド（ADR-0002 の規則 2・3）。
    /// **戻り値が <c>false</c> なら「待たずに打ち切る」**。購読が無ければ待つ。
    /// </summary>
    public event Func<string, bool>? StopConfirmationRequested;

    /// <summary>停止処理の「打ち切り」を要求済みか（REQ-TRX-LIVE-11）。ボタンを無効化するのに使う。</summary>
    [ObservableProperty]
    private bool _isStopAbortRequested;

    /// <summary>「打ち切り」を押す前の確認文言（REQ-REC-07）。</summary>
    internal static string AbortStopConfirmationMessage(double pendingSeconds)
        => $"文字起こしの残り {Math.Ceiling(pendingSeconds):F0} 秒分を捨てて停止しますか？\n捨てた分は文字起こしファイルに残りません。";

    /// <summary>
    /// 停止処理の「打ち切り」（REQ-TRX-LIVE-11）。滞留している文字起こしを捨てて停止を急がせる。
    /// 停止処理中でなければ何もしない。確認は View（<c>MainWindow</c>）が済ませてから呼ぶ。
    /// </summary>
    public void AbortStop()
    {
        if (!IsStopping || IsStopAbortRequested)
        {
            return;
        }

        IsStopAbortRequested = true;
        _transcriptionService.RequestAbort();
        StatusMessage = "文字起こしを打ち切って停止しています...";
    }

    /// <summary>停止処理中の残り表示（REQ-REC-07）。1 秒ごとにメーターのタイマーから呼ばれる。</summary>
    internal static string StoppingStatusFor(double pendingSeconds)
        => pendingSeconds >= 0.5
            ? $"停止処理中... 文字起こしの残り {Math.Ceiling(pendingSeconds):F0} 秒分"
            : "停止処理中...";

    private DateTime _lastStoppingStatusAt;

    /// <summary>
    /// メーターの 50ms タイマーに相乗りして、1 秒ごとに
    /// 停止処理中の残り（REQ-TRX-LIVE-11）と録音中の遅れ（REQ-REC-07）を更新する。
    /// </summary>
    private void UpdateStoppingStatus()
    {
        if (!IsRecording && !IsStopping)
        {
            return;
        }

        var now = DateTime.Now;
        if (now - _lastStoppingStatusAt < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastStoppingStatusAt = now;
        var pending = TranscriptionPendingSeconds;

        if (IsStopping)
        {
            TranscriptionLagWarning = "";
            if (!IsStopAbortRequested)
            {
                StatusMessage = StoppingStatusFor(pending);
            }
            return;
        }

        // 録音中。遅れに気づけないまま続けると、停止時に長く待つことになる
        TranscriptionLagWarning = TranscriptionLagWarningFor(pending);
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
        // REQ-TRX-LIVE-11: 残りが多いなら、待つか打ち切るかを先に確認する。
        // 停止処理に入る前に聞く — 入ってからでは操作が塞がるため。
        var pendingAtStop = TranscriptionPendingSeconds;
        var abortFromStart = StopConfirmationMessage(pendingAtStop) is { } confirmation
            && StopConfirmationRequested?.Invoke(confirmation) == false;

        _clockTimer.Stop();
        IsStopping = true;
        IsStopAbortRequested = false;
        TranscriptionLagWarning = "";
        _lastStoppingStatusAt = DateTime.Now;
        // REQ-LVL-04: スピーカーも常時モニタのため、ここでレベルをリセットしない
        // 残り秒数は UpdateStoppingStatus が 1 秒ごとに上書きする（REQ-REC-07 / REQ-TRX-LIVE-11）
        StatusMessage = "停止処理中...";

        if (abortFromStart)
        {
            // 待たない選択。停止処理に入る前に打ち切りを要求しておく
            IsStopAbortRequested = true;
            _transcriptionService.RequestAbort();
            StatusMessage = "文字起こしの残りを打ち切って停止しています...";
        }

        var transcriptionEnabled = TranscriptionEnabled;
        // 停止中の Error イベント（打ち切りのタイムアウト等）は BeginInvoke で書かれた直後にこの継続が
        // 上書きしてしまう（T134 と同じ順序）。控えておいて完了の 1 行に併記する。
        _lastTranscriptionError = null;
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
        IsStopAbortRequested = false;
        IsAutoStartedRecording = false;
        // REQ-REC-12: 止めた直後に会話が続いていても、クールダウンの間は自動で再開しない
        _autoStartTrigger.NotifyStopped();

        var session = _audioCaptureService.CurrentSession;
        if (session != null)
        {
            var txtPath = System.IO.Path.ChangeExtension(session.FilePath, ".txt");
            var hasTxt = transcriptionEnabled && System.IO.File.Exists(txtPath);
            StatusMessage = hasTxt
                ? $"保存完了: {session.FilePath} (文字起こし: {txtPath})"
                : $"保存完了: {session.FilePath}";
            if (_lastTranscriptionError is { } stopError)
            {
                StatusMessage += $" — {stopError}";
            }
            // REQ-OPEN-01: 文字起こしがあれば .txt、無ければ録音した .mp3 を対象にする
            LastResultPath = hasTxt ? txtPath : session.FilePath;

            // REQ-REC-13: 停止処理が完了してからメタデータの入力を促す。
            // MainWindow が同期的にダイアログを出し、閉じたら CompleteRecordingMetadata を呼ぶ。
            // 終了確認（REQ-REC-11）の経路でも、この戻りを待ってから Close() に進む。
            RequestRecordingMetadata(session);
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
            IsStopAbortRequested = false;
            IsAutoStartedRecording = false;
            _autoStartTrigger.NotifyStopped();
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