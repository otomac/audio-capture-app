using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using AudioCaptureApp.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

/// <summary>
/// 文字起こし表示ウィンドウ（<c>LiveTranscriptWindow</c>）の ViewModel。表示する行の蓄積と反映を担当する
/// （ADR-0008）。生成と保持は <see cref="MainViewModel"/> が行い、<see cref="MainViewModel.LiveTranscript"/> で公開する。
/// </summary>
/// <remarks>
/// 親の状態（処理中フラグ・ステータス表示）には一切触れない。行は <see cref="TranscriptionService.SegmentTranscribed"/>
/// から <see cref="QueueLine"/> へ届き、録音の開始時に親が <see cref="Clear"/> を呼ぶ（REQ-LIVEVIEW-08）。
/// ウィンドウを開く要求（REQ-LIVEVIEW-01）はメインウィンドウのボタンから来るため、親の <c>ShowLiveTranscriptCommand</c> に残してある。
/// </remarks>
public sealed class LiveTranscriptViewModel : ObservableObject
{
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
    internal void QueueLine(string line)
    {
        _pendingTranscriptLines.Enqueue(line);

        // 既に引き取りが予約されているなら、積むだけで済ませる。
        // これが無いと 1 行ごとに BeginInvoke が積まれ、間引きの意味が無くなる。
        if (Interlocked.Exchange(ref _transcriptFlushScheduled, 1) == 0)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(FlushLines);
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
    private void FlushLines()
    {
        Interlocked.Exchange(ref _transcriptFlushScheduled, 0);

        var batch = new List<string>();
        while (_pendingTranscriptLines.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        AppendLiveTranscriptLines(LiveTranscriptLines, batch, MaxLiveTranscriptLines);
    }

    /// <summary>
    /// 表示を空にする（REQ-LIVEVIEW-08）。録音の開始に**成功した後**に親が呼ぶ（UI スレッド）。
    /// </summary>
    /// <remarks>
    /// 引き取り待ちのキュー（REQ-LIVEVIEW-09）も一緒に空にする。消し忘れると
    /// 前のセッションの行がクリアの直後に画面へ現れる。字幕帯（REQ-LIVEVIEW-10）も空にする。
    /// </remarks>
    internal void Clear()
    {
        _pendingTranscriptLines.Clear();
        LiveTranscriptLines.Clear();
        ClearProvisional();
    }

    // --- 字幕帯（T187 / REQ-LIVEVIEW-10）---
    // 行一覧（LiveTranscriptLines）とは別のコレクションに出す。行一覧の中で差し替えると、表示上限で古い行を捨てる処理・
    // まとめて追加する処理・スクロールを後回しにするクラッシュ対策（REQ-LIVEVIEW-04 / 06 / 07 / 09）のいずれとも衝突するため。

    /// <summary>字幕帯に出す速報の上限（REQ-LIVEVIEW-10）。</summary>
    internal const int MaxProvisionalLines = 4;

    /// <summary>字幕帯に出す速報（<c>[ラベル] 本文</c>）。届いた順で、最大 <see cref="MaxProvisionalLines"/> 件。</summary>
    public ObservableCollection<string> ProvisionalLines { get; } = new();

    /// <summary>字幕帯の中身（<see cref="ProvisionalLines"/> はこれを写したもの）。UI スレッドだけが触る。</summary>
    private readonly List<ProvisionalEntry> _provisionalEntries = [];

    /// <summary>音源ごとの確定位置（REQ-TRX-LIVE-18）。UI スレッドだけが触る。</summary>
    private readonly Dictionary<AudioSourceType, long> _confirmedPositions = [];

    /// <summary>受け付けるセッション ID。<c>null</c> ならどの通知も受け付けない（録音していない）。</summary>
    private long? _provisionalSessionId;

    /// <summary>字幕帯の 1 件。同じ <see cref="Source"/> と <see cref="PhraseId"/> は同じ句。</summary>
    internal sealed record ProvisionalEntry(AudioSourceType Source, int PhraseId, long StartPosition, long EndPosition, string Display);

    /// <summary>字幕帯の 1 件の表示（<c>[ラベル] 本文</c>。行一覧の行頭のラベルと同じ書き方）。</summary>
    internal static string ProvisionalDisplay(ProvisionalUpdate update) => $"[{update.Label}] {update.Text}";

    /// <summary>
    /// 速報 1 件を字幕帯へ反映する（REQ-LIVEVIEW-10）。反映したら <c>true</c>。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>受け付けているセッションと違えば捨てる（前のセッションの通知）</item>
    /// <item>その音源の確定位置以下で終わる速報は捨てる（確定の行がもう出ている）</item>
    /// <item>同じ音源・句 ID の速報は<b>その場で</b>上書きする。新しい句は末尾に足し、上限を超えたら古い方から捨てる</item>
    /// </list>
    /// </remarks>
    internal static bool ApplyProvisionalUpdate(
        List<ProvisionalEntry> entries,
        Dictionary<AudioSourceType, long> confirmed,
        long? sessionId,
        ProvisionalUpdate update,
        int maxEntries)
    {
        if (sessionId != update.SessionId)
        {
            return false;
        }

        if (confirmed.TryGetValue(update.Source, out var confirmedEnd) && update.EndPosition <= confirmedEnd)
        {
            return false;
        }

        var entry = new ProvisionalEntry(
            update.Source, update.PhraseId, update.StartPosition, update.EndPosition, ProvisionalDisplay(update));
        int index = entries.FindIndex(e => e.Source == update.Source && e.PhraseId == update.PhraseId);
        if (index >= 0)
        {
            entries[index] = entry;
            return true;
        }

        entries.Add(entry);
        while (entries.Count > maxEntries)
        {
            entries.RemoveAt(0);
        }

        return true;
    }

    /// <summary>
    /// 確定完了の通知（REQ-TRX-LIVE-18）を字幕帯へ反映する。その音源の確定位置を進め、
    /// <b>終端が確定位置以下の速報を消す</b>（確定位置をまたぐ速報は残す）。反映したら <c>true</c>。
    /// </summary>
    internal static bool ApplyChunkCompletion(
        List<ProvisionalEntry> entries,
        Dictionary<AudioSourceType, long> confirmed,
        long? sessionId,
        LiveChunkCompletion completion)
    {
        if (sessionId != completion.SessionId)
        {
            return false;
        }

        long end = confirmed.TryGetValue(completion.Source, out var previous)
            ? Math.Max(previous, completion.EndPosition)
            : completion.EndPosition;
        confirmed[completion.Source] = end;
        entries.RemoveAll(e => e.Source == completion.Source && e.EndPosition <= end);
        return true;
    }

    /// <summary>
    /// 表示用のコレクションを <paramref name="source"/> と同じ並びにする。同じ位置で同じ文字列なら触らない
    /// （字幕帯は毎秒更新されるので、変わらない項目の再生成でちらつかせない）。
    /// </summary>
    internal static void SyncLines(IList<string> target, IReadOnlyList<string> source)
    {
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count)
            {
                if (!string.Equals(target[i], source[i], StringComparison.Ordinal))
                {
                    target[i] = source[i];
                }
            }
            else
            {
                target.Add(source[i]);
            }
        }
    }

