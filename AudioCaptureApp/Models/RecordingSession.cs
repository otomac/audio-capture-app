namespace AudioCaptureApp.Models;

public class RecordingSession
{
    /// <summary>録音ファイルのパス。メタデータの会議名で改名したら追従する（REQ-META-02）。</summary>
    public required string FilePath { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? StoppedAt { get; set; }
    public required string DeviceId { get; init; }
}