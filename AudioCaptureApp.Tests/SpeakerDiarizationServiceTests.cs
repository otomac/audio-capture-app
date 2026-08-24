using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>
/// 起動時のモデル存在検査（REQ-TRX-DIA-15）。**モデルの読み込みは行わない**ため、
/// sherpa-onnx のネイティブライブラリも実モデルも要らない。
/// </summary>
public class SpeakerDiarizationServiceTests
{
    private static SpeakerDiarizationOptions OptionsFor(string segmentation, string embedding)
        => new(segmentation, embedding, clusteringThreshold: 0.5, knownSpeakerCount: null, numThreads: 1);

    [Fact]
    public void ModelFilesExist_BothPresent_IsTrue()
    {
        var segmentation = Path.Combine(Path.GetTempPath(), $"acapp-seg-{Guid.NewGuid():N}.onnx");
        var embedding = Path.Combine(Path.GetTempPath(), $"acapp-emb-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(segmentation, "x");
        File.WriteAllText(embedding, "x");
        try
        {
            Assert.True(SpeakerDiarizationService.ModelFilesExist(OptionsFor(segmentation, embedding)));
        }
        finally
        {
            File.Delete(segmentation);
            File.Delete(embedding);
        }
    }

    [Fact]
    public void ModelFilesExist_OneMissing_IsFalse()
    {
        // 片方だけ置いた状態で「有効」と表示すると、実行時に初めて失敗が分かることになる
        var segmentation = Path.Combine(Path.GetTempPath(), $"acapp-seg-{Guid.NewGuid():N}.onnx");
        var missing = Path.Combine(Path.GetTempPath(), $"acapp-missing-{Guid.NewGuid():N}.onnx");
        File.WriteAllText(segmentation, "x");
        try
        {
            Assert.False(SpeakerDiarizationService.ModelFilesExist(OptionsFor(segmentation, missing)));
            Assert.False(SpeakerDiarizationService.ModelFilesExist(OptionsFor(missing, segmentation)));
        }
        finally
        {
            File.Delete(segmentation);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ModelFilesExist_BlankPath_IsFalse(string blank)
    {
        // File.Exists("") の戻り値に頼らず、明示的に弾いていることを固定する
        Assert.False(SpeakerDiarizationService.ModelFilesExist(OptionsFor(blank, blank)));
    }
}