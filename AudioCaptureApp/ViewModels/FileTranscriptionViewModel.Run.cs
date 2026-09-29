using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// FileTranscriptionViewModel のうち、処理の進行（開始・進捗・中止・完了時のメタデータ書き出し）を担当する部分。
// 本体（FileTranscriptionViewModel.cs）が 500 行を超えるため、機能単位で partial に割っている（ADR-0008）。
public sealed partial class FileTranscriptionViewModel
{
    [ObservableProperty]
    private string _fileTranscriptionStatus = "";

    private CancellationTokenSource? _fileTranscriptionCts;

    /// <summary>
    /// 実行中のファイル文字起こし（REQ-TRX-FILE-14）。終了確認の「はい」で完了を待つために保持する。
    /// </summary>
    private Task<bool>? _fileTranscriptionTask;

    // --- 進捗と中止 (REQ-TRX-FILE-06 / 07) ---

    /// <summary>進捗の百分率（0〜100）。ダイアログの <c>ProgressBar</c> 用。</summary>
    [ObservableProperty]
    private double _fileTranscriptionProgress;

    /// <summary>「中止」を要求済みか（REQ-TRX-FILE-07）。</summary>
    /// <remarks>
    /// 中止が実際に効くのは推論の境界だけであり（REQ-TRX-DIA-12）、押してから止まるまで
    /// 話者ダイアライゼーション有効時は数十秒〜数分かかる。その間も進捗は進み続けるため、
    /// 「押した」ことをこのフラグに残して「中止」の無効化と注記の表示に使う。
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelFileTranscriptionCommand))]
    private bool _isFileTranscriptionCancelRequested;

    /// <summary>
    /// 「中止」を押したあとにダイアログへ出す注記（REQ-TRX-FILE-07）。
    /// </summary>
    /// <remarks>
    /// **押した時点のフェーズで決まる**ため、値は実行中に変わりうる。押した後に書き換えないのは、
    /// 話者識別中に押せばその完了直後の境界で中止が効き、Whisper のフェーズへ進まないためである。
    /// </remarks>
    [ObservableProperty]
    private string _fileTranscriptionCancelNotice = "";

    /// <summary>
    /// いま話者識別のフェーズにいるか。中止の注記をどちらにするかだけに使う。
    /// </summary>
    /// <remarks>
    /// 書くのは進捗のハンドラー、読むのは <c>CancelFileTranscription</c> で、どちらも
    /// UI スレッドで走る（<see cref="Progress{T}"/> は UI スレッドで生成している）。
    /// したがって同期は要らない。
    /// </remarks>
    private bool _isDiarizingFile;

    /// <summary>
    /// 進捗のフェーズ名（REQ-TRX-FILE-06）が話者識別かどうか。
    /// </summary>
    /// <remarks>
    /// 表示名の写しを ViewModel 側に持たない。持つと、フェーズ名を変えたときに
    /// 一致しなくなったことに誰も気づけない。
    /// </remarks>
    internal static bool IsDiarizationPhase(string phase) =>
        string.Equals(phase, TranscriptionService.DiarizePhase, StringComparison.Ordinal);

    /// <summary>中止を要求したときの注記の文言（REQ-TRX-FILE-07）。</summary>
    /// <param name="waitingForDiarization">押した時点で話者識別のフェーズにいたか。</param>
    /// <remarks>
    /// 待たされる理由も長さもフェーズで違うため、文言を分ける。話者識別は推論の境界でしか
    /// 止まらず数十秒〜数分かかるが（REQ-TRX-DIA-12）、Whisper はチャンクと有声区間の
    /// 境目ごとにキャンセルを見るので数秒で止まる。**「話者識別が有効か」で決めてはならない** —
    /// 有効時も Whisper のフェーズを通るためである（T160）。
    /// </remarks>
    internal static string FileTranscriptionCancelNoticeFor(bool waitingForDiarization) =>
        waitingForDiarization
            ? "中止を要求しました（話者識別が終わるまでお待ちください）"
            : "中止を要求しました（処理の切れ目までお待ちください）";

    /// <summary>進捗を百分率（0〜100）に直す。総時間が 0 なら 0。</summary>
    internal static double FileTranscriptionProgressFor(TimeSpan processed, TimeSpan total)
        => total <= TimeSpan.Zero
            ? 0.0
            : Math.Clamp(processed / total * 100.0, 0.0, 100.0);

    /// <summary>
    /// ダイアログの「開始」から呼ばれる。開始時刻を解析して本処理へ渡す。
    /// </summary>
    /// <returns>
    /// 処理を始めた（完了・失敗・中止のいずれかで終わった）なら <c>true</c>。モデルを読み込めず
    /// 始めなかったなら <c>false</c> — 呼び出し元はダイアログを閉じない（REQ-TRX-FILE-17）。
    /// </returns>
    public Task<bool> StartFileTranscriptionAsync()
    {
        if (!TryParseStartTime(FileTranscriptionStartTime, out var startOffset))
        {
            return Task.FromResult(true);
        }

        // 走っている Task を掴んでおく。終了確認（REQ-TRX-FILE-14）で
        // 中止処理の完了を待つために要る。
        var task = RunFileTranscriptionAsync(_pendingTranscriptionFilePath, startOffset);
        _fileTranscriptionTask = task;
        return task;
    }

    private async Task<bool> RunFileTranscriptionAsync(string filePath, TimeSpan startOffset)
    {
        _fileTranscriptionCts = new CancellationTokenSource();
        SetTranscribing(true);
        IsFileTranscriptionCancelRequested = false;
        FileTranscriptionCancelNotice = "";
        FileTranscriptionModelError = "";
        _main.LastTranscriptionError = null;
        // 最初の進捗が届くまでは話者識別のフェーズではない（走るのはデコードで、ct で素早く止まる）
        _isDiarizingFile = false;
        FileTranscriptionStatus = "準備中...";
        FileTranscriptionProgress = 0;
        _main.StatusMessage = "音声ファイルから文字起こし中...";
        try
        {
            // 話者ダイアライゼーションが有効だと「話者識別中」→「処理中」の 2 フェーズになる。
            // フェーズ名を出さないと、進捗バーが 2 度 0% に戻る理由が分からない（REQ-TRX-FILE-06）。
            var progress = new Progress<Services.FileTranscriptionProgress>(v =>
            {
                // 中止の注記をどちらにするかは、押した時点のフェーズで決まる（REQ-TRX-FILE-07）
                _isDiarizingFile = IsDiarizationPhase(v.Phase);
                // モデル読み込み中（REQ-TRX-FILE-17）は総時間が無いのでフェーズ名だけ出す
                FileTranscriptionStatus = v.Total <= TimeSpan.Zero
                    ? $"{v.Phase}..."
                    : $"{v.Phase}: {v.Processed:hh\\:mm\\:ss} / {v.Total:hh\\:mm\\:ss}";
                FileTranscriptionProgress = FileTranscriptionProgressFor(v.Processed, v.Total);
            });
            var token = _fileTranscriptionCts.Token;
            // ファイル I/O とリサンプル処理でUIスレッドをブロックしないようワーカーへ
            // REQ-TRX-FILE-16 / 17 / REQ-TRX-DIA-17: 「開始」を押した時点の選択を使う
            var options = new FileTranscriptionOptions(
                startOffset,
                SelectedFileLanguage.Code,
                SelectedSpeakerCount.Count,
                SelectedFileWhisperModel?.ModelPath,
                _main.Settings.UseGpuForTranscription,
                MetadataMeetingName);
            // REQ-TRX-DIA-16: OFF なら無効時と同じ null を渡す。サービス側に切り替えの分岐は無い（REQ-TRX-DIA-03）。
            var diarization = FileDiarizationEnabled ? _main.SpeakerDiarizationService : null;
            var transcriptionService = _main.TranscriptionService;
            var result = await Task.Run(() => transcriptionService.TranscribeFileAsync(
                filePath, options, diarization, progress, token));
            if (result.Success)
            {
                var txtPath = TranscriptionService.BuildTranscriptPath(filePath, MetadataMeetingName);
                FileTranscriptionStatus = "完了";
                _main.StatusMessage = $"文字起こし完了: {txtPath}";
                _main.LastResultPath = txtPath;   // REQ-OPEN-01
                WriteFileTranscriptionMetadata(txtPath);
            }
            else if (result.Outcome == FileTranscriptionOutcome.ModelLoadFailed)
            {
                // REQ-TRX-FILE-17: 処理を始めていない。理由をダイアログ内に出し、閉じない。
                FileTranscriptionStatus = "";
                FileTranscriptionModelError = result.Message ?? "モデルを読み込めませんでした";
                _main.StatusMessage = $"文字起こしを開始できません: {FileTranscriptionModelError}";
                return false;
            }
            else
            {
                FileTranscriptionStatus = "失敗";
                // T134: Error イベントが BeginInvoke で書いたステータスは、この継続が上書きしてしまう
                // （同じ Dispatcher・同じ優先度の FIFO）。理由を併記して手がかりを残す（REQ-TRX-FILE-12）。
                _main.StatusMessage = FileTranscriptionFailureMessageFor(_main.LastTranscriptionError);
            }
        }
        catch (OperationCanceledException)
        {
            FileTranscriptionStatus = "中止しました";
            _main.StatusMessage = "文字起こしを中止しました";
        }
        // CA1031: UI コマンド境界。ファイル文字起こしの任意の失敗を画面のステータスに変換し、
        //         アプリを落とさずに次の操作へ戻す。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            FileTranscriptionStatus = $"エラー: {ex.Message}";
            _main.StatusMessage = $"エラー: {ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _fileTranscriptionCts?.Dispose();
            _fileTranscriptionCts = null;
            SetTranscribing(false);
        }

        return true;
    }

    /// <summary>失敗の 1 行（REQ-TRX-FILE-12）。理由が届いていれば併記する。</summary>
    internal static string FileTranscriptionFailureMessageFor(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "文字起こしに失敗しました"
            : $"文字起こしに失敗しました: {reason}";

    /// <summary>
    /// ファイル文字起こしの完了時にメタデータ JSON を書く（REQ-TRX-FILE-18 / 19 / REQ-META-01）。
    /// 書き出し先が既にあれば 3 項目のキーだけを更新し、無ければ 3 項目のいずれかが入力されているときだけ作る。
    /// 失敗はステータスに出すだけ。
    /// </summary>
    private void WriteFileTranscriptionMetadata(string transcriptPath)
    {
        var jsonPath = RecordingMetadataFile.BuildMetadataPath(transcriptPath);
        var exists = System.IO.File.Exists(jsonPath);
        if (!exists && RecordingMetadataViewModel.IsMetadataEmpty(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText))
        {
            return;
        }

        try
        {
            var metadata = RecordingMetadataViewModel.BuildMetadata(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText,
                _main.AppSettings.ParticipantDomainSortedLast);
            if (exists)
            {
                RecordingMetadataFile.Update(jsonPath, metadata);
            }
            else
            {
                RecordingMetadataFile.Write(jsonPath, metadata);
            }

            var verb = exists ? "メタデータを更新" : "メタデータ";
            _main.StatusMessage = $"文字起こし完了: {transcriptPath} ({verb}: {System.IO.Path.GetFileName(jsonPath)})";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _main.StatusMessage = $"文字起こしは完了しましたが、メタデータの保存に失敗しました: {ex.Message}";
        }
    }

    // 2 度押しても意味が無い（REQ-TRX-DIA-12 により中止の判定は推論の境界でしか起きない）。
    // 押せたことを見せるために、要求した時点で無効化する（REQ-TRX-FILE-07）。
    private bool CanCancelFileTranscription => IsTranscribingFile && !IsFileTranscriptionCancelRequested;

    [RelayCommand(CanExecute = nameof(CanCancelFileTranscription))]
    private void CancelFileTranscription()
    {
        // 押したことは FileTranscriptionStatus ではなくフラグに残す。進捗行へ書いても
        // 直後の progress.Report が上書きしてしまい、画面上は何も起きていないように見える。
        FileTranscriptionCancelNotice = FileTranscriptionCancelNoticeFor(_isDiarizingFile);
        IsFileTranscriptionCancelRequested = true;
        _fileTranscriptionCts?.Cancel();
    }

    /// <summary>
    /// 中止を要求し、処理の完了を待つ（REQ-TRX-FILE-14）。アプリの終了時に親が呼ぶ。
    /// </summary>
    /// <remarks>
    /// 処理は内部で全例外を <see cref="MainViewModel.StatusMessage"/> へ変換するため、ここから例外は出ない。
    /// </remarks>
    internal async Task CancelAndWaitAsync()
    {
        CancelFileTranscription();
        if (_fileTranscriptionTask is { } task)
        {
            await task;
        }
    }

    public void Dispose()
    {
        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = null;
    }
}