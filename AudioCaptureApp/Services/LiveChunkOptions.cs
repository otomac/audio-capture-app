namespace AudioCaptureApp.Services;

/// <summary>
/// ライブ文字起こしの確定パスの区切り（REQ-CFG-15 / REQ-TRX-LIVE-10 / 13、ADR-0009）。settings.json から与えられるため、
/// <see cref="SilenceCutOptions"/> と同じく手書きの不正値でも壊れないようコンストラクターでクランプする。
/// </summary>
/// <remarks>
/// <para>
/// 既定（6 秒 / 0.8 秒）は T199 の実測で合格した構成（small-q5_1 × 6 秒 × 0.8 秒）の値。
/// ADR-0007 の構成（10 秒 / 2.0 秒）に戻すときは settings.json に書く。
/// </para>
/// <para>
/// 効くのは<b>遅れていないとき</b>だけである。遅れているときは上限 20 秒・発話終了の無音を使わない
/// （REQ-TRX-LIVE-10 / 13。<see cref="TranscriptionService.ChunkTakeCount"/>）。
/// チャンク内の区間分け（<see cref="TranscriptionService.SplitVoicedRegions"/>）には
/// <see cref="SilenceCutOptions.MergeGapSeconds"/> を使い、ここの無音は使わない（T199 の D4 と同じ形）。
/// </para>
/// </remarks>
public sealed record LiveChunkOptions
{
    private const double DefaultMaxChunkSeconds = 6.0;
    private const double DefaultEndpointSilenceSeconds = 0.8;

    /// <summary>上限の下限。これより短い呼び出しは音声 1 秒あたりの費用が割高になる（REQ-TRX-LIVE-13 の実測）。</summary>
    public const double MinMaxChunkSeconds = 2.0;

    /// <summary>上限の上限。遅れているときの上限（20 秒）より長くしない。</summary>
    public const double MaxMaxChunkSeconds = 20.0;

    /// <summary>発話終了の無音の上限。<see cref="SilenceCutOptions"/> の結合幅と同じ。</summary>
    public const double MaxEndpointSilenceSeconds = 20.0;

    public LiveChunkOptions(double maxChunkSeconds, double endpointSilenceSeconds)
    {
        MaxChunkSeconds = Sanitize(maxChunkSeconds, MinMaxChunkSeconds, MaxMaxChunkSeconds, DefaultMaxChunkSeconds);
        EndpointSilenceSeconds = Sanitize(
            endpointSilenceSeconds, 0.0, MaxEndpointSilenceSeconds, DefaultEndpointSilenceSeconds);
    }

    /// <summary>遅れていないときのチャンクの上限（秒。REQ-TRX-LIVE-10）。話し続けていても、この長さごとに確定する。</summary>
    public double MaxChunkSeconds { get; }

    /// <summary>末尾にこの長さの無音が続いたら発話が終わったとみなして確定する（秒。REQ-TRX-LIVE-13）。</summary>
    public double EndpointSilenceSeconds { get; }

    public static LiveChunkOptions Default { get; } = new(DefaultMaxChunkSeconds, DefaultEndpointSilenceSeconds);

    // Math.Clamp は NaN をそのまま返すため、先に弾く（SilenceCutOptions と同じ）。
    private static double Sanitize(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}