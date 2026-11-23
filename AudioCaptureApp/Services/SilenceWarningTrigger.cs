namespace AudioCaptureApp.Services;

/// <summary>
/// 無音の警告（REQ-REC-14）の調整値。settings.json から与えられるため、
/// 手書きされた不正値でも壊れないようコンストラクターで必ずクランプする（REQ-CFG-16）。
/// </summary>
public sealed record SilenceWarningOptions
{
    public const double DefaultSeconds = 60.0;
    public const double DefaultThresholdDb = -50.0;

    public SilenceWarningOptions(double seconds, double thresholdDb)
    {
        var sanitizedSeconds = double.IsFinite(seconds) ? seconds : DefaultSeconds;
        Enabled = sanitizedSeconds > 0;
        Duration = TimeSpan.FromSeconds(Enabled ? Math.Clamp(sanitizedSeconds, 10.0, 3600.0) : DefaultSeconds);
        // −60 はレベルの下限（REQ-LVL-02）で「未満」が成り立たないため、下限は −59 にする
        ThresholdDb = double.IsFinite(thresholdDb) ? Math.Clamp(thresholdDb, -59.0, -20.0) : DefaultThresholdDb;
    }

    /// <summary>警告するか。<c>SilenceWarningSeconds</c> が 0 以下なら <c>false</c>。</summary>
    public bool Enabled { get; }

    /// <summary>無音がこれだけ途切れずに続いたら警告する。</summary>
    public TimeSpan Duration { get; }

    /// <summary>
    /// この値**未満**のレベル（dB）を無音とみなす。実測では、普通に録れた会議で −50 dB 未満が続いた最長は 14.1 秒。
    /// </summary>
    public double ThresholdDb { get; }

    public static SilenceWarningOptions Default { get; } = new(DefaultSeconds, DefaultThresholdDb);
}

/// <summary>
/// マイクとスピーカーのレベルから「無音が続いているので警告すべき瞬間」を決める判定器（REQ-REC-14）。
/// </summary>
/// <remarks>
/// 副作用を持たず、状態は「両方が無音の時間」と「この無音で警告済みか」だけである。
/// 呼び出しはレベルメーターのタイマー（UI スレッド、50ms）から行い、スレッドは増やさない（<see cref="AutoStartTrigger"/> と同じ）。
/// 1 回の無音につき 1 回だけ発火し、どちらかのレベルがしきい値以上になったら次の無音に備える。
/// </remarks>
public sealed class SilenceWarningTrigger
{
    private readonly SilenceWarningOptions _options;
    private TimeSpan _silentFor;
    private bool _warned;

    public SilenceWarningTrigger(SilenceWarningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// レベルを 1 回観測する。警告すべきなら <c>true</c> を返す。同じ無音の間は 2 度目以降 <c>false</c>。
    /// </summary>
    /// <param name="micLevelDb">マイクのレベル（dB）。未選択・ミュート中は −60。</param>
    /// <param name="loopbackLevelDb">スピーカーのレベル（dB）。未選択・再生なしは −60。</param>
    /// <param name="elapsed">前回の観測からの経過時間。</param>
    /// <param name="isRecording">
    /// 録音中か（停止処理中を除く）。<c>false</c> の間は数えず、状態を捨てる（次の録音を 0 から数える）。
    /// </param>
    public bool Observe(double micLevelDb, double loopbackLevelDb, TimeSpan elapsed, bool isRecording)
    {
        if (!_options.Enabled || !isRecording)
        {
            Reset();
            return false;
        }

        if (micLevelDb >= _options.ThresholdDb || loopbackLevelDb >= _options.ThresholdDb)
        {
            // 音が戻った。次の無音に備える
            Reset();
            return false;
        }

        _silentFor += elapsed;
        if (_warned || _silentFor < _options.Duration)
        {
            return false;
        }

        _warned = true;
        return true;
    }

    /// <summary>無音の時間と警告済みの印を捨てる。</summary>
    public void Reset()
    {
        _silentFor = TimeSpan.Zero;
        _warned = false;
    }
}