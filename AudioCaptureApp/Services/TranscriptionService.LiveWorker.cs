using System.Diagnostics;
using System.IO;
using System.Text;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ライブの確定パスのワーカー（WhisperTranscription スレッド）を担当する部分。
// チャンクの取り出しと区切りの判定（REQ-TRX-LIVE-04 / 10 / 13）・Whisper の呼び出し・追記・確定完了の通知（REQ-TRX-LIVE-18）（T193 で切り出した）。
public partial class TranscriptionService
{
    private void TranscriptionLoop()
    {
        var token = _cts!.Token;

        while (_isRunning)
        {
            try
            {
                token.WaitHandle.WaitOne(1000);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            // ギャップで確定したチャンクが複数溜まっていることがあるため、
            // 1 tick で 1 つではなく取り出せるだけ処理する。ただし音源ごとに手番を回す（REQ-TRX-LIVE-04 / T204）。
            // 1 つの音源を取り出せる限り処理し続けると、実時間より遅れている音源から抜けられず、
            // もう一方の音源が録音の間ずっと処理されない。
            // _isRunning は必ず見ること。これが無いと停止要求後もバックログを全部捌き切るまで抜けず、
            // StopSession がタイムアウトする（T117）。
            ProcessRoundRobin(
                _sources.Values.ToList(),
                state =>
                {
                    var chunk = TakeNextChunk(
                        state, _sessionClock.Elapsed, SilenceCut, LiveChunking, PendingBufferedSamples());
                    if (chunk == null)
                    {
                        return false;
                    }

                    // 停止要求が来ても、取り出したチャンクは最後の区間まで処理する（T184）。
                    // バッファからは既に消えているので、途中で抜けると残りの区間を捨てることになる。
                    ProcessChunkCounted(chunk, state, token);
                    return true;
                },
                () => _isRunning && !token.IsCancellationRequested,
                RoundRobinTurnBudget,
                () => _sessionClock.Elapsed);
        }

        // 残りバッファを処理（キャンセルされていなければ）
        if (!token.IsCancellationRequested)
        {
            foreach (var (_, state) in _sources)
            {
                PendingChunk? chunk;
                while (!token.IsCancellationRequested
                       && (chunk = TakeNextChunk(
                           state, _sessionClock.Elapsed, SilenceCut, LiveChunking, PendingBufferedSamples())) != null)
                {
                    // 排出処理。打ち切りたいときは token をキャンセルする（StopSession の「打ち切り」）。
                    ProcessChunkCounted(chunk, state, token);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                PendingChunk? tail = null;
                lock (state.BufferLock)
                {
                    if (state.Pcm16kBuffer.Count >= MinTailSamples)
                    {
                        tail = new PendingChunk(
                            state.Pcm16kBuffer.ToArray(),
                            ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count),
                            state.BufferStartPosition);
                    }
                    state.BufferStartPosition += state.Pcm16kBuffer.Count;
                    state.Pcm16kBuffer.Clear();
                }

                if (tail != null)
                {
                    ProcessChunkCounted(tail, state, token);
                }
            }
        }
    }

    /// <summary>1 つの音源が 1 回の手番で処理を続けてよい時間の目安（REQ-TRX-LIVE-04 / T204）。</summary>
    internal static readonly TimeSpan RoundRobinTurnBudget = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 音源を順に回し、どの音源からも取り出せなくなるまで繰り返す（REQ-TRX-LIVE-04 / T204）。
    /// 1 回の手番では、少なくとも 1 チャンク、使った時間が <paramref name="turnBudget"/> に達するまで続けて処理する。
    /// </summary>
    /// <remarks>
    /// 発話のチャンクは 1 回で手番の時間を使い切るので、発話どうしなら 1 チャンクずつ交代する。
    /// 無音のチャンクは Whisper を呼ばずほぼ 0 秒で済むので、溜まった分を 1 回の手番で片付けられる。
    /// 1 チャンクずつ厳密に交代すると、無音の音源が重い音源と同じ速さでしか進めず、停止時の残りに無音が積み上がる（T204 の実測）。
    /// </remarks>
    /// <param name="sources">音源（登録順）。</param>
    /// <param name="processOne">その音源から 1 チャンク取り出して処理する。取り出せなければ <c>false</c>。</param>
    /// <param name="shouldContinue">続けてよいか（停止要求・キャンセルで <c>false</c>）。1 チャンクごとに見る。</param>
    /// <param name="turnBudget">1 回の手番で処理を続けてよい時間。</param>
    /// <param name="now">時計（テストで差し替える）。</param>
    internal static void ProcessRoundRobin<T>(
        IReadOnlyList<T> sources, Func<T, bool> processOne, Func<bool> shouldContinue,
        TimeSpan turnBudget, Func<TimeSpan> now)
    {
        bool tookAny = true;
        while (tookAny)
        {
            tookAny = false;
            foreach (var source in sources)
            {
                var turnStart = now();
                while (true)
                {
                    if (!shouldContinue())
                    {
                        return;
                    }

                    if (!processOne(source))
                    {
                        break;
                    }

                    tookAny = true;
                    if (now() - turnStart >= turnBudget)
                    {
                        break;
                    }
                }
            }
        }
    }

    /// <summary>処理中のチャンクのサンプル数（<see cref="PendingSeconds"/> に含めるため）。</summary>
    private long _inFlightSamples;

    /// <summary><see cref="ProcessChunk"/> を、処理中のサンプル数を数えながら呼ぶ。</summary>
    private void ProcessChunkCounted(PendingChunk chunk, SourceState state, CancellationToken token)
    {
        // 計測ログ用（REQ-CFG-12）。取り出した直後の滞留で、TakeNextChunk と同じ判定をする
        bool behind = PendingBufferedSamples() >= BacklogSuppressEndpointingSamples;
        Interlocked.Exchange(ref _inFlightSamples, chunk.Samples.Length);
        try
        {
            ProcessChunk(chunk, state, behind, token);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlightSamples, 0);
        }
    }

