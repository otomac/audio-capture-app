using System.Globalization;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、音声ファイルからの文字起こし（オプション指定ダイアログ・開始時刻の推定を含む）を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D）。
public partial class MainViewModel
{
    [ObservableProperty]
    private string _fileTranscriptionStatus = "";

    private CancellationTokenSource? _fileTranscriptionCts;

    // --- ファイル文字起こしのオプション指定ダイアログ (T113) ---
    //
    // ダイアログは MainViewModel を DataContext として共有する状態レスな View である
    // （ADR-0002）。ここに置くのはダイアログが見る状態だけで、Window の生成は View 層が行う。

    /// <summary>
    /// オプション指定ダイアログを開いてほしい、という要求（REQ-TRX-FILE-09）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド。ViewModel から
    /// <see cref="System.Windows.Window"/> を直接生成しないための逃がし口（ADR-0002 の規則 2・3）。
    /// </summary>
    public event Action? FileTranscriptionRequested;

    /// <summary>ダイアログで「開始」を押されたときに処理する対象。</summary>
    private string _pendingTranscriptionFilePath = "";

    /// <summary>ダイアログに表示する対象ファイル名（パスは含めない）。</summary>
    [ObservableProperty]
    private string _fileTranscriptionFileName = "";

    /// <summary>開始時刻の入力（`h:mm` / `hh:mm`、空欄は未指定）。REQ-TRX-FILE-10。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartFileTranscription))]
    [NotifyPropertyChangedFor(nameof(HasFileTranscriptionStartTimeError))]
    private string _fileTranscriptionStartTime = "";

    /// <summary>
    /// 開始時刻を自動入力した根拠（REQ-TRX-FILE-15）。空文字列なら何も表示しない。
    /// </summary>
    [ObservableProperty]
    private string _fileTranscriptionStartTimeHint = "";

    partial void OnFileTranscriptionStartTimeChanged(string value)
    {
        // 利用者が触ったらもう「自動入力した値」ではない。
        // 自動入力そのものは、この後に Hint を入れ直すので消えない（RequestFileTranscription の順序）。
        FileTranscriptionStartTimeHint = "";
    }

    /// <summary>
    /// この実行で話者識別を通すか（REQ-TRX-DIA-16）。ダイアログを開くたびに
    /// 「①有効なら ON」へ戻す。**設定には保存しない**（1 回きりの選択）。
    /// </summary>
    [ObservableProperty]
    private bool _fileDiarizationEnabled;

    /// <summary>話者人数の選択肢（REQ-TRX-DIA-17）。「指定なし」「1 人」〜「9 人」「10 人以上」。</summary>
    public IReadOnlyList<SpeakerCountOption> SpeakerCountOptions { get; } = Services.SpeakerCountOptions.All;

    /// <summary>
    /// この実行で指定する話者人数（REQ-TRX-DIA-17）。ダイアログを開くたびに設定値から既定を入れ直す。
    /// **設定には保存しない。**
    /// </summary>
    [ObservableProperty]
    private SpeakerCountOption _selectedSpeakerCount = Services.SpeakerCountOptions.Unspecified;

    /// <summary>
    /// ダイアログを開いたときの話者人数の既定（REQ-TRX-DIA-17）。設定値が 1〜9 ならその人数、
    /// それ以外（<c>null</c>・0 以下・10 以上）は「指定なし」。
    /// </summary>
    /// <remarks>
    /// 10 以上を「10 人以上」ではなく「指定なし」に倒すのは、どちらも未選択（＝設定値に倒れる）で
    /// 意味が同じであり、設定値がそのまま効く従来の挙動を変えないため。
    /// </remarks>
    internal static SpeakerCountOption SpeakerCountOptionFor(int? settingsValue)
    {
        if (settingsValue is int count && count >= 1 && count <= Services.SpeakerCountOptions.MaxSelectableCount)
        {
            return Services.SpeakerCountOptions.All[count];
        }

        return Services.SpeakerCountOptions.Unspecified;
    }

