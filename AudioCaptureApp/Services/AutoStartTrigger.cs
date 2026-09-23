namespace AudioCaptureApp.Services;

/// <summary>
/// 録音の自動開始（REQ-REC-12）の調整値。settings.json から与えられるため、
/// 手書きされた不正値でも壊れないようコンストラクターで必ずクランプする（REQ-CFG-10）。
/// </summary>
public sealed record AutoStartOptions
{
    public const double DefaultThresholdDb = -15.0;
    public const double DefaultSustainSeconds = 1.0;
    public const double DefaultDipGraceSeconds = 0.3;
    public const double DefaultCooldownSeconds = 10.0;

    public AutoStartOptions(
        double thresholdDb,
        double sustainSeconds,
        double cooldownSeconds,
        double dipGraceSeconds = DefaultDipGraceSeconds)
    {
        ThresholdDb = Sanitize(thresholdDb, -60.0, 0.0, DefaultThresholdDb);
        Sustain = TimeSpan.FromSeconds(Sanitize(sustainSeconds, 0.5, 60.0, DefaultSustainSeconds));
        DipGrace = TimeSpan.FromSeconds(Sanitize(dipGraceSeconds, 0.0, 5.0, DefaultDipGraceSeconds));
        Cooldown = TimeSpan.FromSeconds(Sanitize(cooldownSeconds, 0.0, 600.0, DefaultCooldownSeconds));
    }

    /// <summary>
    /// この値**以上**のレベル（dB。REQ-LVL-02 の値）を「声がある」とみなす。
    /// 実測では会議音声のレベルの中央値が −22.9 dB で、−30 dB は発話していない時間帯も含め 63% の窓が超える。
    /// </summary>
    public double ThresholdDb { get; }

    /// <summary>閾値以上の時間がこれだけ**たまったら**発火する（連続である必要はない）。</summary>
    public TimeSpan Sustain { get; }

    /// <summary>
    /// これ以内の落ち込みは発話の区切りとみなし、累積を 0 に戻さない。
    /// 発話は語と語の間で必ずレベルが落ちるため（実測: −15 dB を連続で超えるのは中央値 0.10 秒）、
    /// 0 にすると実質発火しない。
    /// </summary>
    public TimeSpan DipGrace { get; }

    /// <summary>録音が止まってからこの時間は発火しない（止めた直後の再開防止）。</summary>
    public TimeSpan Cooldown { get; }

    public static AutoStartOptions Default { get; } =
        new(DefaultThresholdDb, DefaultSustainSeconds, DefaultCooldownSeconds, DefaultDipGraceSeconds);

    private static double Sanitize(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }
}

/// <summary>
/// マイクのレベルから「録音を自動で始めるべき瞬間」を決める判定器（REQ-REC-12）。
/// </summary>
/// <remarks>
/// 副作用を持たず、状態は「閾値以上が続いた時間」と「停止からの経過時間」だけである。
/// 呼び出しはレベルメーターのタイマー（UI スレッド、50ms）から行い、スレッドは増やさない。
/// 誤起動（咳・キーボード音の 1 発）を避けるため、落ち込みが <see cref="AutoStartOptions.DipGrace"/> を
/// 超えたら累積を 0 に戻す。
/// </remarks>
public sealed class AutoStartTrigger
{
    private readonly AutoStartOptions _options;
    private TimeSpan _sustained;

    /// <summary>閾値を下回っている時間。<see cref="AutoStartOptions.DipGrace"/> を超えたら累積を捨てる。</summary>
    private TimeSpan _belowFor;

    /// <summary>直前の停止からの経過。<c>null</c> なら停止したことが無い（クールダウン無し）。</summary>
    private TimeSpan? _sinceStopped;

    public AutoStartTrigger(AutoStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>閾値以上がたまっている時間（表示・診断用）。</summary>
    public TimeSpan Sustained => _sustained;

    /// <summary>
    /// レベルを 1 回観測する。発火すべきなら <c>true</c> を返し、内部の累積を 0 に戻す。
    /// </summary>
    /// <param name="levelDb">マイクのレベル（dB）。</param>
    /// <param name="elapsed">前回の観測からの経過時間。</param>
    /// <param name="canStart">
    /// いま録音を始めてよいか（自動開始が有効・録音中でない・マイク選択済み・モーダル中でない）。
    /// <c>false</c> の間は積算しない（条件が整った瞬間に、以前の積算で即発火しないため）。
    /// </param>
    public bool Observe(double levelDb, TimeSpan elapsed, bool canStart)
    {
        if (_sinceStopped is { } since)
        {
            _sinceStopped = since + elapsed;
        }

        if (!canStart)
        {
            Reset();
            return false;
        }

        if (levelDb < _options.ThresholdDb)
        {
            // 語と語の間の短い落ち込みでは捨てない（REQ-REC-12）
            _belowFor += elapsed;
            if (_belowFor > _options.DipGrace)
            {
                _sustained = TimeSpan.Zero;
            }
            return false;
        }

        _belowFor = TimeSpan.Zero;
        _sustained += elapsed;
        if (_sustained < _options.Sustain)
        {
            return false;
        }

        // 停止直後のクールダウン中は、条件が揃っていても発火しない（積算は続く）
        if (_sinceStopped is { } sinceStop && sinceStop < _options.Cooldown)
        {
            return false;
        }

        _sustained = TimeSpan.Zero;
        return true;
    }

    /// <summary>録音が止まった。クールダウンを始める。</summary>
    public void NotifyStopped()
    {
        _sinceStopped = TimeSpan.Zero;
        Reset();
    }

    /// <summary>累積を 0 に戻す（開始に失敗したときなど）。</summary>
    public void Reset()
    {
        _sustained = TimeSpan.Zero;
        _belowFor = TimeSpan.Zero;
    }
}