    /// <summary>
    /// 確定済みチャンクを 1 つ取り出す。無ければバッファから 1 チャンク切り出す。
    /// どちらも無ければ <c>null</c>。
    /// </summary>
    /// <param name="options">有声判定の閾値（<see cref="SilenceCutOptions.RmsThreshold"/>）だけを使う。</param>
    /// <param name="chunking">
    /// 遅れていないときの上限と発話終了の無音（REQ-CFG-15）。<see cref="SilenceCutOptions.MergeGapSeconds"/> は使わない（ADR-0009）。
    /// </param>
    /// <param name="pendingSamples">
    /// 全ソースの確定待ちの合計（<see cref="PendingBufferedSamples"/>）。遅れているかの判定に使う（REQ-TRX-LIVE-13）。
    /// </param>
    /// <remarks>
    /// 遅れの判定に <paramref name="state"/> 自身の分だけを使ってはいけない（T183）。確定済みチャンクは
    /// 先に返すので判定時には残っておらず、未確定バッファも上限（<paramref name="chunking"/> の上限または 20 秒分）以上なら先に上限で切り出すため、
    /// 判定時の自ソース分は常に 20 秒未満で、60 秒の閾値に届かない。
    /// <paramref name="pendingSamples"/> はこの錠の外で数えて渡す。中で数えると
    /// <see cref="PendingSeconds"/>（<c>_sourcesLock</c> → 各ソースの錠）と錠の順序が逆になる。
    /// </remarks>
    internal static PendingChunk? TakeNextChunk(
        SourceState state, TimeSpan nowElapsed, SilenceCutOptions options, LiveChunkOptions chunking, long pendingSamples)
    {
        lock (state.BufferLock)
        {
            if (state.Ready.Count > 0)
            {
                return state.Ready.Dequeue();
            }

            if (state.Pcm16kBuffer.Count == 0)
            {
                return null;
            }

            // REQ-TRX-LIVE-13: 遅れているときは末尾無音での早期確定を使わない。
            // 20 秒に満たないチャンクは 1 回あたりの効率が悪く（実測: 音声 1 秒あたり
            // 20 秒入力 0.181 秒 / 5 秒入力 0.381 秒）、遅れをさらに広げるため。
            var start = ChunkStartElapsed(state.BufferEndElapsed, state.Pcm16kBuffer.Count);
            int take = ChunkTakeCount(
                state.Pcm16kBuffer.Count,
                nowElapsed - state.BufferEndElapsed,
                TrailingSilenceSamples(state.Pcm16kBuffer, options.RmsThreshold),
                SecondsToSamples(chunking.EndpointSilenceSeconds),
                SecondsToSamples(chunking.MaxChunkSeconds),
                pendingSamples >= BacklogSuppressEndpointingSamples);
            if (take == 0)
            {
                return null;
            }

            var samples = new float[take];
            state.Pcm16kBuffer.CopyTo(0, samples, 0, take);
            state.Pcm16kBuffer.RemoveRange(0, take);
            var chunk = new PendingChunk(samples, start, state.BufferStartPosition);
            state.BufferStartPosition += take;
            return chunk;
        }
    }