    // --- ファイル文字起こしに使うモデル (T163 / REQ-TRX-FILE-17 / REQ-CFG-09) ---

    /// <summary>
    /// この実行で使う Whisper モデル（REQ-TRX-FILE-17）。ダイアログを開くたびに
    /// <see cref="FileWhisperModelFor"/> で既定を入れ直す。変えた時点で名前を保存する（REQ-CFG-09）。
    /// </summary>
    [ObservableProperty]
    private WhisperModelEntry? _selectedFileWhisperModel;

    partial void OnSelectedFileWhisperModelChanged(WhisperModelEntry? value)
    {
        if (_initializing || _suppressFileWhisperModelWriteBack)
        {
            return;
        }

        _settings.FileWhisperModelName = value?.ModelName;
        SaveSettings();
    }

    /// <summary>ダイアログを開くときの既定を入れる間、保存を抑止する。</summary>
    private bool _suppressFileWhisperModelWriteBack;

    /// <summary>
    /// モデルの読み込みに失敗した理由（REQ-TRX-FILE-17）。空なら表示しない。
    /// 処理を始めていないのでダイアログは閉じない。
    /// </summary>
    [ObservableProperty]
    private string _fileTranscriptionModelError = "";

    /// <summary>
    /// ダイアログを開いたときのモデルの既定（REQ-TRX-FILE-17）。保存名（REQ-CFG-09）の要素 →
    /// ライブ用に選択中のモデル → 一覧の先頭 の順。一覧が空なら <c>null</c>。
    /// </summary>
    internal static WhisperModelEntry? FileWhisperModelFor(
        IReadOnlyList<WhisperModelEntry> models, string? savedName, WhisperModelEntry? liveModel)
    {
        if (!string.IsNullOrEmpty(savedName))
        {
            var saved = models.FirstOrDefault(m => string.Equals(m.ModelName, savedName, StringComparison.Ordinal));
            if (saved != null)
            {
                return saved;
            }
        }

        if (liveModel != null && models.Contains(liveModel))
        {
            return liveModel;
        }

        return models.Count > 0 ? models[0] : null;
    }

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

    /// <summary>
    /// ダイアログの「開始」が押せるか。書式が不正な間は押させない（REQ-TRX-FILE-10）。
    /// </summary>
    public bool CanStartFileTranscription =>
        !IsTranscribingFile && TryParseStartTime(FileTranscriptionStartTime, out _);

    /// <summary>開始時刻の書式が不正か。ダイアログの注意書きの表示条件。</summary>
    public bool HasFileTranscriptionStartTimeError =>
        !TryParseStartTime(FileTranscriptionStartTime, out _);

    /// <summary>
    /// 開始時刻の入力を解析する（REQ-TRX-FILE-10）。
    /// </summary>
    /// <returns>
    /// 受理できたら <c>true</c>。空欄は「未指定」として受理し <see cref="TimeSpan.Zero"/> を返す。
    /// </returns>
    /// <remarks>
    /// 受理するのは 24 時間表記の `h:mm` / `hh:mm` のみ。秒は受け付けない。
    /// <see cref="TimeSpan.TryParseExact(string, string[], IFormatProvider, out TimeSpan)"/> の
    /// `hh` は 0〜23 しか取らないため、`24:00` は自動的に弾かれる。
    /// </remarks>
    internal static bool TryParseStartTime(string text, out TimeSpan startTime)
    {
        startTime = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return TimeSpan.TryParseExact(
            text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out startTime);
    }

    /// <summary>
    /// オプション指定ダイアログを閉じる前に見せる確認文言（REQ-TRX-FILE-13）。
    /// 処理中でなければ <c>null</c> を返し、確認せずに閉じてよいことを表す。
    /// </summary>
    /// <remarks>
    /// 確認を挟むのは、「キャンセル」ボタンが <c>IsCancel</c> であるため **Esc でも閉じうる**ためである。
    /// 確認が無いと、長時間の文字起こしが誤操作で黙って捨てられる。
    /// </remarks>
    internal static string? FileTranscriptionCloseConfirmation(bool isTranscribingFile)
        => isTranscribingFile
            ? "文字起こしを中止して閉じますか？\n作成中の出力ファイルは削除されます。"
            : null;

