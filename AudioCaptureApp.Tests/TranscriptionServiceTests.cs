using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

public class TranscriptionServiceTests
{
    [Fact]
    public void IsSilent_AllZeros_ReturnsTrue()
    {
        var samples = new float[1000];

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_VerySmallSignal_ReturnsTrue()
    {
        // RMS < 0.01 threshold
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.005f; // constant 0.005 → RMS = 0.005 < 0.01

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_LoudSignal_ReturnsFalse()
    {
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.5f; // RMS = 0.5 >> 0.01

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_SingleLargeSampleAmongSilence_DependsOnRms()
    {
        // 1000 samples, only 1 is loud (0.5)
        // RMS = sqrt(0.25 / 1000) = sqrt(0.00025) ≈ 0.0158 > 0.01 → not silent
        var samples = new float[1000];
        samples[500] = 0.5f;

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_BelowThresholdRms_ReturnsTrue()
    {
        // Choose amplitude so RMS is just below 0.01
        // For constant signal: RMS = amplitude, so amplitude < 0.01
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.009f;

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_AboveThreshold_ReturnsFalse()
    {
        // RMS clearly above 0.01
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.02f;

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    // --- 窓ごとの無音判定 (T116) ---
    //
    // チャンク全体の平均 RMS で判定すると、長い無音に埋もれた短い発話がならされて
    // 無音扱いになり、チャンクごと Whisper に渡らなくなっていた。

    /// <summary>16kHz で <paramref name="seconds"/> 秒ぶんのサンプル数。</summary>
    private static int Samples(double seconds) => (int)(16000 * seconds);

    /// <summary>全体 <paramref name="totalSec"/> 秒の無音の中に、振幅 <paramref name="amplitude"/> の
    /// 発話が <paramref name="speechSec"/> 秒だけ入っている信号を作る。</summary>
    private static float[] SpeechInSilence(double totalSec, double speechSec, float amplitude)
    {
        var samples = new float[Samples(totalSec)];
        int speechStart = (samples.Length - Samples(speechSec)) / 2;
        for (int i = speechStart; i < speechStart + Samples(speechSec); i++)
        {
            samples[i] = amplitude;
        }
        return samples;
    }

    [Fact]
    public void IsSilent_ShortSpeechInLongSilence_ReturnsFalse()
    {
        // 20秒中1.5秒だけ RMS 0.03 の発話。報告された「短い発話が出力されない」の再現条件。
        var samples = SpeechInSilence(totalSec: 20, speechSec: 1.5, amplitude: 0.03f);

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_ShortSpeechInLongSilence_WholeChunkAveragingWouldDropIt()
    {
        // 修正前の挙動（窓＝チャンク全体）では無音と判定され捨てられていたことを固定する。
        // 全体 RMS = 0.03 * sqrt(1.5 / 20) ≒ 0.0082 < 0.01
        var samples = SpeechInSilence(totalSec: 20, speechSec: 1.5, amplitude: 0.03f);

        Assert.True(TranscriptionService.IsSilent(
            samples,
            TranscriptionService.SilenceRmsThreshold,
            windowSamples: samples.Length));
    }

    [Fact]
    public void IsSilent_VeryShortSpeechInLongSilence_ReturnsFalse()
    {
        // 窓長 (100ms) より短い 50ms の発話でも、窓の半分を占めれば
        // RMS = 0.05 * sqrt(0.5) ≒ 0.035 で閾値を超える
        var samples = SpeechInSilence(totalSec: 20, speechSec: 0.05, amplitude: 0.05f);

        Assert.False(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_LongQuietRoomNoise_StillReturnsTrue()
    {
        // 窓ごと判定にしてもハルシネーション抑止は維持されること。
        // 暗騒音相当（RMS 0.003）はどの窓でも閾値を下回る。
        var samples = new float[Samples(20)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 2 == 0) ? 0.003f : -0.003f;
        }

        Assert.True(TranscriptionService.IsSilent(samples));
    }

    [Fact]
    public void IsSilent_EmptyChunk_ReturnsTrue()
    {
        Assert.True(TranscriptionService.IsSilent([]));
    }

    // --- ギャップ検出 (T116) ---

    [Fact]
    public void ShouldSplitOnGap_ContiguousAudio_ReturnsFalse()
    {
        // 直前のパケット末尾 10.000s に対し、次のパケットが 10.010s から始まる（通常の連続供給）
        var result = TranscriptionService.ShouldSplitOnGap(
            audioStart: TimeSpan.FromSeconds(10.010),
            bufferEndElapsed: TimeSpan.FromSeconds(10.0),
            bufferedSampleCount: 160000,
            threshold: TimeSpan.FromMilliseconds(500));

        Assert.False(result);
    }

    [Fact]
    public void ShouldSplitOnGap_LongSilenceGap_ReturnsTrue()
    {
        // ミュート／再生停止で 2 分間コールバックが来なかったケース
        var result = TranscriptionService.ShouldSplitOnGap(
            audioStart: TimeSpan.FromSeconds(130),
            bufferEndElapsed: TimeSpan.FromSeconds(10),
            bufferedSampleCount: 160000,
            threshold: TimeSpan.FromMilliseconds(500));

        Assert.True(result);
    }

    [Fact]
    public void ShouldSplitOnGap_JitterBelowThreshold_ReturnsFalse()
    {
        // キャプチャスレッドの遅延程度では分割しない
        var result = TranscriptionService.ShouldSplitOnGap(
            audioStart: TimeSpan.FromSeconds(10.3),
            bufferEndElapsed: TimeSpan.FromSeconds(10),
            bufferedSampleCount: 160000,
            threshold: TimeSpan.FromMilliseconds(500));

        Assert.False(result);
    }

    [Fact]
    public void ShouldSplitOnGap_EmptyBuffer_ReturnsFalse()
    {
        // 分割すべき中身が無いので、どれだけ間が空いていても分割しない
        var result = TranscriptionService.ShouldSplitOnGap(
            audioStart: TimeSpan.FromSeconds(600),
            bufferEndElapsed: TimeSpan.FromSeconds(10),
            bufferedSampleCount: 0,
            threshold: TimeSpan.FromMilliseconds(500));

        Assert.False(result);
    }

    // --- チャンク先頭時刻の逆算 (T116) ---

    [Fact]
    public void ChunkStartElapsed_SubtractsBufferedDuration()
    {
        // 末尾が 30 秒地点、バッファに 20 秒 (320000 サンプル) → 先頭は 10 秒地点
        var start = TranscriptionService.ChunkStartElapsed(
            TimeSpan.FromSeconds(30), bufferedSampleCount: 320000);

        Assert.Equal(TimeSpan.FromSeconds(10), start);
    }

    [Fact]
    public void ChunkStartElapsed_NeverGoesNegative()
    {
        // セッション開始直後は逆算がマイナスになりうるが、記録時刻が
        // 録音開始より前になってはいけない
        var start = TranscriptionService.ChunkStartElapsed(
            TimeSpan.FromSeconds(1), bufferedSampleCount: 320000);

        Assert.Equal(TimeSpan.Zero, start);
    }

    [Fact]
    public void ChunkStartElapsed_AfterTakingOneChunk_RemainderStartsWhereChunkEnded()
    {
        // T117: TakeNextChunk はバッファ全体ではなく 20 秒 (320000) ぶんだけ切り出す。
        // 切り出し後も残りの先頭時刻が連続していることを保証する。
        var bufferEnd = TimeSpan.FromSeconds(60);
        const int fullCount = 16000 * 60;      // 60 秒ぶん
        const int chunkCount = 16000 * 20;     // 20 秒ぶん切り出す

        var chunkStart = TranscriptionService.ChunkStartElapsed(bufferEnd, fullCount);
        var remainderStart = TranscriptionService.ChunkStartElapsed(bufferEnd, fullCount - chunkCount);

        Assert.Equal(TimeSpan.Zero, chunkStart);
        // 残りは切り出したチャンクの直後から始まる（時刻の連続性）
        Assert.Equal(chunkStart + TimeSpan.FromSeconds(20), remainderStart);
    }

    // --- 滞留バッファの切り出し判定 (T120) ---
    //
    // 20 秒分たまらなくても、先頭サンプルが 20 秒以上書き出されずに残っていたら確定する。
    // これが無いと、ミュートや再生停止で供給が止まったソースのバッファは
    // 「次のパケットが来てギャップ分割が発火するまで」書き出されない。

    private const int Threshold = 16000 * 20;   // BufferThresholdSamples

    [Fact]
    public void ChunkTakeCount_ReachedThreshold_TakesExactlyThreshold()
    {
        // 20 秒分に達していれば、それ以上溜まっていても 20 秒分だけ（T117）
        Assert.Equal(Threshold, TranscriptionService.ChunkTakeCount(Threshold, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void ChunkTakeCount_FarOverThresholdAndStale_StillTakesOnlyThreshold()
    {
        // バックログが積んでいても 1 回の Whisper 呼び出しは 20 秒分に制限する
        Assert.Equal(
            Threshold,
            TranscriptionService.ChunkTakeCount(Threshold * 6, TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void ChunkTakeCount_BelowThresholdAndFresh_TakesNothing()
    {
        // まだ溜め続けるべき状態（通常の録音中）
        Assert.Equal(0, TranscriptionService.ChunkTakeCount(16000 * 16, TimeSpan.FromSeconds(16)));
    }

    [Fact]
    public void ChunkTakeCount_BelowThresholdButStale_TakesWholeBuffer()
    {
        // 報告事象の再現条件: 16 秒分たまった直後にミュートされ、
        // 20 秒経っても次のパケットが来ない → バッファ全部を確定させる
        const int buffered = 16000 * 16;

        Assert.Equal(
            buffered,
            TranscriptionService.ChunkTakeCount(buffered, TimeSpan.FromSeconds(57)));
    }

    [Fact]
    public void ChunkTakeCount_ExactlyAtStaleAge_TakesWholeBuffer()
    {
        const int buffered = 16000 * 16;

        Assert.Equal(
            buffered,
            TranscriptionService.ChunkTakeCount(buffered, TranscriptionService.StaleBufferAge));
    }

    [Fact]
    public void ChunkTakeCount_StaleButShorterThanTailMinimum_TakesNothing()
    {
        // 0.2 秒未満の断片は 1 回の推論に見合わないためセッション終了まで持ち越す
        Assert.Equal(
            0,
            TranscriptionService.ChunkTakeCount(
                TranscriptionService.MinTailSamples - 1, TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void ChunkTakeCount_EmptyBuffer_TakesNothing()
    {
        Assert.Equal(0, TranscriptionService.ChunkTakeCount(0, TimeSpan.FromSeconds(600)));
    }

    // --- 短いチャンクのパディング (T116) ---

    [Fact]
    public void PadToMinimum_ShorterThanMinimum_PadsWithSilenceAtEnd()
    {
        float[] samples = [0.5f, -0.5f, 0.25f];

        var result = TranscriptionService.PadToMinimum(samples, minSamples: 8);

        Assert.Equal(8, result.Length);
        // 先頭は保存される（セグメント時刻がずれないこと）
        Assert.Equal(0.5f, result[0]);
        Assert.Equal(-0.5f, result[1]);
        Assert.Equal(0.25f, result[2]);
        // 残りは無音
        for (int i = 3; i < result.Length; i++)
        {
            Assert.Equal(0f, result[i]);
        }
    }

    [Fact]
    public void PadToMinimum_AlreadyLongEnough_ReturnsSameInstance()
    {
        float[] samples = [0.1f, 0.2f, 0.3f, 0.4f];

        var result = TranscriptionService.PadToMinimum(samples, minSamples: 4);

        Assert.Same(samples, result);
    }

    [Fact]
    public void LoadModel_FileDoesNotExist_ReturnsFailureAndGpuAvailableUnknown()
    {
        using var service = new TranscriptionService();

        var (success, gpuAvailable) = service.LoadModel(@"C:\nonexistent\model.bin", useGpu: true);

        Assert.False(success);
        // 判定不可の場合はGPU利用可能とみなす（チェックボックスをON扱いのままにするため）
        Assert.True(gpuAvailable);
        Assert.False(service.IsModelLoaded);
    }
}