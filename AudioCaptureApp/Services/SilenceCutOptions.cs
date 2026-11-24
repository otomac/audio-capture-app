namespace AudioCaptureApp.Services;

/// <summary>
/// 無音カットの調整値。settings.json から与えられるため、
/// 手書きされた不正値（負値・NaN・無限大・過大値）でも壊れないよう
/// コンストラクターで必ずクランプする。
/// </summary>
public sealed record SilenceCutOptions
{
    private const double DefaultRmsThreshold = 0.01;
    private const double DefaultMergeGapSeconds = 2.0;
    private const double DefaultPaddingSeconds = 0.2;

    /// <summary>余白の上限。1 チャンク（20 秒）に対して現実的な範囲に収める。</summary>
    private const double MaxPaddingSeconds = 5.0;

    /// <summary>結合幅の上限。チャンク長（20 秒）を超えても意味が無い。</summary>
    private const double MaxMergeGapSeconds = 20.0;

    public SilenceCutOptions(double rmsThreshold, double mergeGapSeconds, double paddingSeconds)
    {
        RmsThreshold = Sanitize(rmsThreshold, 0.0, 1.0, DefaultRmsThreshold);
        MergeGapSeconds = Sanitize(mergeGapSeconds, 0.0, MaxMergeGapSeconds, DefaultMergeGapSeconds);
        PaddingSeconds = Sanitize(paddingSeconds, 0.0, MaxPaddingSeconds, DefaultPaddingSeconds);
    }

    /// <summary>有声とみなす窓の RMS 下限。</summary>
    public double RmsThreshold { get; }

    /// <summary>これ未満の無音を挟む有声区間どうしは 1 区間に結合する。</summary>
    public double MergeGapSeconds { get; }

    /// <summary>各有声区間の前後に付ける余白。</summary>
    public double PaddingSeconds { get; }

    public static SilenceCutOptions Default { get; } =
        new(DefaultRmsThreshold, DefaultMergeGapSeconds, DefaultPaddingSeconds);

    // 非有限値は「設定が壊れている」とみなして既定値へ戻す。
    // Math.Clamp は NaN をそのまま返すため、先に弾く必要がある。
    private static double Sanitize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>チャンク内の有声区間。Start はチャンク先頭からのサンプル位置。</summary>
public readonly record struct VoicedRegion(int Start, int Length);