    /// <summary>進捗を百分率（0〜100）に直す。総時間が 0 なら 0。</summary>
    internal static double FileTranscriptionProgressFor(TimeSpan processed, TimeSpan total)
        => total <= TimeSpan.Zero
            ? 0.0
            : Math.Clamp(processed / total * 100.0, 0.0, 100.0);

    // REQ-TRX-FILE-01 / 17: 可否は「登録済みモデルが 1 つ以上ある」で決める。実際の読み込みは「開始」で行う。
    private bool CanTranscribeFromFile =>
        !IsRecording && !IsStopping && !IsTranscribingFile
        && WhisperModels.Count > 0;

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

        if (!IsSupportedAudioExtension(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath);
            StatusMessage = $"エラー: 対応していないファイル形式です ({ext})";
            return;
        }

        RequestFileTranscription(filePath);
    }

    /// <summary>
    /// 対象ファイルを確定し、オプション指定ダイアログの表示を要求する（REQ-TRX-FILE-09）。
    /// ここでは処理を始めない。始めるのはダイアログの「開始」から呼ばれる
    /// <see cref="StartFileTranscriptionAsync"/>。
    /// </summary>
    private void RequestFileTranscription(string filePath)
    {
        _pendingTranscriptionFilePath = filePath;
        FileTranscriptionFileName = System.IO.Path.GetFileName(filePath);

        // REQ-TRX-FILE-15: 開始時刻の初期値を推定する。
        // Hint は StartTime より**後**に入れること。StartTime の変更ハンドラーが Hint を消すため。
        var estimate = EstimateStartTime(filePath);
        FileTranscriptionStartTime = estimate.Text;
        FileTranscriptionStartTimeHint = StartTimeHintFor(estimate.Source);

        FileTranscriptionStatus = "";
        FileTranscriptionProgress = 0;
        IsFileTranscriptionCancelRequested = false;
        FileTranscriptionCancelNotice = "";
        _isDiarizingFile = false;
        // REQ-TRX-DIA-16: 開くたびに既定へ戻す。①有効なら ON（従来と同じ挙動）、②③は OFF 固定。
        FileDiarizationEnabled = CanChooseFileDiarization;
        // REQ-TRX-DIA-17: 話者人数も開くたびに設定値から入れ直す（通常は「指定なし」）。
        SelectedSpeakerCount = SpeakerCountOptionFor(_settings.KnownSpeakerCount);
        // REQ-TRX-FILE-17: モデルは保存名 → ライブ用 → 先頭。既定を入れるだけなので保存はしない。
        _suppressFileWhisperModelWriteBack = true;
        try
        {
            SelectedFileWhisperModel = FileWhisperModelFor(WhisperModels, _settings.FileWhisperModelName, SelectedWhisperModel);
        }
        finally
        {
            _suppressFileWhisperModelWriteBack = false;
        }
        FileTranscriptionModelError = "";
        // REQ-TRX-FILE-18: メタデータ 3 項目は開くたびに空へ戻す（開始時刻 REQ-TRX-FILE-10 とは別の項目）
        MetadataMeetingName = "";
        MetadataHeldAt = "";
        MetadataParticipantsText = "";
        MetadataTargetName = FileTranscriptionFileName;
        FileTranscriptionRequested?.Invoke();
    }

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

    /// <summary>
    /// 対応形式か（REQ-TRX-FILE-03）。`.wav` / `.mp3` / `.m4a`（AAC）の 3 つ。
    /// デコードはいずれも <c>AudioFileReader</c> で行い、`.wav` 以外は Media Foundation に委ねる（T161）。
    /// </summary>
    internal static bool IsSupportedAudioExtension(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath);
        return string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".m4a", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> RunFileTranscriptionAsync(string filePath, TimeSpan startOffset)
    {
        _fileTranscriptionCts = new CancellationTokenSource();
        IsTranscribingFile = true;
        IsFileTranscriptionCancelRequested = false;
        FileTranscriptionCancelNotice = "";
        FileTranscriptionModelError = "";
        _lastTranscriptionError = null;
        // 最初の進捗が届くまでは話者識別のフェーズではない（走るのはデコードで、ct で素早く止まる）
        _isDiarizingFile = false;
        FileTranscriptionStatus = "準備中...";
        FileTranscriptionProgress = 0;
        StatusMessage = "音声ファイルから文字起こし中...";
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
                UseGpuForTranscription,
                MetadataMeetingName);
            // REQ-TRX-DIA-16: OFF なら無効時と同じ null を渡す。サービス側に切り替えの分岐は無い（REQ-TRX-DIA-03）。
            var diarization = FileDiarizationEnabled ? _speakerDiarizationService : null;
            var result = await Task.Run(() => _transcriptionService.TranscribeFileAsync(
                filePath, options, diarization, progress, token));
            if (result.Success)
            {
                var txtPath = TranscriptionService.BuildTranscriptPath(filePath, MetadataMeetingName);
                FileTranscriptionStatus = "完了";
                StatusMessage = $"文字起こし完了: {txtPath}";
                LastResultPath = txtPath;   // REQ-OPEN-01
                WriteFileTranscriptionMetadata(txtPath);
            }
            else if (result.Outcome == FileTranscriptionOutcome.ModelLoadFailed)
            {
                // REQ-TRX-FILE-17: 処理を始めていない。理由をダイアログ内に出し、閉じない。
                FileTranscriptionStatus = "";
                FileTranscriptionModelError = result.Message ?? "モデルを読み込めませんでした";
                StatusMessage = $"文字起こしを開始できません: {FileTranscriptionModelError}";
                return false;
            }
            else
            {
                FileTranscriptionStatus = "失敗";
                // T134: Error イベントが BeginInvoke で書いたステータスは、この継続が上書きしてしまう
                // （同じ Dispatcher・同じ優先度の FIFO）。理由を併記して手がかりを残す（REQ-TRX-FILE-12）。
                StatusMessage = FileTranscriptionFailureMessageFor(_lastTranscriptionError);
            }
        }
        catch (OperationCanceledException)
        {
            FileTranscriptionStatus = "中止しました";
            StatusMessage = "文字起こしを中止しました";
        }
        // CA1031: UI コマンド境界。ファイル文字起こしの任意の失敗を画面のステータスに変換し、
        //         アプリを落とさずに次の操作へ戻す。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            FileTranscriptionStatus = $"エラー: {ex.Message}";
            StatusMessage = $"エラー: {ex.Message}";
        }
