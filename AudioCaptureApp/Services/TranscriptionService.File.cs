using System.IO;
using System.Text;
using Whisper.net;

namespace AudioCaptureApp.Services;

// TranscriptionService のうち、ファイル文字起こしの入口と、話者識別なしの経路を担当する部分。
// 話者識別ありの経路は .FileDiarization.cs、入出力ファイルまわりは .FileIo.cs にある（T193 で切り出した）。
public partial class TranscriptionService
{
    /// <summary>
    /// 音声ファイルを文字起こしする。
    /// </summary>
    /// <param name="options">開始時刻・言語・話者人数（<see cref="FileTranscriptionOptions"/>）。</param>
    /// <remarks>
    /// <see cref="FileTranscriptionOptions.StartOffset"/> は進捗（<paramref name="progress"/>）には足さない。
    /// 進捗は残りの目安であって時刻ではないため、常にファイル先頭基準で報告する。
    /// </remarks>
    public async Task<FileTranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        FileTranscriptionOptions options,
        SpeakerDiarizationService? diarization,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        // REQ-TRX-FILE-17: ライブ用と同じモデルなら共有し、違えばこの実行の間だけ 2 つ目を作る。
        WhisperFactory factory;
        WhisperFactory? ownFactory = null;
        if (ShouldShareLiveFactory(options.ModelPath, _loadedModelPath, _factory != null))
        {
            factory = _factory!;
        }
        else
        {
            var modelPath = options.ModelPath ?? _loadedModelPath;
            if (string.IsNullOrEmpty(modelPath))
            {
                return new FileTranscriptionResult(
                    FileTranscriptionOutcome.ModelLoadFailed, "Whisperモデルが選ばれていません。");
            }

            progress?.Report(new FileTranscriptionProgress(LoadModelPhase, TimeSpan.Zero, TimeSpan.Zero));
            var (loaded, loadError) = TryCreateFactory(
                modelPath, options.UseGpu, ShouldUseFlashAttention(UseFlashAttentionOnGpu, options.UseGpu));
            if (loaded == null)
            {
                return new FileTranscriptionResult(FileTranscriptionOutcome.ModelLoadFailed, loadError);
            }
            ownFactory = loaded;
            factory = loaded;
        }

        // 前の結果を上書きしない（REQ-TRX-FILE-05 / T208）
        string outputPath = BuildAvailableTranscriptPath(audioFilePath, options.MeetingName, File.Exists);
        var startOffset = options.StartOffset;
        var language = options.Language;
        try
        {
            ct.ThrowIfCancellationRequested();

            // diarization が null なら従来どおりストリーミングで処理する（REQ-TRX-DIA-03）。
            // 非 null のときだけ、音声全体をメモリへ載せる経路へ分岐する（NFR-07）。
            var work = diarization == null
                ? TranscribeFileCoreAsync(factory, audioFilePath, outputPath, startOffset, language, progress, ct)
                : TranscribeFileWithDiarizationAsync(
                    factory, audioFilePath, outputPath, startOffset, language, options.KnownSpeakerCount,
                    diarization, progress, ct);
            var ok = await work.ConfigureAwait(false);
            return ok
                ? new FileTranscriptionResult(FileTranscriptionOutcome.Completed, OutputPath: outputPath)
                : new FileTranscriptionResult(FileTranscriptionOutcome.Failed);
        }
        catch (OperationCanceledException)
        {
            // 部分出力ファイルは削除する（ユーザーが完結したと誤認しないように）。
            // Core を抜ける時点で using が StreamWriter を破棄済みのため、ここで削除できる。
            DeletePartialOutput(outputPath, diarization);
            throw;
        }
        // CA1031: キャンセル判定のため全例外をいったん見る必要がある（次行のコメント参照）。
        //         キャンセル以外は Error イベントに変換して Failed を返す。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            // Whisper のネイティブ処理はキャンセル時に OperationCanceledException 以外を
            // 投げることがあるため、トークンがキャンセル済みなら中止として扱う
            if (ct.IsCancellationRequested)
            {
                DeletePartialOutput(outputPath, diarization);
                throw new OperationCanceledException(ct);
            }
            Error?.Invoke($"ファイル文字起こしエラー: {ex.Message}");
            return new FileTranscriptionResult(FileTranscriptionOutcome.Failed);
        }
