using AudioCaptureApp.ViewModels;

namespace AudioCaptureApp.Tests;

public class MainViewModelTests
{
    [Fact]
    public void PeakToDb_UnitPeak_ReturnsZeroDb()
    {
        var db = MainViewModel.PeakToDb(1.0f);

        Assert.Equal(0.0, db, precision: 1);
    }

    [Fact]
    public void PeakToDb_ZeroPeak_ReturnsMinDb()
    {
        var db = MainViewModel.PeakToDb(0.0f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_NegativePeak_ReturnsMinDb()
    {
        var db = MainViewModel.PeakToDb(-0.5f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_HalfPeak_ReturnsApproxMinus6Db()
    {
        var db = MainViewModel.PeakToDb(0.5f);

        // 20 * log10(0.5) ≈ -6.02
        Assert.Equal(-6.02, db, precision: 1);
    }

    [Fact]
    public void PeakToDb_OverUnitPeak_ClampsToMaxDb()
    {
        // 20 * log10(1.414) ≈ 3.01 → clamped to 3.0
        var db = MainViewModel.PeakToDb(1.414f);

        Assert.Equal(3.0, db);
    }

    [Fact]
    public void PeakToDb_VerySmallPeak_ClampsToMinDb()
    {
        // 20 * log10(0.000001) = -120 → clamped to -60
        var db = MainViewModel.PeakToDb(0.000001f);

        Assert.Equal(-60.0, db);
    }

    [Fact]
    public void PeakToDb_TenthPeak_ReturnsMinus20Db()
    {
        var db = MainViewModel.PeakToDb(0.1f);

        // 20 * log10(0.1) = -20
        Assert.Equal(-20.0, db, precision: 1);
    }

    // --- 成果物フォルダを開く (T111) ---

    [Fact]
    public void BuildExplorerArguments_ExistingFile_SelectsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"acapp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            var args = MainViewModel.BuildExplorerArguments(file);

            Assert.Equal($"/select,\"{file}\"", args);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildExplorerArguments_MissingFileButExistingFolder_OpensFolder()
    {
        // 成果物が削除されていても、置かれていたフォルダは開く（REQ-OPEN-03）
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var missing = Path.Combine(folder, $"acapp-missing-{Guid.NewGuid():N}.txt");

        var args = MainViewModel.BuildExplorerArguments(missing);

        Assert.Equal($"\"{folder}\"", args);
    }

    [Fact]
    public void BuildExplorerArguments_MissingFileAndFolder_ReturnsNull()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), $"acapp-nodir-{Guid.NewGuid():N}", "result.txt");

        Assert.Null(MainViewModel.BuildExplorerArguments(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildExplorerArguments_BlankPath_ReturnsNull(string path)
    {
        // 起動直後など成果物が未設定の状態（REQ-OPEN-04 と対になる防御）
        Assert.Null(MainViewModel.BuildExplorerArguments(path));
    }

    // --- 作業中は保存先を開けない (T121) ---

    [Fact]
    public void CanOpenResultFolder_IdleWithResult_IsTrue()
    {
        Assert.True(MainViewModel.CanOpenResultFolderFor(@"C:\out\20260818_120000.txt", isNotBusy: true));
    }

    [Fact]
    public void CanOpenResultFolder_Busy_IsFalse()
    {
        // 録音中・停止処理中・ファイル文字起こし中は、保持しているのが直前の成果物のため無効
        // （REQ-OPEN-05）
        Assert.False(MainViewModel.CanOpenResultFolderFor(@"C:\out\20260818_120000.txt", isNotBusy: false));
    }

    [Fact]
    public void CanOpenResultFolder_IdleWithoutResult_IsFalse()
    {
        // 起動直後（REQ-OPEN-04）
        Assert.False(MainViewModel.CanOpenResultFolderFor("", isNotBusy: true));
    }

    // --- ファイル文字起こしの開始時刻 (T113 / REQ-TRX-FILE-10) ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseStartTime_Blank_ReturnsZero(string text)
    {
        // 空欄は「未指定」。これまでどおりファイル先頭を 00:00:00 として出力する
        Assert.True(MainViewModel.TryParseStartTime(text, out var startTime));
        Assert.Equal(TimeSpan.Zero, startTime);
    }

    [Theory]
    [InlineData("9:05", 9, 5)]
    [InlineData("09:05", 9, 5)]
    [InlineData("0:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData(" 14:30 ", 14, 30)]
    public void TryParseStartTime_ValidForms_AreAccepted(string text, int hours, int minutes)
    {
        Assert.True(MainViewModel.TryParseStartTime(text, out var startTime));
        Assert.Equal(new TimeSpan(hours, minutes, 0), startTime);
    }

    [Theory]
    [InlineData("24:00")]   // 24 時は存在しない
    [InlineData("9:5")]     // 分は 2 桁
    [InlineData("12:60")]   // 分が範囲外
    [InlineData("12")]      // 区切りが無い
    [InlineData("12:34:56")]// 秒は受け付けない
    [InlineData("あ:い")]
    public void TryParseStartTime_InvalidForms_AreRejected(string text)
    {
        // 拒否できないと、不正な時刻のまま文字起こしが走り出す
        Assert.False(MainViewModel.TryParseStartTime(text, out _));
    }

    // --- ファイル文字起こしの進捗率 (T113 / REQ-TRX-FILE-06) ---

    [Fact]
    public void FileTranscriptionProgress_ZeroTotal_ReturnsZero()
    {
        // 総時間を取れないファイルでゼロ除算しない
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromSeconds(5), TimeSpan.Zero);

        Assert.Equal(0.0, value);
    }

    [Fact]
    public void FileTranscriptionProgress_HalfProcessed_ReturnsFifty()
    {
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.Equal(50.0, value);
    }

    [Fact]
    public void FileTranscriptionProgress_ProcessedExceedsTotal_ClampsTo100()
    {
        // 最終チャンクは総時間を跨ぐことがある（20 秒単位で切り出すため）
        var value = MainViewModel.FileTranscriptionProgressFor(
            TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(10));

        Assert.Equal(100.0, value);
    }
}