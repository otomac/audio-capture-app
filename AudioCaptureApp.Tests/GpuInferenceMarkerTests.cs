using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>
/// GPU で推論中の印（T199 / REQ-GPU-08）。推論の前に置き、正常に終えたら消す。次の起動で残っていれば
/// 「前回は GPU で推論している最中に終わった」とみなす。
/// </summary>
public sealed class GpuInferenceMarkerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "acapp-gpumarker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private GpuInferenceMarker MarkerIn(params string[] parts)
        => new(Path.Combine([_folder, .. parts, "gpu-inference.marker"]));

    [Fact]
    public void TakeLeftover_WithoutMarker_ReturnsFalse()
    {
        Assert.False(MarkerIn().TakeLeftover());
    }

    [Fact]
    public void TakeLeftover_AfterSetWithoutClear_ReturnsTrueOnlyOnce()
    {
        // 推論中に PC が固まった → 消されないまま次の起動を迎える
        var marker = MarkerIn();
        marker.Set("live");

        Assert.True(marker.TakeLeftover());
        Assert.False(marker.TakeLeftover());   // 取り出したら消えている（同じ印で 2 度倒さない）
    }

    [Fact]
    public void TakeLeftover_AfterClear_ReturnsFalse()
    {
        var marker = MarkerIn();
        marker.Set("file");
        marker.Clear();

        Assert.False(marker.TakeLeftover());
    }

    [Fact]
    public void Set_CreatesMissingFolder()
    {
        // 初回の起動では %APPDATA%\AudioCaptureApp がまだ無いことがある
        var marker = MarkerIn("AudioCaptureApp");
        marker.Set("live");

        Assert.True(marker.TakeLeftover());
    }

    [Fact]
    public void SetAndClear_WhenPathIsBlocked_DoNotThrow()
    {
        // フォルダを作るべき位置にファイルがある → 置けない・消せない。録音と文字起こしは止めない（D8）
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "blocker"), "");
        var marker = MarkerIn("blocker");

        marker.Set("live");
        marker.Clear();

        Assert.False(marker.TakeLeftover());
    }

    [Fact]
    public void DefaultPath_IsNextToSettings()
    {
        var path = GpuInferenceMarker.DefaultPath;

        Assert.Equal("gpu-inference.marker", Path.GetFileName(path));
        Assert.Equal("AudioCaptureApp", Path.GetFileName(Path.GetDirectoryName(path)));
    }
}