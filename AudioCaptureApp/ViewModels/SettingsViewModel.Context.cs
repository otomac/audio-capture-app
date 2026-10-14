using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioCaptureApp.ViewModels;

// SettingsViewModel のうち、確定パスの文脈プロンプトに渡す用語集（REQ-SETWIN-03 ⑧ / REQ-CFG-13）を担当する部分（T188）。
public sealed partial class SettingsViewModel
{
    /// <summary>
    /// 用語集（1 行 1 語。REQ-CFG-13）。入力のたびに保存する（Esc で閉じても失われない）。次の録音から効く。
    /// </summary>
    [ObservableProperty]
    private string _transcriptionGlossary = "";

    partial void OnTranscriptionGlossaryChanged(string value)
    {
        _main.TranscriptionService.TranscriptionGlossary = value;
        if (!_initializing)
        {
            _main.SaveSettings();
        }
    }
}