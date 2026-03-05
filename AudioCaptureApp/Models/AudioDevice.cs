namespace AudioCaptureApp.Models;

public class AudioDevice
{
    public required string DeviceId { get; init; }
    public required string FriendlyName { get; init; }
    public bool IsDefault { get; init; }

    public override string ToString() => FriendlyName;
}
