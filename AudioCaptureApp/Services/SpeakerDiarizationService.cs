using System.Globalization;
using System.IO;
using AudioCaptureApp.Models;
using SherpaOnnx;

namespace AudioCaptureApp.Services;

/// <summary>
/// 話者ダイアライゼーションの調整値。settings.json から与えられるため、
/// 手書きされた不正値でも壊れないようコンストラクターで必ずクランプする（REQ-TRX-DIA-07）。
/// </summary>
/// <remarks>
/// <see cref="ClusteringThreshold"/> を 0 以下にしてはならない。sherpa-onnx は
/// 「話者数の指定が無く、かつ閾値が正でない」設定を不正とみなして **NULL ハンドルを返す**。
/// C# ラッパーはそれを検査しないため、そのまま使うとアクセス違反でプロセスが落ちる。
/// クランプはその防壁でもある（ADR-0003 の事実 F4）。
/// </remarks>
public sealed record SpeakerDiarizationOptions
{
    private const double DefaultClusteringThreshold = 0.5;

    /// <summary>閾値の下限。0 以下は sherpa-onnx の設定検証を通らない。</summary>
    private const double MinClusteringThreshold = 0.01;

    /// <summary>閾値の上限。コサイン距離ベースの閾値であり、これを超える値に意味は無い。</summary>
    private const double MaxClusteringThreshold = 2.0;

    /// <summary>話者数の上限。会議の参加者数として現実的な範囲に収める。</summary>
    private const int MaxKnownSpeakerCount = 100;

    /// <summary>推論スレッド数の上限。</summary>
    private const int MaxNumThreads = 16;

    public SpeakerDiarizationOptions(
        string segmentationModelPath,
        string embeddingModelPath,
        double clusteringThreshold,
        int? knownSpeakerCount,
        int numThreads)
    {
        SegmentationModelPath = segmentationModelPath ?? "";
        EmbeddingModelPath = embeddingModelPath ?? "";
        ClusteringThreshold = Sanitize(
            clusteringThreshold, MinClusteringThreshold, MaxClusteringThreshold, DefaultClusteringThreshold);

        // 0 以下は「未指定」として扱い、閾値による自動判定へ倒す（REQ-TRX-DIA-07）。
        KnownSpeakerCount = knownSpeakerCount is int count && count > 0
            ? Math.Min(count, MaxKnownSpeakerCount)
            : null;

        NumThreads = Math.Clamp(numThreads, 1, MaxNumThreads);
    }

    public string SegmentationModelPath { get; }

    public string EmbeddingModelPath { get; }

    /// <summary>話者数が未知のときに使うクラスタリング閾値。必ず正の値になる。</summary>
    public double ClusteringThreshold { get; }

    /// <summary>話者数の指定。<c>null</c> なら <see cref="ClusteringThreshold"/> を使う。</summary>
    public int? KnownSpeakerCount { get; }

    public int NumThreads { get; }

    private static double Sanitize(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
        {
            return fallback;
        }

        return value;
    }
}

/// <summary>
/// 話者ダイアライゼーションの失敗（REQ-TRX-DIA-11）。
/// どの段階で何が起きたかがメッセージから分かるようにする。音声の内容や文字起こし本文は載せない。
/// </summary>
public sealed class SpeakerDiarizationException : Exception
{
    public SpeakerDiarizationException()
    {
    }

    public SpeakerDiarizationException(string message) : base(message)
    {
    }

    public SpeakerDiarizationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// sherpa-onnx による話者ダイアライゼーション（REQ-TRX-DIA-01）。
/// **sherpa-onnx 固有の型への依存はこのクラスの中だけに閉じる**（ADR-0003 争点 3）。
/// 呼び出し側へ返すのは <see cref="SpeakerSegment"/> だけである。
/// </summary>
/// <remarks>
/// Whisper 側（<see cref="TranscriptionService"/>）を一切参照しない。
/// 両エンジンは同じ音声を独立に解析し、統合は <see cref="TranscriptDiarizationMerger"/> が担う
/// （REQ-TRX-DIA-04）。
/// </remarks>
public sealed class SpeakerDiarizationService : IDisposable
{
    /// <summary>
    /// 本アプリが Diarization へ渡す音声のサンプリングレート。
    /// REQ-TRX-05 により音声は常にこのレートのモノラルへ正規化されている。
    /// </summary>
    public const int RequiredSampleRate = 16000;

    private readonly SpeakerDiarizationOptions _options;

    /// <summary>
    /// <see cref="OfflineSpeakerDiarization"/> はスレッド安全性が保証されていないため、
    /// 生成と推論をまとめて直列化する（REQ-TRX-DIA-10）。
    /// </summary>
    private readonly Lock _gate = new();

    private OfflineSpeakerDiarization? _diarization;
    private bool _disposed;

