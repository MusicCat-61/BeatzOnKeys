namespace BeatzOnKeys.Core.Models;

/// <summary>Persistent processing state of an imported audio track.</summary>
public enum TrackStatus
{
    Pending,
    Processing,
    Ready,
    Error
}