    /// <summary>これ以上の滞留があれば「遅れている」とみなし、効率を優先する（REQ-TRX-LIVE-13）。</summary>
    internal const int BacklogSuppressEndpointingSamples = TargetRate * 60;

    /// <summary>
    /// バッファから今回切り出すサンプル数を決める。<c>0</c> ならまだ切り出さない。
    /// </summary>
    /// <param name="bufferedSampleCount">バッファに溜まっているサンプル数（16kHz）。</param>
    /// <param name="supplyIdle">最後にサンプルを受け取ってからの経過時間。</param>
    /// <param name="trailingSilenceSamples">
    /// 末尾の連続無音サンプル数。バッファ全体が無音なら <c>null</c>
    /// （<see cref="TrailingSilenceSamples"/> の戻り値）。
    /// </param>
    /// <param name="endpointSilenceSamples">
    /// 発話が終わったとみなす末尾無音の長さ（<see cref="LiveChunkOptions.EndpointSilenceSeconds"/>。REQ-TRX-LIVE-13）。
    /// </param>
    /// <param name="liveChunkSamples">
    /// 遅れていないときの上限（<see cref="LiveChunkOptions.MaxChunkSeconds"/>。REQ-TRX-LIVE-10）。
    /// <see cref="BufferThresholdSamples"/> より大きければ <see cref="BufferThresholdSamples"/> で切る。
    /// </param>
    /// <param name="behind">
    /// 文字起こしが遅れているか（全ソースの滞留が <see cref="BacklogSuppressEndpointingSamples"/> 以上。REQ-TRX-LIVE-13）。
    /// </param>
    /// <remarks>
    /// 契機は 3 つあり、この優先順で判定する。
    /// <list type="number">
    /// <item>上限までたまった → 上限の分だけ切り出す（1 回の Whisper 呼び出しを
    /// 際限なく長くしないための上限。T117）。上限は遅れていなければ <paramref name="liveChunkSamples"/>（既定 6 秒分。
    /// T190 で 20 → 10 秒、ADR-0009 で 10 → 6 秒）、遅れていれば <see cref="BufferThresholdSamples"/>（20 秒分）。</item>
    /// <item>末尾に <paramref name="endpointSilenceSamples"/> 以上の無音が積まれ、かつ
    /// バッファ内に有声窓がある → 発話が終わったとみなしてバッファ全部を切り出す（T129）。</item>
    /// <item>供給が <see cref="StaleSupplyIdle"/> 以上途絶えている → バッファ全部を切り出す（T120）。</item>
    /// </list>
    /// <para>
    /// <paramref name="behind"/>（遅れている）のときは、1 の上限を 20 秒分に戻し、2 を使わない（REQ-TRX-LIVE-13）。
    /// どちらも 1 回あたりの効率を優先するためである。
    /// </para>
    /// <para>
    /// 2 が無いと、マイクは無音でも WASAPI がサンプルを供給し続けるため
    /// ギャップ分割（<see cref="ShouldSplitOnGap"/>）も 3 も発火せず、出力粒度が上限で固定になる。
    /// 保持時間は <see cref="SplitVoicedRegions"/> の <see cref="SilenceCutOptions.MergeGapSeconds"/>（2.0 秒）とは別の値
    /// （<see cref="LiveChunkOptions.EndpointSilenceSeconds"/>、既定 0.8 秒）である（ADR-0009）。以前は揃えて
    /// 確定チャンクが有声区間ちょうど 1 個を含む形にしていたが、話し終えてから確定まで 2 秒待つことになるため、
    /// ライブだけ短くした。0.8〜2.0 秒の間を挟む発話は別々のチャンクに分かれうる。
    /// </para>
    /// <para>
    /// バッファ全体が無音（<paramref name="trailingSilenceSamples"/> が <c>null</c>）なら
    /// 2 では確定しない。1 で切り出され、有声区間 0 件として Whisper を呼ばずに捨てられる。
    /// </para>
    /// <para>
    /// 3 が無いと、ミュートや再生停止で供給が止まったソースのバッファは
    /// 「次のパケットが来てギャップ分割が発火するまで」書き出されない。
    /// 実測では 16 秒分の音声が 57 秒間放置され、その間に他ソースが書き進んだため
    /// 出力行の時刻が前後して見えていた。
    /// </para>
    /// </remarks>
    internal static int ChunkTakeCount(
        int bufferedSampleCount,
        TimeSpan supplyIdle,
        int? trailingSilenceSamples,
        int endpointSilenceSamples,
        int liveChunkSamples,
        bool behind = false)
    {
        // REQ-TRX-LIVE-10: 遅れていなければ設定の上限（既定 6 秒）、遅れていれば 20 秒で切る（T190 / ADR-0009）。
        // 設定は LiveChunkOptions で 2〜20 秒に収めてあるが、0 以下で毎回 0 サンプルを返す事故を防ぐため、ここでも収める。
        int limit = behind
            ? BufferThresholdSamples
            : Math.Clamp(liveChunkSamples, MinWhisperSamples, BufferThresholdSamples);
        if (bufferedSampleCount >= limit)
        {
            return limit;
        }

        // REQ-TRX-LIVE-13: 遅れているときは②を使わない。20 秒たまるまで待って効率を優先する。
        // ③（供給の途絶）は残す — 供給が止まったソースを無期限に抱え込まないための契機であり、
        // 遅れているかどうかとは関係がない。
        if (behind)
        {
            return supplyIdle >= StaleSupplyIdle && bufferedSampleCount >= MinTailSamples
                ? bufferedSampleCount
                : 0;
        }

        // null（＝全体が無音）はここで確定しない。1 で切り出され、有声区間 0 件として捨てられる。
        // 末尾無音が測れている＝有声窓が 1 つ以上あるので、最小長は自動的に満たす
        // （有声 100ms + 無音 > 0.2 秒）ため MinTailSamples は見ない。
        // 0 サンプルを弾くのは、LiveEndpointSilenceSeconds に 0 を設定されたときに
        // 有声のまま（発話の途中で）毎ポーリング確定してしまうのを防ぐため。
        if (trailingSilenceSamples is int trailingSilence
            && trailingSilence > 0 && trailingSilence >= endpointSilenceSamples)
        {
            return bufferedSampleCount;
        }

        // 0.2 秒未満の断片は 1 回の推論に見合わないため、セッション終了まで持ち越す
        if (supplyIdle >= StaleSupplyIdle && bufferedSampleCount >= MinTailSamples)
        {
            return bufferedSampleCount;
        }

        return 0;
    }

