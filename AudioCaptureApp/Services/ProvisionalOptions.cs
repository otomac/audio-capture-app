namespace AudioCaptureApp.Services;

/// <summary>速報を出す音源（REQ-CFG-12 の <c>ProvisionalSources</c>）。</summary>
public enum ProvisionalSources
{
    /// <summary>スピーカーだけ（既定）。両方に出すと 1 回の更新で 2 回呼ぶことになり、遅れが合格線に届かなかった（T182）。</summary>
    Speaker,

    /// <summary>マイクだけ。</summary>
    Mic,

    /// <summary>両方。</summary>
    Both
}

/// <summary>
/// 速報（REQ-TRX-LIVE-15〜17）の調整値。settings.json から与えられるため、
/// <see cref="SilenceCutOptions"/> と同じく手書きの不正値でも壊れないようコンストラクターでクランプする。
/// </summary>
/// <remarks>
/// 既定値は T182 の実測で合格した組み合わせ（間隔 1 秒・duty の上限 1.0・CPU 4 スレッド・スピーカーだけ・句の上限 8 秒）。
/// 8 スレッドは CPU の p95 が 34〜43% になったため採らなかった（ADR-0007）。
/// </remarks>
public sealed record ProvisionalOptions
{
    private const double DefaultIntervalSeconds = 1.0;
    private const double DefaultDutyCap = 1.0;
    private const int DefaultThreads = 4;
    private const double DefaultMaxPhraseSeconds = 8.0;

    public ProvisionalOptions(
        double intervalSeconds, double dutyCap, int threads, ProvisionalSources sources, double maxPhraseSeconds)
    {
        IntervalSeconds = Sanitize(intervalSeconds, 0.5, MaxIntervalSeconds, DefaultIntervalSeconds);
        DutyCap = Sanitize(dutyCap, 0.1, 1.0, DefaultDutyCap);
        Threads = Math.Clamp(threads, 1, 16);
        Sources = Enum.IsDefined(sources) ? sources : ProvisionalSources.Speaker;
        // 上限 25 秒: audio_ctx（句 + 1 秒）が 1500 フレーム（30 秒）に収まる範囲（REQ-TRX-LIVE-17）
        MaxPhraseSeconds = Sanitize(maxPhraseSeconds, 2.0, 25.0, DefaultMaxPhraseSeconds);
    }

    /// <summary>次の間隔の上限（秒。REQ-TRX-LIVE-16）。duty の上限で伸ばしてもここで止める。</summary>
    public const double MaxIntervalSeconds = 4.0;

    /// <summary>呼び出しの間隔の下限（秒）。処理が追いつかないときはこれより伸びる。</summary>
    public double IntervalSeconds { get; }

    /// <summary>速報に使ってよい時間の割合の上限。</summary>
    public double DutyCap { get; }

    /// <summary>速報用モデルの CPU スレッド数。</summary>
    public int Threads { get; }

    /// <summary>速報を出す音源。</summary>
    public ProvisionalSources Sources { get; }

    /// <summary>句の上限（秒）。これに達したら句を閉じて、続きは切った位置から始める。</summary>
    public double MaxPhraseSeconds { get; }

    public static ProvisionalOptions Default { get; } = new(
        DefaultIntervalSeconds, DefaultDutyCap, DefaultThreads, ProvisionalSources.Speaker, DefaultMaxPhraseSeconds);

    /// <summary>
    /// settings.json の文字列（<c>speaker</c> / <c>mic</c> / <c>both</c>）を解釈する。未知の値・空はスピーカーへ倒す。
    /// </summary>
    public static ProvisionalSources ParseSources(string? value)
    {
        var trimmed = value?.Trim();
        if (string.Equals(trimmed, "mic", StringComparison.OrdinalIgnoreCase))
        {
            return ProvisionalSources.Mic;
        }

        return string.Equals(trimmed, "both", StringComparison.OrdinalIgnoreCase)
            ? ProvisionalSources.Both
            : ProvisionalSources.Speaker;
    }

    /// <summary>この音源に速報を出すか。</summary>
    public bool Targets(AudioSourceType source) => Sources switch
    {
        ProvisionalSources.Both => true,
        ProvisionalSources.Mic => source == AudioSourceType.Mic,
        _ => source == AudioSourceType.Speaker
    };

    // Math.Clamp は NaN をそのまま返すため、先に弾く（SilenceCutOptions と同じ）。
    private static double Sanitize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// 速報 1 件の通知（REQ-TRX-LIVE-15）。同じ <see cref="Source"/> と <see cref="PhraseId"/> の通知は、同じ句の更新である。
/// </summary>
/// <param name="SessionId">録音ごとのセッション ID（<see cref="TranscriptionService.CurrentSessionId"/>）。</param>
/// <param name="Source">音源。</param>
/// <param name="Label">音源のラベル（<c>マイク</c> / <c>スピーカー</c>）。</param>
/// <param name="PhraseId">句の番号（音源ごと・セッションごとに 1 から）。</param>
/// <param name="StartPosition">句の先頭の絶対位置（REQ-TRX-LIVE-18 と同じ単位）。</param>
/// <param name="EndPosition">今回デコードした範囲の終端の絶対位置。</param>
/// <param name="Closing">句を閉じた（これ以上この句の更新は来ない）か。</param>
/// <param name="Text">速報の本文。幻聴の除去（REQ-TRX-LIVE-17）を通ったもの。</param>
public sealed record ProvisionalUpdate(
    long SessionId,
    AudioSourceType Source,
    string Label,
    int PhraseId,
    long StartPosition,
    long EndPosition,
    bool Closing,
    string Text);