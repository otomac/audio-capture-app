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
}