    /// <summary>
    /// チャンクを有声区間に分けて Whisper に掛ける。
    /// </summary>
    /// <remarks>
    /// 区間ループは停止要求（<see cref="_isRunning"/> が false）では抜けず、キャンセルでだけ抜ける（T184）。
    /// チャンクは <see cref="TakeNextChunk"/> でバッファから取り除き済みなので、途中で抜けると
    /// 残りの区間はどこにも残らず捨てられる。かつて（T127）は <see cref="StopSession"/> の猶予 30 秒を
    /// 超えないよう停止要求で抜けていたが、T165 で猶予を無くして「捨てない」にしたため不要になった。
    /// <para>
    /// 処理し終えたら、結末（0 行・キャンセル・例外）によらず <see cref="LiveChunkCompleted"/> を発火する（REQ-TRX-LIVE-18）。
    /// 発火しないと、その位置までの速報が字幕帯から消えずに残る。
    /// </para>
    /// </remarks>
    private void ProcessChunk(
        PendingChunk chunk, SourceState state, bool behind, CancellationToken token)
    {
        // results は try の外で宣言する。中で宣言すると、Whisper がキャンセル例外を投げたときに
        // 確定済みの区間の行まで一緒に捨てられる。それらの行は TranscribeRegion の中で
        // SegmentTranscribed により画面へ出た後なので、捨てると画面と .txt が食い違う（T126）。
        var results = new List<string>();
        try
        {
            // 無音は Whisper に渡さない（ハルシネーション防止）。
            // 時刻は区間自身が持つため、無音を捨てても後続の時刻はずれない。
            foreach (var region in SplitVoicedRegions(chunk.Samples, SilenceCut))
            {
                // ここで _isRunning を見てはいけない（T184。remarks を参照）。
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var regionStart = RegionStart(chunk.StartElapsed, region.Start);
                var samples = PadToMinimum(SliceRegion(chunk.Samples, region), MinWhisperSamples);
                long callStarted = Stopwatch.GetTimestamp();
                int segments = TranscribeRegion(state, samples, regionStart, results, token);
                _metrics?.WriteFinal(
                    _sessionClock.Elapsed, state.Type, chunk.StartPosition + region.Start, region.Length,
                    Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds, segments, behind, PendingSeconds);
            }
        }
        // CA1031: ワーカースレッド境界＋Whisper のネイティブ処理。例外を漏らすとプロセスごと
        //         落ちて録音中のセッションを失うため、全例外を Error イベントに変換する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            // キャンセルによる中断は正常終了扱い。推論中にキャンセルすると Whisper.net は
            // WhisperProcessingException を投げることがあるので、型ではなくトークンで判定する（T192）
            if (ShouldReportChunkError(ex, token.IsCancellationRequested))
            {
                Error?.Invoke($"文字起こしエラー: {ex.Message}");
            }
        }
#pragma warning restore CA1031

