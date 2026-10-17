namespace AudioCaptureApp.Services;

/// <summary>
/// 速報 1 回分の依頼（REQ-TRX-LIVE-16）。位置は音源ごとの絶対位置（REQ-TRX-LIVE-18 と同じ単位）。
/// </summary>
/// <param name="PhraseId">句の番号。同じ番号の依頼は同じ句の更新である。</param>
/// <param name="Start">句の先頭。</param>
/// <param name="End">今回デコードする範囲の終端（末尾の無音は含めない）。</param>
/// <param name="Closing">この依頼で句を閉じるか。</param>
internal sealed record ProvisionalRequest(int PhraseId, long Start, long End, bool Closing);

/// <summary>
/// 速報の区切り（REQ-TRX-LIVE-16、T181 D9）。句単位で、呼び出すたびにデコードするのは今の句だけ。
/// </summary>
/// <remarks>
/// <para>
/// T182 の計測ハーネス（<c>PhrasePlanner</c>）で合格した規則をそのまま移したもの。違いは、音声全体を先に持たず
/// 100ms 窓の有声判定を <see cref="AppendWindow"/> で 1 つずつ受け取ることと、位置が <c>long</c> の絶対位置であること。
/// </para>
/// <list type="bullet">
/// <item>句は最初の有声窓の 0.2 秒前から始まる</item>
/// <item>有声の後に 0.6 秒以上の無音が来るか、句の上限（既定 8 秒）に達したら閉じて次の句へ移る（上限で閉じたら続きは切った位置から始まる）。
/// 無音は句の末尾だけでなく途中も見る（ハーネスは末尾だけを見ていたため、呼び出しの合間に収まった間を見逃した。T186 D12）</item>
/// <item>末尾の無音は削ってから渡す</item>
/// <item>前回の呼び出しから新しい有声が 0.3 秒未満なら呼ばない。句が 1 秒未満なら呼ばない</item>
/// </list>
/// <para>
/// 速報スレッドだけが触る（錠は持たない）。
/// </para>
/// </remarks>
internal sealed class ProvisionalPhrasePlanner
{
    /// <summary>有声判定の窓（100ms。REQ-TRX-06 と同じ）。</summary>
    internal const int WindowSamples = Rate / 10;

    private const int Rate = 16000;

    /// <summary>句の先頭を最初の有声窓より前へ広げる長さ（0.2 秒）。</summary>
    internal const int LeadSamples = Rate / 5;

    /// <summary>句を閉じる末尾の無音（0.6 秒）。</summary>
    internal const int CloseSilenceSamples = Rate * 6 / 10;

    /// <summary>前回の呼び出しからこれだけ新しい有声が無ければ呼ばない（0.3 秒）。</summary>
    internal const int MinNewVoicedSamples = Rate * 3 / 10;

    /// <summary>句がこれより短ければ呼ばない（1 秒）。</summary>
    internal const int MinPhraseSamples = Rate;

    private readonly long _maxPhraseSamples;

    /// <summary>窓ごとの有声判定。先頭は <see cref="_voicedBase"/> 番目の窓。</summary>
    private readonly List<bool> _voiced = [];

    /// <summary><see cref="_voiced"/>[0] が何番目の窓か（捨てた分だけ進む）。</summary>
    private long _voicedBase;

    /// <summary>次の句が始まってよい最も早い位置（前の句の終わり・確定済みの位置）。</summary>
    private long _floor;

    /// <summary>有声窓を探す位置（窓の番号）。</summary>
    private long _searchW;

    /// <summary>今の句の先頭。句が無ければ -1。</summary>
    private long _phraseStart = -1;

    private int _phraseId;

    /// <summary>今の句で、前回の呼び出しまでに渡した有声窓の数。</summary>
    private int _decodedVoicedWindows;

    /// <param name="maxPhraseSeconds">句の上限（秒）。窓の境界へ切り下げる。</param>
    public ProvisionalPhrasePlanner(double maxPhraseSeconds)
    {
        long samples = (long)(maxPhraseSeconds * Rate);
        _maxPhraseSamples = Math.Max(WindowSamples, samples / WindowSamples * WindowSamples);
    }

    /// <summary>有声判定が済んだ窓の数（＝判定済みの音声の終端 ÷ 窓の長さ）。</summary>
    public long KnownWindows => _voicedBase + _voiced.Count;

    private long KnownEnd => KnownWindows * WindowSamples;

    /// <summary>
    /// これより前の音声はもう要らない（今の句の先頭。句が無ければ次の句が始まりうる最も早い位置）。
    /// 呼び出し側はここより前の写しを捨ててよい（T186 D3）。
    /// </summary>
    public long KeepFrom => _phraseStart >= 0
        ? _phraseStart
        : Math.Max(_floor, Math.Max(0, (_searchW * WindowSamples) - LeadSamples));

    /// <summary>次の窓（<see cref="KnownWindows"/> 番目）の有声判定を足す。</summary>
    public void AppendWindow(bool voiced) => _voiced.Add(voiced);