#pragma warning restore CA1031
        finally
        {
            _fileTranscriptionCts?.Dispose();
            _fileTranscriptionCts = null;
            IsTranscribingFile = false;
        }

        return true;
    }

    /// <summary>失敗の 1 行（REQ-TRX-FILE-12）。理由が届いていれば併記する。</summary>
    internal static string FileTranscriptionFailureMessageFor(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "文字起こしに失敗しました"
            : $"文字起こしに失敗しました: {reason}";

    /// <summary>
    /// ファイル文字起こしの完了時に、3 項目のいずれかが入力されていればメタデータ JSON を書く
    /// （REQ-TRX-FILE-18 / REQ-META-01）。3 項目とも空なら作らない。失敗はステータスに出すだけ。
    /// </summary>
    private void WriteFileTranscriptionMetadata(string transcriptPath)
    {
        if (IsMetadataEmpty(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText))
        {
            return;
        }

        var jsonPath = RecordingMetadataFile.BuildMetadataPath(transcriptPath);
        try
        {
            RecordingMetadataFile.Write(
                jsonPath,
                BuildMetadata(MetadataMeetingName, MetadataHeldAt, MetadataParticipantsText,
                    _settings.ParticipantDomainSortedLast));
            StatusMessage = $"文字起こし完了: {transcriptPath} (メタデータ: {System.IO.Path.GetFileName(jsonPath)})";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"文字起こしは完了しましたが、メタデータの保存に失敗しました: {ex.Message}";
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
}