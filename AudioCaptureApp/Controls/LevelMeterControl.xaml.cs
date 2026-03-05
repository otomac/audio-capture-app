using System.Windows;
using System.Windows.Controls;

namespace AudioCaptureApp.Controls;

public partial class LevelMeterControl : UserControl
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(double),
            typeof(LevelMeterControl),
            new PropertyMetadata(-60.0, OnLevelChanged));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private const double MinDb = -60.0;
    private const double MaxDb = 3.0;

    public LevelMeterControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateMeter();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((LevelMeterControl)d).UpdateMeter();
    }

    private void UpdateMeter()
    {
        double totalWidth = MeterGrid.ActualWidth;
        if (totalWidth <= 0) return;

        double db = Math.Clamp(Level, MinDb, MaxDb);
        double fraction = (db - MinDb) / (MaxDb - MinDb); // 0.0 ~ 1.0
        double overlayWidth = totalWidth * (1.0 - fraction);

        OverlayRect.Width = overlayWidth;
    }
}
