using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using AudioCaptureApp.Models;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioCaptureService _audioCaptureService = new();
    private readonly TranscriptionService _transcriptionService = new();
    private readonly SettingsService _settingsService = new();

    /// <summary>
    /// 話者ダイアライゼーション（REQ-TRX-DIA-03）。設定で無効なら <c>null</c> のままにする。
    /// 生成と破棄はここが持ち、<see cref="TranscriptionService"/> へは引数で貸すだけである
    /// （[ADR-0003](../../docs/adr/0003-speaker-diarization-with-sherpa-onnx.md) の決定 D11）。
    /// </summary>
    private readonly SpeakerDiarizationService? _speakerDiarizationService;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _clockTimer;

    /// <summary>
    /// 読み込んだ設定そのもの。SaveSettings はこのインスタンスを更新して保存する。
    /// 毎回 new すると、UI を持たない設定項目（無音カットの調整値など）が
    /// 保存のたびに既定値へ戻ってしまうため。
    /// </summary>
    private readonly AppSettings _settings;

    private DateTime _recordingStartTime;
    private bool _initializing;
    private bool _suppressMicMuteWriteBack;
    private bool _suppressUseGpuWriteBack;

    public MainViewModel()
    {
        _initializing = true;
        // dBメーター用タイマー（常時動作、50ms間隔）
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _meterTimer.Start();

        // 経過時間用タイマー（録音中のみ、1秒間隔）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _recordingStartTime;
            ElapsedTime = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture);
        };

        _audioCaptureService.RecordingError += OnRecordingError;
        _audioCaptureService.MicMuteChangedExternally += OnMicMuteChangedExternally;
        _transcriptionService.Error += msg =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                StatusMessage = $"文字起こしエラー: {msg}");
        _transcriptionService.RuntimeInfo += runtime =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                StatusMessage = $"Whisperランタイム: {runtime}");
        // 文字起こしワーカースレッドから発火するため、必ず Dispatcher を経由する（NFR-01）
        _transcriptionService.SegmentTranscribed += QueueLiveTranscriptLine;

        _settings = _settingsService.Load();
        OutputFolder = _settings.OutputFolder;
        TranscriptionEnabled = _settings.TranscriptionEnabled;
        WhisperModelPath = _settings.WhisperModelPath;
        UseGpuForTranscription = _settings.UseGpuForTranscription;

        // REQ-TRX-10: settings.json は手編集され得るので、必ず正規化してから使う。
        // 一覧に無いコードや、ライブ側の "auto" は日本語へ倒れる。
        SelectedLiveLanguage = FindLanguage(
            TranscriptionLanguages.ForLive,
            TranscriptionLanguages.NormalizeForLive(_settings.LiveTranscriptionLanguage));
        SelectedFileLanguage = FindLanguage(
            TranscriptionLanguages.ForFile,
            TranscriptionLanguages.NormalizeForFile(_settings.FileTranscriptionLanguage));

        _transcriptionService.SilenceCut = new SilenceCutOptions(
            _settings.SilenceRmsThreshold,
            _settings.SilenceMergeGapSeconds,
            _settings.VoicedPaddingSeconds);

        var diarizationOptions = new SpeakerDiarizationOptions(
            _settings.SpeakerSegmentationModelPath,
            _settings.SpeakerEmbeddingModelPath,
            _settings.SpeakerClusteringThreshold,
            _settings.KnownSpeakerCount,
            _settings.SpeakerDiarizationThreads);

        // REQ-TRX-DIA-15: 起動時に 1 度だけ状態を判定する。**読み込みはしない**
        // （存在検査だけ。ADR-0003 N2 の遅延読み込みを維持する）。
        var availability = DiarizationAvailabilityFor(
            _settings.SpeakerDiarizationEnabled,
            SpeakerDiarizationService.ModelFilesExist(diarizationOptions));
        SpeakerDiarizationStatus = DiarizationStatusTextFor(availability);
        IsSpeakerDiarizationReady = IsSpeakerDiarizationReadyFor(availability);
        SpeakerDiarizationTooltip = DiarizationTooltipFor(availability);

        // 有効なときだけ生成する。モデルの読み込みは初回の実行まで遅らせるため、
        // ここで生成してもモデルが未配置なら起動を妨げない（エラーは実行時に出る）。
        if (_settings.SpeakerDiarizationEnabled)
        {
            _speakerDiarizationService = new SpeakerDiarizationService(diarizationOptions);
        }

        // モデルパスが設定されていれば常にロードする（ファイル文字起こしは
        // ライブ用チェックボックスと独立して動作する）
        if (!string.IsNullOrEmpty(WhisperModelPath))
        {
            TryLoadWhisperModel();
        }

        RefreshDevicesInternal();

        // 前回のマイク選択を復元
        if (_settings.LastSelectedDeviceId != null)
        {
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == _settings.LastSelectedDeviceId);
        }
        SelectedCaptureDevice ??= CaptureDevices.FirstOrDefault(d => d.IsDefault) ?? CaptureDevices.FirstOrDefault();

        // 前回のスピーカー選択を復元
        if (_settings.LastSelectedLoopbackDeviceId != null)
        {
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == _settings.LastSelectedLoopbackDeviceId);
        }

        _initializing = false;
    }

    // --- マイク入力デバイス ---
    public ObservableCollection<AudioDevice> CaptureDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedCaptureDevice;

    partial void OnSelectedCaptureDeviceChanged(AudioDevice? value)
    {
        if (value != null)
        {
            if (_audioCaptureService.StartMicMonitor(value))
            {
                // 起動時／デバイス切替時に OS の現在ミュート値を ViewModel に反映
                _suppressMicMuteWriteBack = true;
                try { IsMicMuted = _audioCaptureService.IsMicMuted; }
                finally { _suppressMicMuteWriteBack = false; }
            }
            else
            {
                // REQ-DEV-08: 失敗しても選択操作自体は成功させ、機能低下として通知する。
                // REQ-DEV-09: OS 側のミュート状態は取得できないため反映しない。
                StatusMessage = $"マイクの音声を取得できません: {value.FriendlyName}";
            }
        }
        else
        {
            _audioCaptureService.StopMicMonitor();
        }
    }

    // --- スピーカー（ループバック）デバイス ---
    public ObservableCollection<AudioDevice> RenderDevices { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private AudioDevice? _selectedRenderDevice;

    partial void OnSelectedRenderDeviceChanged(AudioDevice? value)
    {
        // マイクと同じく、録音の有無に関わらずデバイス選択でモニタリングを開始する
        // （REQ-DEV-06 / REQ-DEV-07）
        if (value != null)
        {
            if (!_audioCaptureService.StartLoopbackMonitor(value))
            {
                // REQ-DEV-08: 失敗しても選択操作自体は成功させ、機能低下として通知する
                StatusMessage = $"スピーカーの音声を取得できません: {value.FriendlyName}";
            }
        }
        else
        {
            _audioCaptureService.StopLoopbackMonitor();
        }
    }

    // --- 共通プロパティ ---
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopRecordingCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshDevicesCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectOutputFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectWhisperModelCommand))]
    [NotifyCanExecuteChangedFor(nameof(TranscribeFromFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFileTranscriptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    private bool _isTranscribingFile;

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

    // --- 文字起こし表示ウィンドウ (T114) ---

    /// <summary>
    /// 文字起こし表示ウィンドウを開いてほしい、という要求（REQ-LIVEVIEW-01）。
    /// 購読するのは <c>MainWindow</c> のコードビハインド（ADR-0002 の規則 2・3）。
    /// </summary>
    public event Action? LiveTranscriptRequested;

    /// <summary>
    /// 表示する文字起こし行。ライブ・ファイルの両方を含む（REQ-LIVEVIEW-03）。
    /// </summary>
    public ObservableCollection<string> LiveTranscriptLines { get; } = new();

    /// <summary>
    /// 表示行数の上限（REQ-LIVEVIEW-04）。長時間録音でコレクションが無制限に伸びるのを防ぐ。
    /// 捨てられるのは表示だけで、テキストファイルには全行が残る。
    /// </summary>
    internal const int MaxLiveTranscriptLines = 100;

    /// <summary>
    /// 行を末尾へ追加し、上限を超えた分を<b>先頭から</b>捨てる（REQ-LIVEVIEW-04）。
    /// 末尾から捨てると最新の行が消えるため、向きを間違えないこと。
    /// </summary>
    internal static void AppendLiveTranscriptLine(IList<string> lines, string line, int maxLines)
    {
        lines.Add(line);
        while (lines.Count > maxLines)
        {
            lines.RemoveAt(0);
        }
    }

    /// <summary>
    /// まとめて届いた行を追加する（REQ-LIVEVIEW-09）。
    /// <b>上限を超えるぶんは追加せずに捨てる。</b>
    /// </summary>
    /// <remarks>
    /// 1 行ずつ追加してから <see cref="AppendLiveTranscriptLine"/> に捨てさせても
    /// 最終状態は同じだが、追加のたびに <c>CollectionChanged</c> → レイアウト →
    /// <c>ScrollIntoView</c>（REQ-LIVEVIEW-07）が走る。表示上限で直後に捨てられる行に
    /// その費用を払う理由が無いため、先に落とす。
    /// <para>
    /// 実測（2,100 行・表示ウィンドウを開いた状態）: 1 行ずつ 665〜735ms →
    /// 本メソッド 143ms。UI の詰まり（50ms タイマーの最大間隔）は 177ms → 99ms。
    /// </para>
    /// </remarks>
    internal static void AppendLiveTranscriptLines(
        IList<string> lines, IReadOnlyList<string> batch, int maxLines)
    {
        // 上限ぶんだけ残す。batch が上限より短ければ全部が対象になる。
        for (int i = Math.Max(0, batch.Count - maxLines); i < batch.Count; i++)
        {
            AppendLiveTranscriptLine(lines, batch[i], maxLines);
        }
    }

    /// <summary>
    /// UI へ渡す前に行を溜めておくキュー（REQ-LIVEVIEW-09）。
    /// 文字起こしワーカースレッドから積まれ、UI スレッドで引き取る。
    /// </summary>
    private readonly ConcurrentQueue<string> _pendingTranscriptLines = new();

    /// <summary>引き取りを二重に予約しないための旗。0 = 未予約 / 1 = 予約済み。</summary>
    private int _transcriptFlushScheduled;

    /// <summary>
    /// <see cref="TranscriptionService.SegmentTranscribed"/> の受け口（REQ-LIVEVIEW-09）。
    /// **ワーカースレッドから呼ばれる。** ここで UI に触れてはならない（NFR-01）。
    /// </summary>
    private void QueueLiveTranscriptLine(string line)
    {
        _pendingTranscriptLines.Enqueue(line);

        // 既に引き取りが予約されているなら、積むだけで済ませる。
        // これが無いと 1 行ごとに BeginInvoke が積まれ、間引きの意味が無くなる。
        if (Interlocked.Exchange(ref _transcriptFlushScheduled, 1) == 0)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(FlushLiveTranscriptLines);
        }
    }

    /// <summary>
    /// 溜まった行を UI スレッドで引き取る（REQ-LIVEVIEW-09）。
    /// </summary>
    /// <remarks>
    /// 旗を下ろすのは**取り出しより先**である。取り出しの最中に積まれた行が
    /// 次の引き取りを予約できるようにするため。逆にすると、その行は次の 1 行が
    /// 届くまで画面に出ない。空振りの引き取りが 1 回増えることがあるが無害である。
    /// </remarks>
    private void FlushLiveTranscriptLines()
    {
        Interlocked.Exchange(ref _transcriptFlushScheduled, 0);

        var batch = new List<string>();
        while (_pendingTranscriptLines.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        AppendLiveTranscriptLines(LiveTranscriptLines, batch, MaxLiveTranscriptLines);
    }

    [RelayCommand]
    private void ShowLiveTranscript() => LiveTranscriptRequested?.Invoke();

    private static readonly SolidColorBrush RecordingBrush = new(Color.FromRgb(0xCC, 0x00, 0x00));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(0x00, 0x00, 0x00));

    static MainViewModel()
    {
        RecordingBrush.Freeze();
        StoppedBrush.Freeze();
    }

    public string RecordingStatusText => IsStopping ? "停止処理中" : IsRecording ? "録音中" : "停止中";
    public SolidColorBrush RecordingStatusColor => IsRecording ? RecordingBrush : StoppedBrush;

    public bool IsNotBusy => !IsRecording && !IsStopping && !IsTranscribingFile;

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
        OnPropertyChanged(nameof(RecordingStatusColor));
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanToggleGpu));
    }

    partial void OnIsStoppingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingStatusText));
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanToggleGpu));
    }

    partial void OnIsTranscribingFileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanToggleGpu));
        OnPropertyChanged(nameof(CanStartFileTranscription));
    }

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private string _statusMessage = "待機中";

    /// <summary>
    /// 直近の成果物のパス（REQ-OPEN-01）。録音とファイル文字起こしで保存先が異なるため、
    /// 「設定上の保存先」ではなくここを開く対象にする。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenResultFolderCommand))]
    private string _lastResultPath = string.Empty;

    /// <summary>
    /// 「保存先を開く」の可否。成果物が未設定なら無効（REQ-OPEN-04）。加えて録音中・停止処理中・
    /// ファイル文字起こし中も無効にする（REQ-OPEN-05）。保持しているのは進行中の作業ではなく
    /// それ以前の成果物であり、開けてしまうと誤解を招くため。
    /// </summary>
    internal static bool CanOpenResultFolderFor(string lastResultPath, bool isNotBusy)
        => isNotBusy && !string.IsNullOrEmpty(lastResultPath);

    private bool CanOpenResultFolder => CanOpenResultFolderFor(LastResultPath, IsNotBusy);

    [RelayCommand(CanExecute = nameof(CanOpenResultFolder))]
    private void OpenResultFolder()
    {
        var arguments = BuildExplorerArguments(LastResultPath);
        if (arguments == null)
        {
            StatusMessage = $"保存先が見つかりません: {LastResultPath}";
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true
            });
        }
        // CA1031: シェル起動は環境依存で任意の例外を投げうる。フォルダを開けなくても
        //         アプリの動作には影響しないため、画面のステータスに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            StatusMessage = $"保存先を開けませんでした: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// エクスプローラーへ渡す引数を組み立てる。成果物が存在すれば選択状態で開き
    /// （REQ-OPEN-02）、無ければ親フォルダを開く（REQ-OPEN-03）。
    /// どちらも存在しなければ <c>null</c>。
    /// </summary>
    internal static string? BuildExplorerArguments(string resultPath)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return null;
        }

        if (System.IO.File.Exists(resultPath))
        {
            return $"/select,\"{resultPath}\"";
        }

        var folder = System.IO.Path.GetDirectoryName(resultPath);
        return !string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder)
            ? $"\"{folder}\""
            : null;
    }

    // --- 話者識別の状態表示 (T152 / REQ-TRX-DIA-15) ---

    /// <summary>話者ダイアライゼーションが使える状態か。</summary>
    internal enum DiarizationAvailability
    {
        /// <summary>設定で無効（<c>SpeakerDiarizationEnabled = false</c>）。</summary>
        Disabled,

        /// <summary>有効だがモデルファイルが揃っていない。</summary>
        ModelMissing,

        /// <summary>有効で、モデル 2 ファイルが揃っている。</summary>
        Available
    }

    /// <summary>
    /// ステータスバーに常時出す話者識別の状態（REQ-TRX-DIA-15）。
    /// <see cref="StatusMessage"/> とは別の欄に出す。あちらは起動直後に
    /// Whisper のランタイム情報で上書きされるため、ここに書くと消えてしまう。
    /// </summary>
    [ObservableProperty]
    private string _speakerDiarizationStatus = "";

    /// <summary>
    /// 「話者識別」チェックボックス（編集不可）の状態。使える状態のときだけ true。
    /// </summary>
    [ObservableProperty]
    private bool _isSpeakerDiarizationReady;

    /// <summary>「話者識別」チェックボックスのツールチップ（REQ-TRX-DIA-15）。</summary>
    [ObservableProperty]
    private string _speakerDiarizationTooltip = "";

    /// <summary>
    /// 3 状態を決める（REQ-TRX-DIA-15）。<paramref name="modelFilesExist"/> は
    /// **存在検査の結果**であって、読み込めることの保証ではない。
    /// </summary>
    internal static DiarizationAvailability DiarizationAvailabilityFor(bool enabled, bool modelFilesExist)
    {
        if (!enabled)
        {
            return DiarizationAvailability.Disabled;
        }

        return modelFilesExist ? DiarizationAvailability.Available : DiarizationAvailability.ModelMissing;
    }

    /// <summary>
    /// 状態を利用者向けの文言にする（REQ-TRX-DIA-15）。
    /// 「有効」は<b>モデルが置いてある</b>ことまでしか言えないため、断定しすぎない語にする。
    /// </summary>
    internal static string DiarizationStatusTextFor(DiarizationAvailability availability) => availability switch
    {
        DiarizationAvailability.Available => "話者識別: 有効",
        DiarizationAvailability.ModelMissing => "話者識別: モデル未配置",
        _ => "話者識別: 無効"
    };

    /// <summary>チェックが入るのは「有効」のときだけ（REQ-TRX-DIA-15）。</summary>
    internal static bool IsSpeakerDiarizationReadyFor(DiarizationAvailability availability)
        => availability == DiarizationAvailability.Available;

    /// <summary>チェックボックスのツールチップ。状態ごとに次の一手が分かるようにする。</summary>
    internal static string DiarizationTooltipFor(DiarizationAvailability availability) => availability switch
    {
        DiarizationAvailability.Available =>
            "ファイル文字起こしの結果に [話者N] が付きます（モデルの配置を確認した結果であり、"
            + "読み込みに成功するかは実行時に分かります）。",
        DiarizationAvailability.ModelMissing =>
            "有効に設定されていますが、モデルファイルが見つかりません。"
            + "settings.json の SpeakerSegmentationModelPath / SpeakerEmbeddingModelPath を確認してください。",
        _ => "settings.json の SpeakerDiarizationEnabled を true にすると有効になります。"
    };

    // --- 文字起こしの言語 (T153 / REQ-TRX-10) ---

    /// <summary>ライブ文字起こしの選択肢（REQ-TRX-LIVE-14）。自動判定は含まない。</summary>
    public IReadOnlyList<TranscriptionLanguage> LiveLanguageOptions { get; } = TranscriptionLanguages.ForLive;

    /// <summary>ファイル文字起こしの選択肢（REQ-TRX-FILE-16）。自動判定を含む。</summary>
    public IReadOnlyList<TranscriptionLanguage> FileLanguageOptions { get; } = TranscriptionLanguages.ForFile;

    /// <summary>
    /// ライブ文字起こしの言語。**変更は次に録音を開始したときから効く**
    /// （<c>WhisperProcessor</c> は録音開始時に作られるため。REQ-TRX-LIVE-14）。
    /// </summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedLiveLanguage = TranscriptionLanguages.ForLive[0];

    /// <summary>ファイル文字起こしの言語（REQ-TRX-FILE-16）。ライブ用とは独立。</summary>
    [ObservableProperty]
    private TranscriptionLanguage _selectedFileLanguage = TranscriptionLanguages.ForFile[0];

    partial void OnSelectedLiveLanguageChanged(TranscriptionLanguage value)
    {
        _transcriptionService.LiveLanguage = value.Code;
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    partial void OnSelectedFileLanguageChanged(TranscriptionLanguage value)
    {
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    /// <summary>正規化済みのコードから選択肢の実体を引く。見つからなければ先頭（日本語）。</summary>
    private static TranscriptionLanguage FindLanguage(
        IReadOnlyList<TranscriptionLanguage> options, string code)
    {
        foreach (var option in options)
        {
            if (string.Equals(option.Code, code, StringComparison.Ordinal))
            {
                return option;
            }
        }

        return options[0];
    }

    // --- 文字起こし設定 ---
    [ObservableProperty]
    private bool _transcriptionEnabled;

    partial void OnTranscriptionEnabledChanged(bool value)
    {
        // このチェックボックスは「録音中のライブ文字起こし」の ON/OFF のみを司る
        // モデルのロード自体はパスが設定されていれば常に行う
        if (value)
        {
            if (_transcriptionService.IsModelLoaded)
            {
                _audioCaptureService.SetTranscriptionService(_transcriptionService);
            }
            else
            {
                TryLoadWhisperModel();
            }
        }
        else
        {
            _audioCaptureService.SetTranscriptionService(null);
        }
        if (!_initializing)
        {
            SaveSettings();
        }
    }

    [ObservableProperty]
    private string _whisperModelPath = string.Empty;

    [ObservableProperty]
    private string _transcriptionStatus = "";

    // --- 文字起こしGPU使用設定 ---
    [ObservableProperty]
    private bool _useGpuForTranscription = true;

    [ObservableProperty]
    private bool _gpuAvailable = true;

    public bool CanToggleGpu => IsNotBusy && GpuAvailable;

    partial void OnGpuAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanToggleGpu));
    }

    partial void OnUseGpuForTranscriptionChanged(bool value)
    {
        if (_initializing || _suppressUseGpuWriteBack)
        {
            return;
        }
        SaveSettings();
        TryLoadWhisperModel();
    }

    [ObservableProperty]
    private bool _isMicMuted;

    partial void OnIsMicMutedChanged(bool value)
    {
        if (_suppressMicMuteWriteBack) return;
        _audioCaptureService.IsMicMuted = value;
    }

    [ObservableProperty]
    private bool _isSpeakerMuted;

    partial void OnIsSpeakerMutedChanged(bool value)
    {
        _audioCaptureService.IsSpeakerMuted = value;
    }

    [ObservableProperty]
    private double _micLevelDb = -60.0;

    [ObservableProperty]
    private double _loopbackLevelDb = -60.0;

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

    private bool CanSelectOutputFolder => !IsRecording && !IsStopping && !IsTranscribingFile;

    [RelayCommand(CanExecute = nameof(CanSelectOutputFolder))]
    private void SelectOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存先フォルダを選択" };
        if (dialog.ShowDialog() == true)
        {
            OutputFolder = dialog.FolderName;
            SaveSettings();
        }
    }

    private bool CanRefreshDevices => !IsRecording && !IsStopping && !IsTranscribingFile;

    [RelayCommand(CanExecute = nameof(CanRefreshDevices))]
    private void RefreshDevices()
    {
        RefreshDevicesInternal();
        StatusMessage = $"デバイス一覧を更新しました (マイク {CaptureDevices.Count} / スピーカー {RenderDevices.Count})";
    }

    private void RefreshDevicesInternal()
    {
        _audioCaptureService.RefreshDevices();

        var prevCapture = SelectedCaptureDevice?.DeviceId;
        var prevRender = SelectedRenderDevice?.DeviceId;

        CaptureDevices.Clear();
        foreach (var d in _audioCaptureService.GetCaptureDevices())
        {
            CaptureDevices.Add(d);
        }

        RenderDevices.Clear();
        foreach (var d in _audioCaptureService.GetRenderDevices())
        {
            RenderDevices.Add(d);
        }

        if (prevCapture != null)
        {
            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.DeviceId == prevCapture);
        }
        if (prevRender != null)
        {
            SelectedRenderDevice = RenderDevices.FirstOrDefault(d => d.DeviceId == prevRender);
        }
    }

    private void UpdateMeters()
    {
        MicLevelDb = PeakToDb(_audioCaptureService.MicPeakLevel);
        LoopbackLevelDb = PeakToDb(_audioCaptureService.LoopbackPeakLevel);
    }

    internal static double PeakToDb(float peak)
    {
        if (peak <= 0f)
        {
            return -60.0;
        }
        double db = 20.0 * Math.Log10(peak);
        return Math.Clamp(db, -60.0, 3.0);
    }

    private void OnMicMuteChangedExternally(bool newMute)
    {
        // OnVolumeNotification は非UIスレッドで発火するため Dispatcher 経由
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (IsMicMuted == newMute) return;
            _suppressMicMuteWriteBack = true;
            try { IsMicMuted = newMute; }
            finally { _suppressMicMuteWriteBack = false; }
        });
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

    private bool CanSelectWhisperModel => !IsRecording && !IsStopping && !IsTranscribingFile;

    [RelayCommand(CanExecute = nameof(CanSelectWhisperModel))]
    private void SelectWhisperModel()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Whisperモデルファイルを選択",
            Filter = "GGMLモデル (*.bin)|*.bin|すべてのファイル (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            WhisperModelPath = dialog.FileName;
            TryLoadWhisperModel();
            SaveSettings();
        }
    }

    private bool _isLoadingModel;

    private async void TryLoadWhisperModel()
    {
        if (string.IsNullOrEmpty(WhisperModelPath))
        {
            TranscriptionStatus = "モデルパス未設定";
            _audioCaptureService.SetTranscriptionService(null);
            return;
        }

        if (!System.IO.File.Exists(WhisperModelPath))
        {
            TranscriptionStatus = "モデルファイルが見つかりません";
            _audioCaptureService.SetTranscriptionService(null);
            return;
        }

        if (_isLoadingModel)
        {
            return;
        }

        try
        {
            _isLoadingModel = true;
            TranscriptionStatus = "モデル読み込み中...";
            var modelPath = WhisperModelPath;
            var requestGpu = UseGpuForTranscription;
            var (success, gpuAvailable) = await Task.Run(() => _transcriptionService.LoadModel(modelPath, requestGpu));
            if (success)
            {
                GpuAvailable = gpuAvailable;
                if (!gpuAvailable && requestGpu)
                {
                    // GPUが利用不可と判明した場合は設定を強制的にOFFにする
                    _suppressUseGpuWriteBack = true;
                    try { UseGpuForTranscription = false; }
                    finally { _suppressUseGpuWriteBack = false; }
                    SaveSettings();
                }

                TranscriptionStatus = "モデル読み込み完了";
                // ライブ文字起こしが ON のときのみ、録音サービスにワイヤする
                if (TranscriptionEnabled)
                {
                    _audioCaptureService.SetTranscriptionService(_transcriptionService);
                }
            }
            else
            {
                TranscriptionStatus = "モデル読み込み失敗";
                _audioCaptureService.SetTranscriptionService(null);
            }
        }
        // CA1031: async void（例外を漏らすとプロセスごと落ちる）かつ Whisper のネイティブ
        //         読み込み境界のため、全例外を画面のステータスに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            TranscriptionStatus = $"モデル読み込みエラー: {ex.Message}";
            _audioCaptureService.SetTranscriptionService(null);
        }