#pragma warning restore CA1031
        finally
        {
            // 2 つ目の factory はこの実行の間だけ（完了・失敗・中止のいずれでも破棄する）
            ownFactory?.Dispose();
        }
    }

    /// <summary>進捗表示に出すフェーズ名（REQ-TRX-FILE-17。2 つ目のモデルを読み込んでいる間）。</summary>
    internal const string LoadModelPhase = "モデル読み込み中";

    /// <summary>
    /// ライブ用に読み込み済みの factory を共有できるか（REQ-TRX-FILE-17）。
    /// 指定が無い（<c>null</c>）か、実パスが読み込み済みのモデルと同じで、かつ読み込み済みなら共有する。
    /// </summary>
    internal static bool ShouldShareLiveFactory(string? requestedPath, string? loadedPath, bool isLoaded)
    {
        if (!isLoaded || string.IsNullOrEmpty(loadedPath))
        {
            return false;
        }

        return requestedPath == null
            || string.Equals(requestedPath.Trim(), loadedPath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ファイル文字起こし用の 2 つ目の factory を作る（REQ-TRX-FILE-17）。
    /// ライブ用の <see cref="LoadModel"/> と同じく <c>CreateBuilder()</c> を 1 度呼んで失敗を確定させる（T122）。
    /// </summary>
    /// <param name="flashAttention">flash attention を使うか（REQ-GPU-07。GPU で読み込むときだけ <c>true</c> にする）。</param>
    /// <returns>成功なら factory、失敗なら理由。</returns>
    private static (WhisperFactory? Factory, string? Error) TryCreateFactory(
        string modelPath, bool useGpu, bool flashAttention = false)
    {
        if (!File.Exists(modelPath))
        {
            return (null, $"モデルファイルが見つかりません: {modelPath}");
        }

        WhisperFactory? factory = null;
        try
        {
            factory = WhisperFactory.FromPath(
                modelPath, new WhisperFactoryOptions { UseGpu = useGpu, UseFlashAttention = flashAttention });
            _ = factory.CreateBuilder();
            return (factory, null);
        }
        // CA1031: Whisper のネイティブ読み込みは環境依存の任意の例外を投げる。
        //         失敗は理由として呼び出し元へ返し、ダイアログに表示する。
#pragma warning disable CA1031
        catch (Exception ex)
        {
            factory?.Dispose();
            return (null, $"Whisperモデル読み込み失敗 ({Path.GetFileName(modelPath)}): {ex.Message}");
        }
#pragma warning restore CA1031
    }

    // 破棄対象（reader / writer / processor）を using で束ねるために本体を切り出している。
    // 例外は using による破棄が完了してから呼び出し元へ伝播する。
    private async Task<bool> TranscribeFileCoreAsync(
        WhisperFactory factory,
        string audioFilePath,
        string outputPath,
        TimeSpan startOffset,
        string language,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken ct)
    {
        await using var reader = OpenAudioFile(audioFilePath);
        int sourceRate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        TimeSpan totalTime = reader.TotalTime;
        float alpha = (float)(Math.PI * TargetRate / (Math.PI * TargetRate + sourceRate));

        await using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
        await using var processor = WithLanguageOption(factory.CreateBuilder(), language).Build();

        // ファイル読み込みバッファ（約1秒分）
        var readBuffer = new float[sourceRate * channels];
        var pcm16kBuffer = new List<float>(BufferThresholdSamples + TargetRate);
        double resamplePos = 0;
        float lpfPrev = 0f;
        TimeSpan chunkOffset = TimeSpan.Zero;
        const string label = FileSourceLabel;

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, TimeSpan.Zero, totalTime));

        int samplesRead;
        while ((samplesRead = reader.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            DownmixResampleAppend(
                readBuffer, samplesRead, channels, sourceRate,
                alpha, ref resamplePos, ref lpfPrev, pcm16kBuffer);

            while (pcm16kBuffer.Count >= BufferThresholdSamples)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new float[BufferThresholdSamples];
                pcm16kBuffer.CopyTo(0, chunk, 0, BufferThresholdSamples);
                pcm16kBuffer.RemoveRange(0, BufferThresholdSamples);

                await ProcessFileChunkAsync(
                        processor, chunk, startOffset + chunkOffset, label, writer, ct)
                    .ConfigureAwait(false);
                chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
                progress?.Report(new FileTranscriptionProgress(TranscribePhase, chunkOffset, totalTime));
            }
        }

        // 残りバッファ（末尾の短い発話を落とさないよう 0.2 秒以上あれば処理）
        if (pcm16kBuffer.Count >= MinTailSamples)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = pcm16kBuffer.ToArray();
            await ProcessFileChunkAsync(
                    processor, chunk, startOffset + chunkOffset, label, writer, ct)
                .ConfigureAwait(false);
            chunkOffset += TimeSpan.FromSeconds((double)chunk.Length / TargetRate);
        }

        progress?.Report(new FileTranscriptionProgress(TranscribePhase, totalTime, totalTime));
        return true;
    }

    // internal なのは、中止の注記をどちらの文言にするかを ViewModel がフェーズで
    // 決めるためである（REQ-TRX-FILE-07）。表示名をここだけで持ち、写しを作らない。

    /// <summary>進捗表示に出すフェーズ名（REQ-TRX-FILE-06）。</summary>
    internal const string TranscribePhase = "処理中";

    /// <summary>進捗表示に出すフェーズ名（話者ダイアライゼーション有効時のみ現れる）。</summary>
    internal const string DiarizePhase = "話者識別中";

    /// <summary>ファイル文字起こしの出力行に付けるラベル。</summary>
    internal const string FileSourceLabel = "ファイル";

    /// <param name="chunkStart">
    /// このチャンク先頭のタイムスタンプ。開始時刻の指定（REQ-TRX-FILE-10）を含んだ値が渡る。
    /// 未指定ならファイル先頭からの経過時間そのもの。
    /// </param>
    private async Task ProcessFileChunkAsync(
        WhisperProcessor processor, float[] samples, TimeSpan chunkStart,
        string label, StreamWriter writer, CancellationToken ct)
    {
        foreach (var region in SplitVoicedRegions(samples, SilenceCut))
        {
            ct.ThrowIfCancellationRequested();

            var regionOffset = RegionStart(chunkStart, region.Start);
            var regionSamples = PadToMinimum(SliceRegion(samples, region), MinWhisperSamples);

            await foreach (var segment in processor.ProcessAsync(regionSamples, ct).ConfigureAwait(false))
            {
                // 行頭の話者名の付け札は落とす（REQ-TRX-LIVE-20。T194）
                var text = StripSpeakerTag(segment.Text?.Trim() ?? "");
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var startTime = regionOffset + segment.Start;
                var endTime = regionOffset + segment.End;
                var line = $"[{startTime:hh\\:mm\\:ss} - {endTime:hh\\:mm\\:ss}] [{label}] {text}";
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                SegmentTranscribed?.Invoke(line);
            }
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
    }
}