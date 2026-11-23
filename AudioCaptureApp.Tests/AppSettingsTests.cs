using System.Text.Json;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new AppSettings();

        Assert.Contains("AudioCapture", settings.OutputFolder, StringComparison.Ordinal);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.Null(settings.LastSelectedLoopbackDeviceId);
        Assert.False(settings.TranscriptionEnabled);
        Assert.Contains("ggml-small.bin", settings.WhisperModelPath, StringComparison.Ordinal);
        Assert.True(settings.UseGpuForTranscription);
        // 従来のハードコード（WithLanguage("ja")）と同じ挙動を既定にする（REQ-CFG-07）
        Assert.Equal("ja", settings.LiveTranscriptionLanguage);
        Assert.Equal("ja", settings.FileTranscriptionLanguage);
        Assert.Equal(0.01, settings.SilenceRmsThreshold);
        Assert.Equal(2.0, settings.SilenceMergeGapSeconds);
        Assert.Equal(0.2, settings.VoicedPaddingSeconds);
        // ライブの区切りは T199 で合格した構成（REQ-CFG-15 / ADR-0009）
        Assert.Equal(6.0, settings.LiveChunkMaxSeconds);
        Assert.Equal(0.8, settings.LiveEndpointSilenceSeconds);
        // 話者ダイアライゼーションは既定で無効（REQ-TRX-DIA-03）。
        Assert.False(settings.SpeakerDiarizationEnabled);
        Assert.Contains("diarization", settings.SpeakerSegmentationModelPath, StringComparison.Ordinal);
        Assert.Contains("diarization", settings.SpeakerEmbeddingModelPath, StringComparison.Ordinal);
        Assert.Equal(0.5, settings.SpeakerClusteringThreshold);
        // 話者数は既定で未指定。固定値を既定にしてはならない（REQ-TRX-DIA-07）。
        Assert.Null(settings.KnownSpeakerCount);
        // 推論スレッド数の既定は環境依存（REQ-TRX-DIA-14）。結果は変わらず速度だけが変わる設定なので、
        // 論理コア数の少ない環境で 4 を強制しないよう min を取る。
        Assert.Equal(Math.Min(4, Environment.ProcessorCount), settings.SpeakerDiarizationThreads);
        // 録音の自動開始は既定で OFF（REQ-CFG-10）。勝手に録音が始まる既定にしてはならない
        Assert.False(settings.AutoStartRecordingEnabled);
        // 実測にもとづく既定（REQ-REC-12 / T176）
        Assert.Equal(-15.0, settings.AutoStartThresholdDb);
        Assert.Equal(1.0, settings.AutoStartSustainSeconds);
        Assert.Equal(0.3, settings.AutoStartDipGraceSeconds);
        Assert.Equal(10.0, settings.AutoStartCooldownSeconds);
        // 計測ログは既定で書かない（REQ-CFG-12）
        Assert.False(settings.TranscriptionMetricsLogEnabled);
        // 速報は既定で OFF（REQ-CFG-12 / ADR-0009。以前は ON）。速報用モデルも未選択
        Assert.False(settings.LiveProvisionalEnabled);
        Assert.Null(settings.ProvisionalWhisperModelName);
        // T182 の実測で合格した組み合わせ
        Assert.Equal(1.0, settings.ProvisionalIntervalSeconds);
        Assert.Equal(1.0, settings.ProvisionalDutyCap);
        Assert.Equal(4, settings.ProvisionalThreads);
        Assert.Equal("speaker", settings.ProvisionalSources);
        Assert.Equal(8.0, settings.ProvisionalMaxPhraseSeconds);
        // 文脈プロンプトは既定で ON（T182 の M6）。用語集は既定で空（REQ-CFG-12 / 13）
        Assert.True(settings.LiveContextPromptEnabled);
        Assert.Equal("", settings.TranscriptionGlossary);
        // flash attention は GPU のときだけ使う設定で、既定は ON（REQ-CFG-14 / T191）
        Assert.True(settings.UseFlashAttentionOnGpu);
    }

    [Fact]
    public void JsonRoundTrip_PreservesValues()
    {
        var original = new AppSettings
        {
            OutputFolder = @"C:\test\output",
            LastSelectedDeviceId = "device-123",
            LastSelectedLoopbackDeviceId = "loopback-456",
            TranscriptionEnabled = true,
            WhisperModelPath = @"C:\models\test.bin",
            UseGpuForTranscription = false,
            SilenceRmsThreshold = 0.02,
            SilenceMergeGapSeconds = 1.5,
            VoicedPaddingSeconds = 0.3,
            LiveChunkMaxSeconds = 10.0,
            LiveEndpointSilenceSeconds = 2.0,
            LiveProvisionalEnabled = true,
            SpeakerDiarizationEnabled = true,
            SpeakerSegmentationModelPath = @"C:\models\seg.onnx",
            SpeakerEmbeddingModelPath = @"C:\models\emb.onnx",
            SpeakerClusteringThreshold = 0.7,
            KnownSpeakerCount = 3,
            SpeakerDiarizationThreads = 4
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(original.OutputFolder, deserialized.OutputFolder);
        Assert.Equal(original.LastSelectedDeviceId, deserialized.LastSelectedDeviceId);
        Assert.Equal(original.LastSelectedLoopbackDeviceId, deserialized.LastSelectedLoopbackDeviceId);
        Assert.Equal(original.TranscriptionEnabled, deserialized.TranscriptionEnabled);
        Assert.Equal(original.WhisperModelPath, deserialized.WhisperModelPath);
        Assert.Equal(original.UseGpuForTranscription, deserialized.UseGpuForTranscription);
        Assert.Equal(original.SilenceRmsThreshold, deserialized.SilenceRmsThreshold);
        Assert.Equal(original.SilenceMergeGapSeconds, deserialized.SilenceMergeGapSeconds);
        Assert.Equal(original.VoicedPaddingSeconds, deserialized.VoicedPaddingSeconds);
        Assert.Equal(original.LiveChunkMaxSeconds, deserialized.LiveChunkMaxSeconds);
        Assert.Equal(original.LiveEndpointSilenceSeconds, deserialized.LiveEndpointSilenceSeconds);
        Assert.Equal(original.LiveProvisionalEnabled, deserialized.LiveProvisionalEnabled);
        Assert.Equal(original.SpeakerDiarizationEnabled, deserialized.SpeakerDiarizationEnabled);
        Assert.Equal(original.SpeakerSegmentationModelPath, deserialized.SpeakerSegmentationModelPath);
        Assert.Equal(original.SpeakerEmbeddingModelPath, deserialized.SpeakerEmbeddingModelPath);
        Assert.Equal(original.SpeakerClusteringThreshold, deserialized.SpeakerClusteringThreshold);
        Assert.Equal(original.KnownSpeakerCount, deserialized.KnownSpeakerCount);
        Assert.Equal(original.SpeakerDiarizationThreads, deserialized.SpeakerDiarizationThreads);
    }

    [Fact]
    public void JsonRoundTrip_PreservesWhisperModelList()
    {
        // REQ-CFG-08: エイリアス付きの一覧が往復すること。既定は空（旧設定は読み込み時に移行する）
        Assert.Empty(new AppSettings().WhisperModelList);

        var original = new AppSettings();
        original.WhisperModelList.Add(new WhisperModelEntry { ModelName = "small", ModelPath = @"C:\m\small.bin" });
        original.WhisperModelList.Add(new WhisperModelEntry { ModelName = "large", ModelPath = @"C:\m\large.bin" });

        var deserialized = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original));

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.WhisperModelList.Count);
        Assert.Equal("small", deserialized.WhisperModelList[0].ModelName);
        Assert.Equal(@"C:\m\large.bin", deserialized.WhisperModelList[1].ModelPath);
    }

    [Fact]
    public void JsonRoundTrip_PreservesFileWhisperModelName()
    {
        // REQ-CFG-09: 既定は null（＝ライブ用と同じ）。名前が往復すること
        Assert.Null(new AppSettings().FileWhisperModelName);

        var original = new AppSettings { FileWhisperModelName = "large" };
        var deserialized = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original));

        Assert.NotNull(deserialized);
        Assert.Equal("large", deserialized.FileWhisperModelName);
    }

    [Fact]
    public void JsonDeserialization_MissingFields_UsesDefaults()
    {
        var json = "{}";

        var settings = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(settings);
        Assert.Null(settings.LastSelectedDeviceId);
        Assert.False(settings.TranscriptionEnabled);
        Assert.True(settings.UseGpuForTranscription);
        // 既存の settings.json には無音カットの 3 キーが無い。0.0 に束縛されると
        // 無音カットが全ユーザーで無効化されるため、既定値が残ることを固定する。
        Assert.Equal(0.01, settings.SilenceRmsThreshold);
        Assert.Equal(2.0, settings.SilenceMergeGapSeconds);
        Assert.Equal(0.2, settings.VoicedPaddingSeconds);
        // 既存の settings.json には話者ダイアライゼーションのキーも無い。
        // 既定が無効であること（勝手に有効化されないこと）を固定する。
        Assert.False(settings.SpeakerDiarizationEnabled);
        Assert.Equal(0.5, settings.SpeakerClusteringThreshold);
        Assert.Equal(Math.Min(4, Environment.ProcessorCount), settings.SpeakerDiarizationThreads);
        // 既存の settings.json に自動開始のキーは無い。OFF のままであること
        Assert.False(settings.AutoStartRecordingEnabled);
        // 既存の settings.json にライブの区切りのキーは無い。新しい既定で読む（REQ-CFG-15。T200 の既定案）
        Assert.Equal(6.0, settings.LiveChunkMaxSeconds);
        Assert.Equal(0.8, settings.LiveEndpointSilenceSeconds);
        // 速報の導入前の settings.json にはキーが無い。新しい既定（OFF）で読む（REQ-CFG-12）
        Assert.False(settings.LiveProvisionalEnabled);
    }

    [Fact]
    public void JsonDeserialization_SavedProvisionalTrue_IsKept()
    {
        // 既定を OFF にしても、保存済みの true は書き換えない（T201 の既定案）
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"LiveProvisionalEnabled\":true}");

        Assert.NotNull(settings);
        Assert.True(settings.LiveProvisionalEnabled);
    }
}