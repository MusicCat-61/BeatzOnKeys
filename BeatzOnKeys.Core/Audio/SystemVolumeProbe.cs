namespace BeatzOnKeys.Core.Audio;

/// <summary>
/// Reads the Windows endpoint volume so the app can explain a session that is running but
/// inaudible. BeatzOnKeys multiplies its own gain by the device volume: at 6% system volume
/// even the maximum typing level is practically silent, and users report that as "no sound".
/// </summary>
public static class SystemVolumeProbe
{
    /// <summary>Default render device level in 0..1, and whether that device is muted.</summary>
    public readonly record struct OutputVolume(double Level, bool Muted)
    {
        /// <summary>True when the output is muted or so quiet that playback cannot be heard.</summary>
        public bool IsEffectivelySilent => Muted || !double.IsFinite(Level) || Level < 0.2;

        public int Percent => (int)Math.Round(Math.Clamp(double.IsFinite(Level) ? Level : 0, 0, 1) * 100);
    }

    /// <summary>Current default output level, or null when no device can be queried.</summary>
    public static OutputVolume? TryRead()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
            var endpoint = device.AudioEndpointVolume;
            return new OutputVolume(endpoint.MasterVolumeLevelScalar, endpoint.Mute);
        }
        catch
        {
            // Diagnostics must never break playback; an unreadable device simply yields no hint.
            return null;
        }
    }
}