    /// <summary><paramref name="position"/> を含む窓より前の判定を捨てる（メモリを溜めないため）。</summary>
    public void TrimBefore(long position)
    {
        long count = Math.Min(_voiced.Count, (position / WindowSamples) - _voicedBase);
        if (count > 0)
        {
            _voiced.RemoveRange(0, (int)count);
            _voicedBase += count;
        }
    }

    /// <summary>
    /// <paramref name="position"/> より前の音声には速報を出さない（確定済み。T186 D4）。
    /// 句が開いていなければ探し始めをそこまで進める。開いている句の有声がすべてそこより前なら、その句を閉じる。
    /// 句がその位置をまたいでいれば何もしない（字幕帯では確定の位置をまたぐ速報は残る。REQ-LIVEVIEW-10）。
    /// </summary>
    public void SkipBefore(long position)
    {
        if (_phraseStart < 0)
        {
            if (position > _floor)
            {
                _floor = position;
            }
            return;
        }

        if (position <= _phraseStart || position < LastVoicedEndOfPhrase())
        {
            return;
        }

        _floor = Math.Max(_floor, position);
        _searchW = Math.Max(_searchW, _floor / WindowSamples);
        _phraseStart = -1;
    }

    /// <summary>今の句の中で、判定済みの最後の有声窓の終わり。有声窓が無ければ句の先頭。</summary>
    private long LastVoicedEndOfPhrase()
    {
        long limitW = Math.Min(KnownWindows, (_phraseStart + _maxPhraseSamples) / WindowSamples);
        for (long w = limitW - 1; w >= _phraseStart / WindowSamples; w--)
        {
            if (IsVoiced(w))
            {
                return (w + 1) * WindowSamples;
            }
        }

        return _phraseStart;
    }

    private bool IsVoiced(long window)
        => window >= _voicedBase && window < KnownWindows && _voiced[(int)(window - _voicedBase)];

    /// <summary>
    /// 判定済みの音声までを見て、デコードすべき依頼を返す。閉じた句があれば先にそれを返す（複数ありうる）。
    /// </summary>
    public List<ProvisionalRequest> Plan()
    {
        var list = new List<ProvisionalRequest>();
        long availWindows = KnownWindows;
        long avail = KnownEnd;
        while (true)
        {
            if (_phraseStart < 0)
            {
                long w = Math.Max(_searchW, _floor / WindowSamples);
                while (w < availWindows && !IsVoiced(w))
                {
                    w++;
                }
                _searchW = w;
                if (w >= availWindows)
                {
                    return list;
                }

                _phraseStart = Math.Max(_floor, (w * WindowSamples) - LeadSamples);
                _decodedVoicedWindows = 0;
                _phraseId++;
            }

            long startW = _phraseStart / WindowSamples;
            long maxEnd = _phraseStart + _maxPhraseSamples;
            bool hitMax = avail >= maxEnd;
            long limitW = hitMax ? maxEnd / WindowSamples : availWindows;

            // 有声の後に 0.6 秒以上の無音があれば、そこで句を閉じる。末尾だけでなく句の途中も見る —
            // 末尾だけだと、呼び出しの合間に収まった間（ま）を見逃して句が閉じない（T186 D12）
            long lastVoicedEndW = -1;
            int voicedCount = 0;
            bool silenceClosed = false;
            for (long w = startW; w < limitW; w++)
            {
                if (IsVoiced(w))
                {
                    lastVoicedEndW = w + 1;
                    voicedCount++;
                }
                else if (lastVoicedEndW >= 0 && (w + 1 - lastVoicedEndW) * WindowSamples >= CloseSilenceSamples)
                {
                    silenceClosed = true;
                    break;
                }
            }

            if (lastVoicedEndW < 0)
            {
                // 句の中に有声窓がまだ無い（前置きだけ）。上限に達していたら諦めて次へ、そうでなければ待つ
                if (hitMax)
                {
                    _floor = maxEnd;
                    _phraseStart = -1;
                    continue;
                }
                return list;
            }

            long voicedEnd = Math.Min(lastVoicedEndW * WindowSamples, hitMax ? maxEnd : avail);
            bool closing = silenceClosed || hitMax;

            bool longEnough = voicedEnd - _phraseStart >= MinPhraseSamples;
            bool enoughNew = (long)(voicedCount - _decodedVoicedWindows) * WindowSamples >= MinNewVoicedSamples;
            if (longEnough && enoughNew)
            {
                list.Add(new ProvisionalRequest(_phraseId, _phraseStart, voicedEnd, closing));
                _decodedVoicedWindows = voicedCount;
            }

            if (!closing)
            {
                return list;
            }

            // 閉じて次の句へ。無音で閉じたときは有声の終わりから、上限で閉じたときは切った位置から探す
            // （次の句の先頭は max(探し始め, 最初の有声窓 − 0.2 秒) なので、話し続けていれば切った位置から続く）
            _floor = silenceClosed ? lastVoicedEndW * WindowSamples : maxEnd;
            _searchW = _floor / WindowSamples;
            _phraseStart = -1;
        }
    }
}