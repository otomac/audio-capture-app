using System.Globalization;
using AudioCaptureApp.Services;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、録音中に無音が続いたときの警告を担当する部分（T210 / REQ-REC-14）。
// MainViewModel はファイルを機能単位で partial に割っている（ADR-0008 が引き継ぐ ADR-0005 規則 2）。
public partial class MainViewModel
{
    /// <summary>
    /// 判定器。秒数・しきい値は起動時の設定で固定する（UI からは変更できない。REQ-CFG-16）。
    /// 生成はコンストラクター（<c>MainViewModel.cs</c>）で設定を読んだ直後に行う。
    /// </summary>
    private readonly SilenceWarningTrigger _silenceWarningTrigger;

    /// <summary>判定器に渡した調整値。警告の文言に秒数を出すのに使う。</summary>
    private readonly SilenceWarningOptions _silenceWarningOptions;

    /// <summary>1 回の観測で足す経過の上限。UI スレッドが止まっていた時間をまとめて無音に数えないため。</summary>
    internal static readonly TimeSpan MaxSilenceObserveStep = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 無音が続いたことを利用者に知らせてほしい（REQ-REC-14）。引数は表示する文言。
    /// View は警告ダイアログ（OK のみ）を出す。録音は止めない。
    /// </summary>
    public event Action<string>? SilenceWarningRequested;

    /// <summary>警告ダイアログの文言（REQ-REC-14）。</summary>
    internal static string SilenceWarningMessage(TimeSpan duration)
    {
        var seconds = ((int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return $"録音中ですが、マイクとスピーカーのどちらにも {seconds} 秒以上音声が入っていません。\n\n"
            + "デバイスの選択やミュートを確認してください。録音は続いています。";
    }

    /// <summary>
    /// レベルメーターの更新に相乗りして判定する（REQ-REC-14）。UI スレッドの 50ms タイマーから呼ばれる。
    /// <paramref name="elapsed"/> は前回の更新からの実時間。DispatcherTimer は 50ms より遅れて回るため、
    /// 名目値を足すと 10 秒の設定が 12.8 秒になった（T210 の実測）。止まっていた時間（スリープ等）は 1 秒までに抑える。
    /// </summary>
    private void ObserveSilence(double micLevelDb, double loopbackLevelDb, TimeSpan elapsed)
    {
        var step = ClampMeterStep(elapsed, MaxSilenceObserveStep);
        if (!_silenceWarningTrigger.Observe(micLevelDb, loopbackLevelDb, step, IsRecording && !IsStopping))
        {
            return;
        }

        var message = SilenceWarningMessage(_silenceWarningOptions.Duration);
        StatusMessage = "音声が入っていません。デバイスの選択やミュートを確認してください";
        SilenceWarningRequested?.Invoke(message);
    }
}