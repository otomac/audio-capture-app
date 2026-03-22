namespace AudioCaptureApp.Models;

public class RecordingSession
{
    public required string FilePath { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? StoppedAt { get; set; }
    public required string DeviceId { get; init; }
}