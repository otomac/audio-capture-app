using System.IO;
using System.Text.Json;
using AudioCaptureApp.Models;

namespace AudioCaptureApp.Services;

public class SettingsService
{
    private readonly string _settingsFolder;
    private readonly string _settingsFilePath;

    // 他に依存しない自己完結の値のみ宣言時に初期化する。
    // パスは互いに派生関係があるためコンストラクターにまとめる。
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        _settingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioCaptureApp");
        _settingsFilePath = Path.Combine(_settingsFolder, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
        }
        // 読み込みに失敗した設定ファイルは既定値で置き換える（起動を妨げない）。
        // 触れるのは File.ReadAllText と JsonSerializer だけなので、型を列挙できる。
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_settingsFolder);
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(_settingsFilePath, json);
    }
}