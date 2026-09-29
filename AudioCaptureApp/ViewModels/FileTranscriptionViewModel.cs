using System.Collections.ObjectModel;
using System.Globalization;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

/// <summary>
/// ファイル文字起こしのオプション指定ダイアログ（<c>FileTranscriptionOptionsWindow</c>）の ViewModel（REQ-TRX-FILE-*、ADR-0008）。
/// 対象ファイル・開始時刻・言語・モデル・話者識別・メタデータの入力と、処理の進行（進捗・中止）を担当する。
/// 処理の進行（開始・進捗・中止）は <c>FileTranscriptionViewModel.Run.cs</c>、開始時刻の推定は <c>FileTranscriptionViewModel.StartTime.cs</c> に分けてある。
/// 生成と保持は <see cref="MainViewModel"/> が行い、<see cref="MainViewModel.FileTranscription"/> で公開する。
/// </summary>
/// <remarks>
/// 親の状態のうち書くのは次の 4 つだけである。
/// <list type="bullet">
/// <item><see cref="MainViewModel.IsTranscribingFile"/> — 書き手はこのクラスだけ。書くのは <see cref="SetTranscribing"/> の 1 か所で、
/// そこでこのクラス側の表示（<see cref="IsTranscribingFile"/> / <see cref="CanStartFileTranscription"/> / 「中止」の可否）も更新する。</item>
/// <item><see cref="MainViewModel.StatusMessage"/> — 開始・完了・失敗・中止の 1 行（REQ-TRX-FILE-06）。</item>
/// <item><see cref="MainViewModel.LastResultPath"/> — 完了時の成果物（REQ-OPEN-01）。</item>
/// <item>設定（ファイル用の言語・モデル名）— 変えた時点で <see cref="MainViewModel.SaveSettings"/> で保存する。</item>
/// </list>
/// ダイアログを開く要求（REQ-TRX-FILE-01 / 02）はメインウィンドウのボタンとドロップから来るため、親に残してある。
/// 親は <see cref="Prepare"/> で対象を渡してから表示を要求する。
/// </remarks>
public sealed partial class FileTranscriptionViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;

    /// <summary>コンストラクターで設定値を写している間、保存を抑止する。</summary>
    private readonly bool _initializing;

    internal FileTranscriptionViewModel(MainViewModel main)
    {
        _main = main;
        _initializing = true;
        try
        {
            // REQ-TRX-10: settings.json は手編集され得るので、必ず正規化してから使う。
            SelectedFileLanguage = SettingsViewModel.FindLanguage(
                TranscriptionLanguages.ForFile,
                TranscriptionLanguages.NormalizeForFile(main.AppSettings.FileTranscriptionLanguage));
        }
        finally
        {
            _initializing = false;
        }
    }

    // --- 親の状態の写し（このダイアログが見るもの） ---

    /// <summary>
    /// 処理中か（親の <see cref="MainViewModel.IsTranscribingFile"/>）。ダイアログの入力欄を無効にし、進捗表示へ切り替える（REQ-TRX-FILE-11）。
    /// </summary>
    public bool IsTranscribingFile => _main.IsTranscribingFile;

    /// <summary>話者識別の状態（REQ-TRX-DIA-15。起動時に決まり、以後変わらない）。</summary>
    public string SpeakerDiarizationStatus => _main.SpeakerDiarizationStatus;

    /// <summary>「話者識別を行う」の操作可否（REQ-TRX-DIA-16。起動後は変わらない）。</summary>
    public bool CanChooseFileDiarization => _main.CanChooseFileDiarization;

    /// <summary>登録済み Whisper モデル（REQ-CFG-08）。<see cref="SettingsViewModel.WhisperModels"/> と同じインスタンス。</summary>
    public ObservableCollection<WhisperModelEntry> WhisperModels => _main.Settings.WhisperModels;

    /// <summary>
    /// 親の処理中フラグを書き、このダイアログ側の表示と操作の可否を合わせて更新する。
    /// フラグの書き手はこのクラスだけなので、中継はここだけで足りる。
    /// </summary>
    private void SetTranscribing(bool value)
    {
        _main.IsTranscribingFile = value;
        OnPropertyChanged(nameof(IsTranscribingFile));
        OnPropertyChanged(nameof(CanStartFileTranscription));
        CancelFileTranscriptionCommand.NotifyCanExecuteChanged();
    }

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
        // 自動入力そのものは、この後に Hint を入れ直すので消えない（Prepare の順序）。
        FileTranscriptionStartTimeHint = "";
    }

    // --- ファイル文字起こしの言語 (T153 / REQ-TRX-FILE-16) ---

    /// <summary>ファイル文字起こしの選択肢（REQ-TRX-FILE-16）。自動判定を含む。</summary>
    public IReadOnlyList<TranscriptionLanguage> FileLanguageOptions { get; } = TranscriptionLanguages.ForFile;

    /// <summary>ファイル文字起こしの言語（REQ-TRX-FILE-16）。ライブ用とは独立。</summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedFileLanguage = TranscriptionLanguages.ForFile[0];

    partial void OnSelectedFileLanguageChanged(TranscriptionLanguage value)
    {
        if (!_initializing)
        {
            _main.SaveSettings();
        }
    }

    // --- 話者識別 (REQ-TRX-DIA-16 / 17) ---

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

        _main.AppSettings.FileWhisperModelName = value?.ModelName;
        _main.SaveSettings();
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

    // --- メタデータ (REQ-TRX-FILE-18 / 19) ---
    // 録音停止後のダイアログ（RecordingMetadataViewModel）と同じ 3 項目。組み立ての規則も共有する。

    /// <summary>会議名（REQ-TRX-FILE-18。空でなければ出力ファイル名にも付く）。</summary>
    [ObservableProperty]
    private string _metadataMeetingName = "";

    /// <summary>実施日時（自由記述）。</summary>
    [ObservableProperty]
    private string _metadataHeldAt = "";

    /// <summary>参加者（複数行。改行・`,`・`、`・`;` 区切り。REQ-META-03）。</summary>
    [ObservableProperty]
    private string _metadataParticipantsText = "";

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

    /// <summary>
    /// 対象ファイルを確定し、ダイアログの入力を開くたびの既定へ戻す（REQ-TRX-FILE-09）。
    /// 表示の要求は親が続けて上げる。ここでは処理を始めない。始めるのはダイアログの「開始」から呼ばれる
    /// <see cref="StartFileTranscriptionAsync"/>。
    /// </summary>
    internal void Prepare(string filePath)
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
        var settings = _main.AppSettings;
        // REQ-TRX-DIA-16: 開くたびに既定へ戻す。①有効なら ON（従来と同じ挙動）、②③は OFF 固定。
        FileDiarizationEnabled = CanChooseFileDiarization;
        // REQ-TRX-DIA-17: 話者人数も開くたびに設定値から入れ直す（通常は「指定なし」）。
        SelectedSpeakerCount = SpeakerCountOptionFor(settings.KnownSpeakerCount);
        // REQ-TRX-FILE-17: モデルは保存名 → ライブ用 → 先頭。既定を入れるだけなので保存はしない。
        _suppressFileWhisperModelWriteBack = true;
        try
        {
            SelectedFileWhisperModel = FileWhisperModelFor(
                WhisperModels, settings.FileWhisperModelName, _main.Settings.SelectedWhisperModel);
        }
        finally
        {
            _suppressFileWhisperModelWriteBack = false;
        }
        FileTranscriptionModelError = "";
        // REQ-TRX-FILE-18 / 19: メタデータ 3 項目は開くたびに入れ直す（開始時刻 REQ-TRX-FILE-10 とは別の項目）。
        // 入力ファイルと同じ stem の .json（録音時に作られたもの）があればその内容、無ければ空。
        var existing = RecordingMetadataFile.TryRead(RecordingMetadataFile.BuildMetadataPath(filePath));
        MetadataMeetingName = existing?.MeetingName ?? "";
        MetadataHeldAt = existing?.HeldAt ?? "";
        MetadataParticipantsText = existing == null ? "" : RecordingMetadataFile.FormatParticipants(existing.Participants);
    }
}