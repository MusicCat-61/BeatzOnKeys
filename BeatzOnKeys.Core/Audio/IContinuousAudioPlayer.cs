using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Audio;

public interface IContinuousAudioPlayer : IDisposable
{
    event Action<PlaybackFailureEventArgs>? PlaybackFailed;
    event Action<PlaybackEndedEventArgs>? TrackEnded;
    Guid PlaybackId { get; }
    double MinVolume { get; set; }
    double MaxVolume { get; set; }
    double IdleTimeout { get; set; }
    bool PauseOnIdle { get; set; }
    bool IsPlaying { get; }
    bool IsIdlePaused { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    TimeSpan LoadTrack(PlaybackTrack track);
    void Start();
    void NotifyKeyPress();
    void Stop();
    void Unload();
}
