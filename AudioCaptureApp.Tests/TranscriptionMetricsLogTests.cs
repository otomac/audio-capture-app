using AudioCaptureApp.Services;

namespace AudioCaptureApp.Tests;

/// <summary>計測ログ（T185 / REQ-CFG-12）。本文は書かず、数値だけを不変カルチャで書く。</summary>
public sealed class TranscriptionMetricsLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "acapp-metrics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void TranscriptionMetricsLog_WritesHeaderAndRows_WithoutText()
    {
        var path = Path.Combine(_folder, "logs", "metrics.tsv");
        var errors = new List<string>();

        using (var log = TranscriptionMetricsLog.Open(path, errors.Add))
        {
            Assert.NotNull(log);
            log.WriteFinal(TimeSpan.FromSeconds(12.5), AudioSourceType.Speaker, 16000 * 4, 16000 * 2, 812.25, 3, behind: true, 7.5);
            log.WriteProvisional(TimeSpan.FromSeconds(13), AudioSourceType.Mic, 8000, 16000, 120, 1, 0, 1.2);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(
            [
                TranscriptionMetricsLog.Header,
                "12.500\tfinal\tspeaker\t4.000\t2.000\t812.250\t3\t1\t7.500\t",
                "13.000\tprovisional\tmic\t0.500\t1.000\t120.000\t1\t\t0.000\t1.200"
            ],
            lines);
        Assert.Empty(errors);
    }

    [Fact]
    public void TranscriptionMetricsLog_OpenFailure_DisablesAndReportsOnce()
    {
        // フォルダを作るべき位置にファイルがある → 開けない
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "");
        var errors = new List<string>();

        using var log = TranscriptionMetricsLog.Open(Path.Combine(blocker, "metrics.tsv"), errors.Add);

        Assert.Null(log);
        Assert.Single(errors);
    }

    [Fact]
    public void TranscriptionMetricsLog_WriteAfterDispose_IsIgnored()
    {
        // 停止がタイムアウトしてワーカーが残っていても、閉じた後の書き込みで落ちない
        var path = Path.Combine(_folder, "metrics.tsv");
        var errors = new List<string>();
        var log = TranscriptionMetricsLog.Open(path, errors.Add);
        Assert.NotNull(log);
        log.Dispose();

        log.WriteFinal(TimeSpan.Zero, AudioSourceType.Mic, 0, 0, 0, 0, behind: false, 0);

        Assert.Single(File.ReadAllLines(path));
        Assert.Empty(errors);
    }

    [Fact]
    public void PathFor_UsesSessionStartTimeInAppDataLogs()
    {
        var path = TranscriptionMetricsLog.PathFor(new DateTime(2026, 10, 12, 9, 5, 7));

        Assert.Equal("metrics_20261012_090507.tsv", Path.GetFileName(path));
        Assert.Equal("logs", Path.GetFileName(Path.GetDirectoryName(path)));
    }
}