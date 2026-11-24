namespace AudioCaptureApp.Services;

/// <summary>
/// ファイル文字起こしの進捗（REQ-TRX-FILE-06）。
/// </summary>
/// <param name="Phase">
/// 実行中のフェーズ名。話者ダイアライゼーションが有効なときは「話者識別中」→「処理中」の
/// 2 フェーズになり、フェーズごとに 0% から進む。フェーズ名とセットで表示しないと
/// 進捗バーが 2 度 0% に戻る理由が分からなくなる。
/// </param>
/// <param name="Processed">
/// そのフェーズが読み終えたファイル内の位置。**常にファイル先頭を基準**とし、
/// REQ-TRX-FILE-10 の開始時刻は足さない（残り時間の目安であって時刻ではないため）。
/// </param>
public readonly record struct FileTranscriptionProgress(string Phase, TimeSpan Processed, TimeSpan Total);

/// <summary>
/// ファイル文字起こし 1 回分の指定。オプション指定ダイアログ（REQ-TRX-FILE-09）で「開始」を押した時点の値。
/// </summary>
/// <param name="StartOffset">
/// 出力行のタイムスタンプの起点（REQ-TRX-FILE-10）。ファイル先頭がこの時刻に録音されたものとして扱う。
/// 未指定なら <see cref="TimeSpan.Zero"/>（＝ファイル先頭からの経過時間になる）。
/// </param>
/// <param name="Language">
/// Whisper へ渡す言語（REQ-TRX-FILE-16）。<c>auto</c> なら自動判定。話者識別の有無にかかわらず同じ値を使う。
/// </param>
/// <param name="KnownSpeakerCount">
/// ダイアログで選んだ話者人数（REQ-TRX-DIA-17）。<c>null</c> なら未選択で、設定値に倒れる（REQ-TRX-DIA-07）。
/// 話者識別を通さないときは使われない。
/// </param>
/// <param name="ModelPath">
/// この実行で使う Whisper モデルの実パス（REQ-TRX-FILE-17）。ライブ用に読み込み済みのモデルと同じなら共有し、
/// 違えば実行の間だけ 2 つ目の <c>WhisperFactory</c> を作る。<c>null</c> ならライブ用をそのまま使う。
/// </param>
/// <param name="UseGpu">2 つ目の factory を作るときの GPU 使用有無。ライブ側の設定に従う（REQ-GPU-01 / 02）。</param>
/// <param name="MeetingName">
/// 会議名（REQ-TRX-FILE-18）。空でなければ出力ファイル名に `_会議名` を挟む（REQ-TRX-FILE-05 / REQ-META-02）。
/// </param>
public sealed record FileTranscriptionOptions(
    TimeSpan StartOffset,
    string Language,
    int? KnownSpeakerCount,
    string? ModelPath = null,
    bool UseGpu = true,
    string? MeetingName = null);

/// <summary>ファイル文字起こしの結末（REQ-TRX-FILE-17）。</summary>
public enum FileTranscriptionOutcome
{
    /// <summary>最後まで書き出した。</summary>
    Completed,

    /// <summary>モデルを読み込めず、処理を始めなかった。ダイアログは閉じない。</summary>
    ModelLoadFailed,

    /// <summary>処理中に失敗した（理由は <c>Error</c> イベントにも出している）。</summary>
    Failed
}

/// <summary>
/// <see cref="TranscriptionService.TranscribeFileAsync"/> の戻り値。
/// </summary>
/// <param name="Outcome">結末。</param>
/// <param name="Message"><see cref="FileTranscriptionOutcome.ModelLoadFailed"/> のときの理由。それ以外は <c>null</c>。</param>
/// <param name="OutputPath">
/// <see cref="FileTranscriptionOutcome.Completed"/> のときに書き出したファイル（連番の別名を含む。REQ-TRX-FILE-05）。それ以外は <c>null</c>。
/// </param>
public sealed record FileTranscriptionResult(
    FileTranscriptionOutcome Outcome, string? Message = null, string? OutputPath = null)
{
    /// <summary>最後まで書き出せたか。</summary>
    public bool Success => Outcome == FileTranscriptionOutcome.Completed;
}