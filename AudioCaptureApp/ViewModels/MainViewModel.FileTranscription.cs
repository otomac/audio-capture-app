using System.Globalization;
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

    /// <summary>進捗の百分率（0〜100）。ダイアログの <c>ProgressBar</c> 用。</summary>
    [ObservableProperty]
    private double _fileTranscriptionProgress;

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

    // --- 開始時刻の自動入力 (T150 / REQ-TRX-FILE-15) ---

    /// <summary>開始時刻をどこから推定したか。</summary>
    internal enum StartTimeSource
    {
        /// <summary>推定できなかった（入力欄は空欄のまま）。</summary>
        None,

        /// <summary>ファイル名（本アプリが録音した <c>yyyyMMdd_HHmmss</c> 形式）。</summary>
        FileName,

        /// <summary>ファイルの作成日時。</summary>
        CreationTime,

        /// <summary>最終更新日時 − 音声の長さ（録音終了時刻からの逆算）。</summary>
        LastWriteMinusDuration
    }

    /// <summary>推定した開始時刻と、その根拠。</summary>
    /// <param name="Text">入力欄へ入れる文字列（<c>HH:mm</c>）。推定できなければ空文字列。</param>
    /// <param name="Source">どこから取ったか。</param>
    internal readonly record struct StartTimeEstimate(string Text, StartTimeSource Source);

    /// <summary>
    /// 本アプリが録音したファイル名（<c>yyyyMMdd_HHmmss.mp3</c>、REQ-REC-04）から
    /// 録音開始時刻を取り出す（REQ-TRX-FILE-15 の①）。
    /// </summary>
    /// <remarks>
    /// **拡張子を除いた名前全体が一致する場合だけ受理する。** 前後に何か付いた名前から
    /// 数字列を拾いに行くと、無関係な数字を時刻と誤読して誤った既定値を入れてしまう。
    /// 空欄のほうが害が小さい。
    /// </remarks>
    internal static bool TryParseRecordedFileNameTime(string fileName, out DateTime startTime)
    {
        startTime = default;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        return DateTime.TryParseExact(
            stem, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out startTime);
    }

    /// <summary>
    /// 開始時刻の初期値を推定する（REQ-TRX-FILE-15）。
    /// ①ファイル名 → ②作成日時 → ③最終更新日時 − 音声の長さ の順に試す。
    /// </summary>
    /// <param name="fileName">対象ファイル名（パスを含んでいてもよい）。</param>
    /// <param name="creationTime">作成日時。取れなければ <c>null</c>。</param>
    /// <param name="lastWriteTime">最終更新日時。取れなければ <c>null</c>。</param>
    /// <param name="durationProvider">
    /// 音声全長の取得。**③に落ちたときにしか呼ばれない**（読み取りが重いため）。
    /// </param>
    /// <remarks>
    /// ②を素直に使うと、ファイルが存在する限り常に値が取れて③へ届かない。
    /// **作成日時が最終更新日時より後になっている場合だけ②を捨てる** — コピー・移動された
    /// ファイルは作成日時が「コピーした日時」に書き換わるため、この逆転が
    /// 「作成日時が当てにならない」ことの機械的に検出できる唯一の兆候である。
    /// </remarks>
    internal static StartTimeEstimate InferStartTime(
        string fileName,
        DateTime? creationTime,
        DateTime? lastWriteTime,
        Func<TimeSpan?> durationProvider)
    {
        ArgumentNullException.ThrowIfNull(durationProvider);

        if (TryParseRecordedFileNameTime(fileName, out var fromName))
        {
            return new StartTimeEstimate(FormatStartTime(fromName), StartTimeSource.FileName);
        }

        // 逆転していなければ作成日時を信じる。
        if (creationTime is { } created && (lastWriteTime is not { } written || created <= written))
        {
            return new StartTimeEstimate(FormatStartTime(created), StartTimeSource.CreationTime);
        }

        if (lastWriteTime is { } end && durationProvider() is { } duration)
        {
            return new StartTimeEstimate(
                FormatStartTime(end - duration), StartTimeSource.LastWriteMinusDuration);
        }

        return new StartTimeEstimate(string.Empty, StartTimeSource.None);
    }

    /// <summary>
    /// 入力欄の書式（REQ-TRX-FILE-10 の <c>hh:mm</c>）へ落とす。秒は切り捨てる。
    /// </summary>
    private static string FormatStartTime(DateTime value)
        => value.ToString("HH\\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 実ファイルから材料を集めて <see cref="InferStartTime"/> に渡す（REQ-TRX-FILE-15）。
    /// ファイルに触るのは Service 層の責務なので、読み取りは
    /// <see cref="TranscriptionService"/> の静的ヘルパーへ委ねる。
    /// </summary>
    private static StartTimeEstimate EstimateStartTime(string filePath)
    {
        bool hasTimes = TranscriptionService.TryGetAudioFileTimes(filePath, out var created, out var written);
        return InferStartTime(
            filePath,
            hasTimes ? created : null,
            hasTimes ? written : null,
            // ③に落ちたときだけ呼ばれる（読み取りが重いため）
            () => TranscriptionService.TryGetAudioDuration(filePath, out var duration) ? duration : null);
    }

    /// <summary>
    /// 推定の根拠を利用者へ見せる 1 行（REQ-TRX-FILE-15）。推定していなければ空文字列。
    /// </summary>
    internal static string StartTimeHintFor(StartTimeSource source) => source switch
    {
        StartTimeSource.FileName => "ファイル名から自動入力しました（推定値です。不要なら消してください）",
        StartTimeSource.CreationTime => "ファイルの作成日時から自動入力しました（推定値です。不要なら消してください）",
        StartTimeSource.LastWriteMinusDuration =>
            "最終更新日時と音声の長さから逆算しました（推定値です。不要なら消してください）",
        _ => string.Empty
    };

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

    private bool CanTranscribeFromFile =>
        !IsRecording && !IsStopping && !IsTranscribingFile
        && _transcriptionService.IsModelLoaded;

    [RelayCommand(CanExecute = nameof(CanTranscribeFromFile))]
    private void TranscribeFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "文字起こしする音声ファイルを選択",
            Filter = "音声ファイル (*.wav;*.mp3)|*.wav;*.mp3|すべてのファイル (*.*)|*.*"
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
        FileTranscriptionRequested?.Invoke();
    }

    /// <summary>
    /// ダイアログの「開始」から呼ばれる。開始時刻を解析して本処理へ渡す。
    /// </summary>
    public Task StartFileTranscriptionAsync()
    {
        if (!TryParseStartTime(FileTranscriptionStartTime, out var startOffset))
        {
            return Task.CompletedTask;
        }

        // 走っている Task を掴んでおく。終了確認（REQ-TRX-FILE-14）で
        // 中止処理の完了を待つために要る。
        _fileTranscriptionTask = RunFileTranscriptionAsync(_pendingTranscriptionFilePath, startOffset);
        return _fileTranscriptionTask;
    }

    internal static bool IsSupportedAudioExtension(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath);
        return string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunFileTranscriptionAsync(string filePath, TimeSpan startOffset)
    {
        _fileTranscriptionCts = new CancellationTokenSource();
        IsTranscribingFile = true;
        FileTranscriptionStatus = "準備中...";
        FileTranscriptionProgress = 0;
        StatusMessage = "音声ファイルから文字起こし中...";
        try
        {
            // 話者ダイアライゼーションが有効だと「話者識別中」→「処理中」の 2 フェーズになる。
            // フェーズ名を出さないと、進捗バーが 2 度 0% に戻る理由が分からない（REQ-TRX-FILE-06）。
            var progress = new Progress<Services.FileTranscriptionProgress>(v =>
            {
                FileTranscriptionStatus =
                    $"{v.Phase}: {v.Processed:hh\\:mm\\:ss} / {v.Total:hh\\:mm\\:ss}";
                FileTranscriptionProgress = FileTranscriptionProgressFor(v.Processed, v.Total);
            });
            var token = _fileTranscriptionCts.Token;
            // ファイル I/O とリサンプル処理でUIスレッドをブロックしないようワーカーへ
            // REQ-TRX-FILE-16: 「開始」を押した時点の選択を使う
            var language = SelectedFileLanguage.Code;
            var ok = await Task.Run(() => _transcriptionService.TranscribeFileAsync(
                filePath, startOffset, language, _speakerDiarizationService, progress, token));
            if (ok)
            {
                var txtPath = TranscriptionService.BuildTranscriptPath(filePath);
                FileTranscriptionStatus = "完了";
                StatusMessage = $"文字起こし完了: {txtPath}";
                LastResultPath = txtPath;   // REQ-OPEN-01
            }
            else
            {
                FileTranscriptionStatus = "失敗";
                StatusMessage = "文字起こしに失敗しました";
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
    }

    private bool CanCancelFileTranscription => IsTranscribingFile;

    [RelayCommand(CanExecute = nameof(CanCancelFileTranscription))]
    private void CancelFileTranscription()
    {
        _fileTranscriptionCts?.Cancel();
        FileTranscriptionStatus = "中止中...";
    }
}