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

    // --- ダイアログの話者人数 (T167 / REQ-TRX-DIA-07 / 17) ---

    [Fact]
    public void SpeakerCountOptions_All_HasUnspecifiedOneToNineAndTenPlus()
    {
        var all = SpeakerCountOptions.All;

        // 「指定なし」+ 1〜9 + 「10 人以上」= 11 個。両端は未選択（null）
        Assert.Equal(11, all.Count);
        Assert.Null(all[0].Count);
        Assert.Same(all[0], SpeakerCountOptions.Unspecified);
        for (int count = 1; count <= 9; count++)
        {
            Assert.Equal(count, all[count].Count);
        }
        Assert.Null(all[10].Count);
    }

    [Fact]
    public void EffectiveSpeakerCount_DialogSelected_OverridesSettings()
    {
        // ダイアログの選択を優先する（REQ-TRX-DIA-07）
        Assert.Equal(3, SpeakerDiarizationService.EffectiveSpeakerCount(3, null));
        Assert.Equal(3, SpeakerDiarizationService.EffectiveSpeakerCount(3, 5));
    }

    [Fact]
    public void EffectiveSpeakerCount_Unselected_FallsBackToSettings()
    {
        // 未選択（「指定なし」「10 人以上」）なら設定値。既定（null）は null のまま = 従来と同じ
        Assert.Null(SpeakerDiarizationService.EffectiveSpeakerCount(null, null));
        Assert.Equal(12, SpeakerDiarizationService.EffectiveSpeakerCount(null, 12));
        // 0 以下は未指定に倒す
        Assert.Null(SpeakerDiarizationService.EffectiveSpeakerCount(null, 0));
        Assert.Null(SpeakerDiarizationService.EffectiveSpeakerCount(-1, null));
    }
}