        // 追記は catch の後ろに置く（finally ではない）。finally に置くと、ここで起きた
        // IOException が上の catch 節を通らずに TranscriptionLoop まで抜け、
        // ワーカースレッドごと落ちる。
        var failure = AppendTranscriptLines(_outputPath, results);
        if (failure != null)
        {
            Error?.Invoke($"文字起こし結果の書き出しに失敗しました: {failure}");
        }

        // REQ-TRX-LIVE-18: 結末によらず通知する。try の外なので、キャンセル・例外の後も必ず通る。
        MarkProvisionalConfirmed(state, chunk.EndPosition);
        LiveChunkCompleted?.Invoke(new LiveChunkCompletion(CurrentSessionId, state.Type, chunk.EndPosition));
    }

    /// <summary>
    /// 確定パスのチャンクの処理で起きた例外を利用者に知らせるか（REQ-TRX-LIVE-11、ハーネス §3 規則 7）。
    /// キャンセル済みなら型によらず知らせない — 打ち切りは利用者が選んだ操作であり、エラーではない。
    /// </summary>
    /// <remarks>
    /// Whisper.net は推論の途中でキャンセルされると <see cref="OperationCanceledException"/> ではなく
    /// <c>WhisperProcessingException</c> を投げることがあり、メッセージも中断した段階で変わる
    /// （Failed to encode / decode audio features、Failed to decode during token processing。T182・T192 の実測）。
    /// </remarks>
    internal static bool ShouldReportChunkError(Exception exception, bool canceled)
        => !canceled && exception is not OperationCanceledException;

    /// <summary>
    /// 確定した行をテキストファイルへ追記する（REQ-TRX-07）。
    /// </summary>
    /// <returns>成功したら <c>null</c>。失敗したらユーザー向けの理由。</returns>
    /// <remarks>
    /// 失敗を例外ではなく戻り値で返すのは、呼び出し元（<see cref="ProcessChunk"/>）が
    /// キャンセル・Whisper の例外を処理し終えた **後** にここを通るためである。
    /// 例外で返すとワーカースレッドの境界を越えてしまう。
    /// </remarks>
    internal static string? AppendTranscriptLines(string outputPath, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        try
        {
            File.AppendAllLines(outputPath, lines, Encoding.UTF8);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 有声区間 1 つを Whisper に掛け、整形した行を results へ積む。
    /// regionStart は区間先頭の、セッション開始からの経過時間。
    /// </summary>
    /// <returns>積んだ行の数（計測ログ用）。</returns>
    /// <remarks>
    /// 文脈プロンプト（REQ-TRX-LIVE-19）があるときは、この呼び出しのためだけの processor を作って破棄する。
    /// 無ければ音源ごとの作り置きの processor を使う（今までと同じ）。確定行の行頭の付け札は落とす（REQ-TRX-LIVE-20）。
    /// </remarks>
    private int TranscribeRegion(
        SourceState state, float[] samples, TimeSpan regionStart,
        List<string> results, CancellationToken token)
    {
        int count = 0;
        var prompt = ContextPromptFor(regionStart);
        using var prompted = prompt == null
            ? null
            : WithLanguageOption(_factory!.CreateBuilder(), _sessionLanguage).WithPrompt(prompt).Build();
        var processor = prompted ?? state.Processor!;

        // ProcessAsync を同期的に消費
        var asyncEnum = processor.ProcessAsync(samples, token);
        var enumerator = asyncEnum.GetAsyncEnumerator(token);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var segment = enumerator.Current;
                var text = StripSpeakerTag(segment.Text?.Trim() ?? "");
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                RememberConfirmed(regionStart + segment.Start, regionStart + segment.End, text);
                var startTime = _sessionStartTime + regionStart + segment.Start;
                var endTime = _sessionStartTime + regionStart + segment.End;
                var line = $"[{startTime:HH:mm:ss} - {endTime:HH:mm:ss}] [{state.Label}] {text}";
                results.Add(line);
                count++;
                SegmentTranscribed?.Invoke(line);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return count;
    }
}