#pragma warning restore CA1031
        finally
        {
            _isLoadingModel = false;
            TranscribeFromFileCommand.NotifyCanExecuteChanged();
        }
    }

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

    private void SaveSettings()
    {
        // UI を持たない設定項目（無音カットの調整値など）を消さないため、
        // 読み込んだインスタンスの UI 対応プロパティだけを更新して保存する。
        _settings.OutputFolder = OutputFolder;
        _settings.LastSelectedDeviceId = SelectedCaptureDevice?.DeviceId;
        _settings.LastSelectedLoopbackDeviceId = SelectedRenderDevice?.DeviceId;
        _settings.TranscriptionEnabled = TranscriptionEnabled;
        _settings.WhisperModelPath = WhisperModelPath;
        _settings.UseGpuForTranscription = UseGpuForTranscription;
        _settings.LiveTranscriptionLanguage = SelectedLiveLanguage.Code;
        _settings.FileTranscriptionLanguage = SelectedFileLanguage.Code;
        _settingsService.Save(_settings);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    // アンマネージドリソースを直接は保持しないため、ファイナライザーは持たず
    // disposing == false のときは何もしない。
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }
        _meterTimer.Stop();
        _clockTimer.Stop();
        _fileTranscriptionCts?.Dispose();
        _fileTranscriptionCts = null;
        _audioCaptureService.Dispose();
        _transcriptionService.Dispose();
        _speakerDiarizationService?.Dispose();
    }
}