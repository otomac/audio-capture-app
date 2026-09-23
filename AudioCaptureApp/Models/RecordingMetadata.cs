using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace AudioCaptureApp.Models;

/// <summary>
/// 録音のメタデータ（REQ-META-01）。JSON のフィールド名は日本語のまま `会議名` / `実施日時` / `参加者`。
/// </summary>
public class RecordingMetadata
{
    [JsonPropertyName("会議名")]
    public string MeetingName { get; set; } = "";

    /// <summary>実施日時。書式は固定しない自由記述（既定は `yyyy-MM-dd HH:mm〜HH:mm`）。</summary>
    [JsonPropertyName("実施日時")]
    public string HeldAt { get; set; } = "";

    [JsonPropertyName("参加者")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<string> Participants { get; } = [];
}