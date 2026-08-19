using AudioCaptureApp.Services;
using Whisper.net.LibraryLoader;

namespace AudioCaptureApp.Tests;

public class TranscriptionServiceTests
{
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

    [Fact]
    public void LoadModel_CorruptModelFile_ReturnsFailureAndRaisesError()
    {
        // T122 / REQ-TRX-01。WhisperFactory.FromPath は読み込みに失敗しても例外を投げず
        // ファクトリを返すため、LoadModel は壊れたモデルでも成功を返していた。
        //
        // 注: このテストは Whisper のネイティブランタイム（Whisper.net.Runtime* が出力へ
        //     配置する DLL）のロードを伴う。実 GGML モデルは要求しない。
        var path = Path.Combine(Path.GetTempPath(), $"t122-corrupt-{Guid.NewGuid():N}.bin");
        // GGML のマジック (0x67676d6c) と一致しない固定パターンで埋める
        var garbage = new byte[64 * 1024];
        Array.Fill(garbage, (byte)0xAB);
        File.WriteAllBytes(path, garbage);

        try
        {
            using var service = new TranscriptionService();
            var errors = new List<string>();
            service.Error += errors.Add;

            var (success, _) = service.LoadModel(path, useGpu: false);

            Assert.False(success);
            Assert.False(service.IsModelLoaded);
            Assert.NotEmpty(errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- 実行先の表記 (T119 / T123 / REQ-GPU-05) ---
    //
    // RuntimeOptions.LoadedLibrary は「どのランタイム DLL を読み込んだか」であって
    // 「GPU で計算しているか」ではない。GPU 版ランタイムを読み込んだまま CPU 実行する
    // ケースが 2 つある（UseGpu = false のとき / 使える GPU デバイスが無いとき）ため、
    // 通知には LoadedLibrary の値をそのまま使えない。

    [Fact]
    public void DescribeRuntime_GpuInUseAndGpuLibraryLoaded_ReportsGpuWithLibraryName()
    {
        Assert.Equal("GPU (Vulkan)", TranscriptionService.DescribeRuntime(RuntimeLibrary.Vulkan, gpuInUse: true));
        Assert.Equal("GPU (Cuda)", TranscriptionService.DescribeRuntime(RuntimeLibrary.Cuda, gpuInUse: true));
    }

    [Fact]
    public void DescribeRuntime_GpuLibraryLoadedButRunningOnCpu_ReportsCpu()
    {
        // T119 / T123 の本体。Vulkan ランタイムを読み込んだままでも計算が CPU なら CPU と通知する。
        Assert.Equal("CPU", TranscriptionService.DescribeRuntime(RuntimeLibrary.Vulkan, gpuInUse: false));
    }

    [Fact]
    public void DescribeRuntime_CpuLibraryLoaded_ReportsCpuAlways()
    {
        Assert.Equal("CPU", TranscriptionService.DescribeRuntime(RuntimeLibrary.Cpu, gpuInUse: true));
        Assert.Equal("CPU", TranscriptionService.DescribeRuntime(RuntimeLibrary.CpuNoAvx, gpuInUse: true));
        Assert.Equal("CPU", TranscriptionService.DescribeRuntime(RuntimeLibrary.Cpu, gpuInUse: false));
    }

    [Fact]
    public void DescribeRuntime_NoLibraryLoaded_ReportsCpu()
    {
        Assert.Equal("CPU", TranscriptionService.DescribeRuntime(null, gpuInUse: true));
    }

    // --- ネイティブログからの GPU 実態判定 (T123 / REQ-TRX-02, REQ-GPU-05) ---
    //
    // 入力はすべて実機で採取した whisper.cpp / ggml のログ行そのもの。
    // GPU 版ランタイム DLL はデバイスが無くても読み込めるため、LoadedLibrary だけでは
    // GPU 利用可否を判定できない（Vulkan ICD を無効化して誤検知を実証済み）。

    [Fact]
    public void ParseBackendCount_WhisperInitLine_ReturnsCount()
    {
        Assert.Equal(2, TranscriptionService.ParseBackendCount(
            "whisper_init_with_params_no_state: backends   = 2"));
        Assert.Equal(1, TranscriptionService.ParseBackendCount(
            "whisper_init_with_params_no_state: backends   = 1"));
    }

    [Fact]
    public void ParseBackendCount_DeviceCountLine_ReturnsNull()
    {
        // devices は backends とは別の値。取り違えない
        Assert.Null(TranscriptionService.ParseBackendCount(
            "whisper_init_with_params_no_state: devices    = 5"));
    }

    [Fact]
    public void ParseBackendCount_UnrelatedLine_ReturnsNull()
    {
        Assert.Null(TranscriptionService.ParseBackendCount(
            "whisper_model_load: loading model"));
        Assert.Null(TranscriptionService.ParseBackendCount(""));
    }

    [Fact]
    public void ParseModelBackend_GpuPlacement_ReturnsBackendName()
    {
        Assert.Equal("Vulkan0", TranscriptionService.ParseModelBackend(
            "whisper_model_load:      Vulkan0 total size =   487.01 MB"));
    }

    [Fact]
    public void ParseModelBackend_CpuPlacement_ReturnsCpu()
    {
        Assert.Equal("CPU", TranscriptionService.ParseModelBackend(
            "whisper_model_load:          CPU total size =   487.01 MB"));
    }

    [Fact]
    public void ParseModelBackend_ModelSizeLine_ReturnsNull()
    {
        // "total size" ではなく "model size" の行。バックエンド名は載っていない
        Assert.Null(TranscriptionService.ParseModelBackend(
            "whisper_model_load: model size    =  487.01 MB"));
        Assert.Null(TranscriptionService.ParseModelBackend(
            "ggml_vulkan: Found 4 Vulkan devices:"));
    }

    [Fact]
    public void IsGpuInUse_CpuPlacement_ReturnsFalse()
    {
        Assert.False(TranscriptionService.IsGpuInUse("CPU"));
    }

    [Fact]
    public void IsGpuInUse_GpuPlacement_ReturnsTrue()
    {
        Assert.True(TranscriptionService.IsGpuInUse("Vulkan0"));
        Assert.True(TranscriptionService.IsGpuInUse("CUDA0"));
    }

    [Fact]
    public void IsGpuInUse_Unknown_ReturnsFalse()
    {
        Assert.False(TranscriptionService.IsGpuInUse(null));
    }

    // --- 無音カットの調整値 (T112) ---
    //
    // settings.json から手書きで与えられうるため、不正値でも既定値へ倒れることを保証する。

    [Fact]
    public void SilenceCutOptions_Defaults_MatchSpec()
    {
        var options = SilenceCutOptions.Default;

        Assert.Equal(0.01, options.RmsThreshold);
        Assert.Equal(2.0, options.MergeGapSeconds);
        Assert.Equal(0.2, options.PaddingSeconds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SilenceCutOptions_NonFiniteValues_FallBackToDefaults(double bad)
    {
        var options = new SilenceCutOptions(bad, bad, bad);

        Assert.Equal(SilenceCutOptions.Default.RmsThreshold, options.RmsThreshold);
        Assert.Equal(SilenceCutOptions.Default.MergeGapSeconds, options.MergeGapSeconds);
        Assert.Equal(SilenceCutOptions.Default.PaddingSeconds, options.PaddingSeconds);
    }

    [Fact]
    public void SilenceCutOptions_OutOfRangeValues_AreClamped()
    {
        var options = new SilenceCutOptions(-1.0, -5.0, 999.0);

        Assert.Equal(0.0, options.RmsThreshold);
        Assert.Equal(0.0, options.MergeGapSeconds);
        Assert.Equal(5.0, options.PaddingSeconds);
    }

    // --- チャンクの有声区間分割 (T112) ---
    //
    // 20 秒チャンクのうち発話が 0.5 秒だけでも、従来は 20 秒まるごと Whisper に渡っていた。
    // 無音部分に「ご視聴ありがとうございました」等のハルシネーションが載るため、
    // 有声区間だけを切り出してそれぞれ個別に渡す。

    [Fact]
    public void SplitVoicedRegions_AllSilent_ReturnsEmpty()
    {
        var samples = new float[16000 * 20];

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Empty(regions);
    }

    [Fact]
    public void SplitVoicedRegions_EmptyChunk_ReturnsEmpty()
    {
        var regions = TranscriptionService.SplitVoicedRegions([], SilenceCutOptions.Default);

        Assert.Empty(regions);
    }

    /// <summary>16kHz の無音バッファを作り、指定範囲だけ有声（振幅 0.5）にする。</summary>
    private static float[] MakeSamples(int totalSamples, params (int Start, int Length)[] voiced)
    {
        var samples = new float[totalSamples];
        foreach (var (start, length) in voiced)
        {
            for (int i = start; i < start + length; i++)
            {
                samples[i] = 0.5f;
            }
        }
        return samples;
    }

    [Fact]
    public void SplitVoicedRegions_SilenceWithShortNoise_ReturnsOnlyNoiseRegion()
    {
        var samples = MakeSamples(16000 * 20, (160000, 8000));

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        var region = Assert.Single(regions);
        Assert.Equal(160000 - 3200, region.Start);
        Assert.Equal(8000 + 6400, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_TwoUtterancesWithLongGap_ReturnsTwoRegions()
    {
        var samples = MakeSamples(16000 * 10, (0, 16000), (80000, 16000));

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Equal(2, regions.Count);
        Assert.Equal(0, regions[0].Start);
        Assert.Equal(16000 + 3200, regions[0].Length);
        Assert.Equal(80000 - 3200, regions[1].Start);
        Assert.Equal(16000 + 6400, regions[1].Length);
    }

    [Fact]
    public void SplitVoicedRegions_ShortGap_MergesIntoOneRegion()
    {
        var samples = MakeSamples(16000 * 10, (0, 16000), (32000, 16000));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(0, region.Start);
        Assert.Equal(48000 + 3200, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_AddsPaddingAroundVoiced()
    {
        var samples = MakeSamples(16000 * 10, (80000, 16000));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(80000 - 3200, region.Start);
        Assert.Equal(16000 + 6400, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_PaddingClampedToChunkBounds()
    {
        var samples = MakeSamples(16000, (0, 8000));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(0, region.Start);
        Assert.True(region.Start + region.Length <= samples.Length);
        Assert.Equal(8000 + 3200, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_MostlyVoiced_ReturnsSingleFullRegion()
    {
        // 0〜9 秒と 11〜20 秒に発話（間隔ちょうど 2 秒なので結合されない）。
        // 余白込みの有声合計は 92% ≧ 90% → 分割せずチャンク全体を 1 区間で返す
        var samples = MakeSamples(16000 * 20, (0, 144000), (176000, 144000));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(0, region.Start);
        Assert.Equal(samples.Length, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_ContinuousSpeech_ReturnsSingleFullRegion()
    {
        var samples = MakeSamples(16000 * 20, (0, 16000 * 20));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(0, region.Start);
        Assert.Equal(samples.Length, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_ClickShorterThanMinimum_IsDropped()
    {
        // 100ms の単発クリック音（< 0.2 秒）→ 捨てる。
        // 足切りをパディングより後に行うと 0.1 + 0.4 = 0.5 秒になり素通りするため、
        // このテストが「足切りが先」という順序を守る。
        var samples = MakeSamples(16000 * 10, (48000, 1600));

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Empty(regions);
    }

    [Fact]
    public void SplitVoicedRegions_VoicedAtMinimumLength_IsKept()
    {
        var samples = MakeSamples(16000 * 10, (48000, 3200));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(48000 - 3200, region.Start);
        Assert.Equal(3200 + 6400, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_QuietRoomNoise_ReturnsEmpty()
    {
        var samples = new float[16000 * 20];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.005f;
        }

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Empty(regions);
    }

    [Fact]
    public void SplitVoicedRegions_ZeroThreshold_TreatsEverythingAsVoiced()
    {
        var samples = new float[16000 * 10];
        var options = new SilenceCutOptions(0.0, 2.0, 0.2);

        var region = Assert.Single(TranscriptionService.SplitVoicedRegions(samples, options));

        Assert.Equal(0, region.Start);
        Assert.Equal(samples.Length, region.Length);
    }

    // --- 停止時に区間ループを打ち切る条件 (T127) ---
    // 通常運転中は停止要求で打ち切る。停止時の排出処理では打ち切ってはならない
    // （_isRunning は既に false なので、打ち切ると最後のチャンクを 1 区間も処理せず捨てる）。

    [Fact]
    public void ShouldStopRegionLoop_Cancelled_AlwaysStops()
    {
        Assert.True(TranscriptionService.ShouldStopRegionLoop(
            cancelled: true, isRunning: true, interruptible: true));
        Assert.True(TranscriptionService.ShouldStopRegionLoop(
            cancelled: true, isRunning: false, interruptible: false));
    }

    [Fact]
    public void ShouldStopRegionLoop_Running_Continues()
    {
        Assert.False(TranscriptionService.ShouldStopRegionLoop(
            cancelled: false, isRunning: true, interruptible: true));
    }

    [Fact]
    public void ShouldStopRegionLoop_StopRequestedWhileInterruptible_Stops()
    {
        // 通常運転中に停止要求 → 残りの区間は処理しない（T117 の 30 秒猶予を超えないため）
        Assert.True(TranscriptionService.ShouldStopRegionLoop(
            cancelled: false, isRunning: false, interruptible: true));
    }

    [Fact]
    public void ShouldStopRegionLoop_DrainingAfterStop_DoesNotStop()
    {
        // 排出処理は _isRunning == false で走る。ここで打ち切ると
        // 最後のチャンクが 1 区間も書き出されずに失われる（T120 の対策が無効になる）。
        Assert.False(TranscriptionService.ShouldStopRegionLoop(
            cancelled: false, isRunning: false, interruptible: false));
    }

    // --- 区間の開始時刻の合成 (T112) ---
    // ライブ・ファイルの両経路が RegionStart を通る。ここが壊れると
    // 「無音を切ったぶんだけ記録時刻がずれる」という最も気付きにくい壊れ方をする。

    [Fact]
    public void RegionStart_AtChunkHead_EqualsChunkStart()
    {
        var chunkStart = TimeSpan.FromSeconds(30);

        Assert.Equal(chunkStart, TranscriptionService.RegionStart(chunkStart, 0));
    }

    [Fact]
    public void RegionStart_AddsRegionOffsetInSeconds()
    {
        // チャンク先頭が 30 秒地点、区間はその 10 秒後（160000 サンプル）から始まる
        var chunkStart = TimeSpan.FromSeconds(30);

        var start = TranscriptionService.RegionStart(chunkStart, 160000);

        Assert.Equal(TimeSpan.FromSeconds(40), start);
    }

    [Fact]
    public void RegionStart_UsesSampleCountNotRegionLength()
    {
        // 16000 サンプル = 1 秒。区間長ではなく「チャンク先頭からの位置」で換算する
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            TranscriptionService.RegionStart(TimeSpan.Zero, 16000));
    }

    [Fact]
    public void RegionStart_SubSecondOffset_IsNotTruncated()
    {
        // パディング分の 3200 サンプル = 0.2 秒。整数秒に丸めると語頭がずれる
        var start = TranscriptionService.RegionStart(TimeSpan.Zero, 3200);

        Assert.Equal(TimeSpan.FromMilliseconds(200), start);
    }

    // --- 境界値と既知の限界の固定 (T112 / ミューテーション検査で判明した穴) ---

    [Fact]
    public void SplitVoicedRegions_GapExactlyMergeThreshold_DoesNotMerge()
    {
        // 間隔がちょうど 2.0 秒。判定は「未満なら結合」なので結合されない。
        // この境界を固定しないと `<` を `<=` に変えても全テストが通ってしまう。
        var samples = MakeSamples(16000 * 20, (0, 32000), (64000, 32000));

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Equal(2, regions.Count);
        Assert.Equal(0, regions[0].Start);
        Assert.Equal(32000 + 3200, regions[0].Length);
        Assert.Equal(64000 - 3200, regions[1].Start);
        Assert.Equal(32000 + 6400, regions[1].Length);
    }

    [Fact]
    public void SplitVoicedRegions_PaddingCausesOverlap_MergesAfterPadding()
    {
        // 結合幅 0.1 秒 < 余白 2×0.5 秒。パディング後に区間が接触するため、
        // 後段の再結合が無いと重なった区間を 2 つ返し、同じ音声を 2 回 Whisper に渡してしまう。
        var samples = MakeSamples(16000 * 10, (0, 16000), (32000, 16000));
        var options = new SilenceCutOptions(0.01, 0.1, 0.5);

        var region = Assert.Single(TranscriptionService.SplitVoicedRegions(samples, options));

        Assert.Equal(0, region.Start);
        Assert.Equal(56000, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_ChunkLengthNotMultipleOfWindow_CoversToEnd()
    {
        // チャンク長が窓長の倍数でない場合、末尾の短い窓を落とさず区間が末尾まで届く。
        var samples = MakeSamples(16000 * 10 + 800, (152000, 8800));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(152000 - 3200, region.Start);
        Assert.Equal(samples.Length, region.Start + region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_JustBelowNoSplitRatio_ReturnsSeparateRegions()
    {
        // 余白込みの有声合計が 88%（< 90%）→ 分割したまま返す。
        var samples = MakeSamples(16000 * 20, (0, 136000), (168000, 136000));

        var regions = TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default);

        Assert.Equal(2, regions.Count);
    }

    [Fact]
    public void SplitVoicedRegions_ExactlyAtNoSplitRatio_ReturnsSingleFullRegion()
    {
        // 余白込みの有声合計がちょうど 90% → 判定は「以上」なのでチャンク全体を返す。
        var samples = MakeSamples(16000 * 20, (0, 139200), (171200, 139200));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(0, region.Start);
        Assert.Equal(samples.Length, region.Length);
    }

    [Fact]
    public void SplitVoicedRegions_TwoClicksWithinMergeGap_AreMergedAndKept()
    {
        // 既知の限界の固定。0.1 秒のクリック音 2 つが結合幅 2.0 秒の中にあると、
        // 1 つの区間（大半は無音）にまとまり、span が 0.2 秒以上なので生き残る。
        // 有声密度で切る改善は別タスクで扱う。ここは挙動を可視化するための記録テスト。
        var samples = MakeSamples(16000 * 20, (48000, 1600), (64000, 1600));

        var region = Assert.Single(
            TranscriptionService.SplitVoicedRegions(samples, SilenceCutOptions.Default));

        Assert.Equal(48000 - 3200, region.Start);
        Assert.Equal(24000, region.Length);
    }
}