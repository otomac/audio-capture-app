using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.Input;

namespace AudioCaptureApp.ViewModels;

// MainViewModel のうち、文字起こし表示ウィンドウへ流す行の蓄積と反映を担当する部分。
// クラスは 1 つのままで、ファイルだけを機能単位に割っている（ADR-0005 案 D）。
public partial class MainViewModel
{
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
}