    public SpeakerDiarizationService(SpeakerDiarizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// 音声全体を解析して話者区間を返す。
    /// </summary>
    /// <param name="samples">16kHz モノラルの PCM。**音声全体**を渡すこと（部分では話者 ID が揃わない）。</param>
    /// <param name="progress">解析の進み具合（0.0〜1.0）。ネイティブの進捗コールバックから呼ばれる。</param>
    /// <param name="ct">
    /// 開始前と完了後にだけ評価する。ネイティブ処理は途中で止めない（REQ-TRX-DIA-12）。
    /// 強制停止はネイティブリソースの破棄漏れとプロセスクラッシュを招くためである。
    /// </param>
    /// <exception cref="SpeakerDiarizationException">
    /// モデル未配置・モデル破損・初期化失敗・サンプリングレート不一致・推論失敗。
    /// </exception>
    public IReadOnlyList<SpeakerSegment> Diarize(
        float[] samples, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ct.ThrowIfCancellationRequested();

        OfflineSpeakerDiarizationSegment[] segments;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var diarization = EnsureLoaded();

            // REQ-TRX-DIA-09: レートの不一致を暗黙に許容しない。
            // モデルが要求するレートは実行時にしか分からないため、ここで必ず突き合わせる。
            if (diarization.SampleRate != RequiredSampleRate)
            {
                throw new SpeakerDiarizationException(string.Create(CultureInfo.InvariantCulture,
                    $"話者識別モデルが要求するサンプリングレート ({diarization.SampleRate} Hz) が、" +
                    $"本アプリが渡す音声のレート ({RequiredSampleRate} Hz) と一致しません。" +
                    $"モデル: {_options.SegmentationModelPath}"));
            }

            segments = Process(diarization, samples, progress);
        }

        ct.ThrowIfCancellationRequested();

        var result = new List<SpeakerSegment>(segments.Length);
        foreach (var segment in segments)
        {
            result.Add(new SpeakerSegment(
                TimeSpan.FromSeconds(segment.Start),
                TimeSpan.FromSeconds(segment.End),
                segment.Speaker));
        }

        return result;
    }

    private static OfflineSpeakerDiarizationSegment[] Process(
        OfflineSpeakerDiarization diarization, float[] samples, IProgress<double>? progress)
    {
        if (progress == null)
        {
            return InvokeNative(() => diarization.Process(samples), "話者識別の実行");
        }

        // 進捗コールバックはネイティブ側から同期的に呼ばれる。戻り値で処理を中断させることは
        // しない（REQ-TRX-DIA-12）。デリゲートは呼び出しが終わるまで生存させる必要があるため、
        // ローカルに保持したうえで GC.KeepAlive で寿命を明示する。
        var callback = new OfflineSpeakerDiarizationProgressCallback((processed, total, _) =>
        {
            if (total > 0)
            {
                progress.Report(Math.Clamp((double)processed / total, 0.0, 1.0));
            }

            return 0;
        });

        try
        {
            return InvokeNative(
                () => diarization.ProcessWithCallback(samples, callback, IntPtr.Zero),
                "話者識別の実行");
        }
        finally
        {
            GC.KeepAlive(callback);
        }
    }

    /// <summary>
    /// モデルを 1 度だけ読み込み、以後は使い回す（REQ-TRX-DIA-10）。呼び出し元が <see cref="_gate"/> を保持していること。
    /// </summary>
    private OfflineSpeakerDiarization EnsureLoaded()
    {
        if (_diarization != null)
        {
            return _diarization;
        }

        // REQ-TRX-DIA-08: **この存在検査を省いてはならない。**
        // sherpa-onnx はモデルが無いと NULL ハンドルを返すが、C# ラッパーはそれを検査せず
        // オブジェクトを返す。以後の呼び出しでアクセス違反 (0xC0000005) となり、
        // .NET の catch を一切通らずにプロセスが即死する（ADR-0003 の事実 F4）。
        EnsureModelFileExists(_options.SegmentationModelPath, "話者区間検出 (segmentation)");
        EnsureModelFileExists(_options.EmbeddingModelPath, "話者埋め込み (embedding)");

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _options.SegmentationModelPath;
        config.Segmentation.NumThreads = _options.NumThreads;
        config.Embedding.Model = _options.EmbeddingModelPath;
        config.Embedding.NumThreads = _options.NumThreads;

        if (_options.KnownSpeakerCount is int knownCount)
        {
            // 話者数が分かっているときだけクラスタ数を固定する。
            config.Clustering.NumClusters = knownCount;
        }
        else
        {
            // 負値が「話者数は未知」の意味。閾値は必ず正（SpeakerDiarizationOptions が保証する）。
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = (float)_options.ClusteringThreshold;
        }

        _diarization = InvokeNative(
            () => new OfflineSpeakerDiarization(config),
            "話者識別モデルの読み込み",
            string.Create(CultureInfo.InvariantCulture,
                $"segmentation: {_options.SegmentationModelPath} / embedding: {_options.EmbeddingModelPath}"));

        return _diarization;
    }

    private static void EnsureModelFileExists(string path, string role)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SpeakerDiarizationException(
                $"{role}モデルのパスが設定されていません。settings.json を確認してください。");
        }

        if (!File.Exists(path))
        {
            throw new SpeakerDiarizationException(
                $"{role}モデルが見つかりません: {path}");
        }
    }

    /// <summary>
    /// ネイティブ呼び出しを <see cref="SpeakerDiarizationException"/> に包む。
    /// </summary>
    /// <remarks>
    /// CA1031: ネイティブ境界。壊れたモデルは <c>SEHException</c>、ONNX Runtime の内部事情では
    /// それ以外の型も飛びうるため、型を列挙せずに受けて原因の分かるメッセージへ変換する。
    /// なお NULL ハンドル経由のアクセス違反はここでは捕まえられない。それは
    /// <see cref="EnsureModelFileExists"/> で事前に防ぐ。
    /// </remarks>
#pragma warning disable CA1031
    private static T InvokeNative<T>(Func<T> action, string stage, string? detail = null)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            var suffix = detail == null ? "" : $" ({detail})";
            throw new SpeakerDiarizationException(
                $"{stage}に失敗しました{suffix}: {ex.Message}", ex);
        }
    }
#pragma warning restore CA1031

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _diarization?.Dispose();
            _diarization = null;
        }
    }
}