    /// <summary>
    /// <see cref="TranscriptionService.ProvisionalTranscribed"/> の受け口。**速報スレッドから呼ばれる。** ここで UI に触れない（NFR-01）。
    /// </summary>
    internal void QueueProvisional(ProvisionalUpdate update)
        => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ApplyProvisionalUpdate(
                    _provisionalEntries, _confirmedPositions, _provisionalSessionId, update, MaxProvisionalLines))
            {
                RefreshProvisionalLines();
            }
        });

    /// <summary>
    /// <see cref="TranscriptionService.LiveChunkCompleted"/> の受け口。**文字起こしワーカースレッドから呼ばれる。** ここで UI に触れない（NFR-01）。
    /// </summary>
    internal void QueueChunkCompletion(LiveChunkCompletion completion)
        => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (ApplyChunkCompletion(_provisionalEntries, _confirmedPositions, _provisionalSessionId, completion))
            {
                RefreshProvisionalLines();
            }
        });

    /// <summary>
    /// 録音の開始に成功した後に親が呼ぶ（UI スレッド）。字幕帯を空にし、このセッションの通知だけを受け付ける（REQ-LIVEVIEW-08）。
    /// </summary>
    internal void BeginProvisionalSession(long sessionId)
    {
        ClearProvisional();
        _provisionalSessionId = sessionId;
    }

    /// <summary>
    /// 録音の停止が完了した後に親が呼ぶ（UI スレッド）。字幕帯を空にし、どの通知も受け付けない（REQ-TRX-LIVE-11）。
    /// </summary>
    internal void EndProvisionalSession()
    {
        ClearProvisional();
        _provisionalSessionId = null;
    }

    private void ClearProvisional()
    {
        _provisionalEntries.Clear();
        _confirmedPositions.Clear();
        ProvisionalLines.Clear();
    }

    private void RefreshProvisionalLines()
        => SyncLines(ProvisionalLines, _provisionalEntries.ConvertAll(e => e.Display));
}