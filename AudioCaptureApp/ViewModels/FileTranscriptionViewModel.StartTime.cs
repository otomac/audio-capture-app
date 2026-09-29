using System.Globalization;
using AudioCaptureApp.Services;

namespace AudioCaptureApp.ViewModels;

// FileTranscriptionViewModel のうち、開始時刻の自動入力（REQ-TRX-FILE-15）を担当する部分。
// 本体（FileTranscriptionViewModel.cs）が 500 行を超えるため、機能単位で partial に割っている（ADR-0008）。
public sealed partial class FileTranscriptionViewModel
{
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
        StartTimeSource.FileName => "ファイル名から自動入力しました",
        StartTimeSource.CreationTime => "ファイルの作成日時から自動入力しました",
        StartTimeSource.LastWriteMinusDuration =>
            "最終更新日時と音声の長さから逆算しました",
        _ => string.Empty
    };
}