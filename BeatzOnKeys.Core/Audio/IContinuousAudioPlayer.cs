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
    bool PunchResponseEnabled { get; set; }
    double BassReactiveGain { get; set; }
    bool StereoPulseEnabled { get; set; }
    bool AdaptiveVolumeEnabled { get; set; }
    double EffectiveMinVolume { get; }
    bool IsPlaying { get; }
    bool IsIdlePaused { get; }

    /// <summary>True while the user paused playback explicitly; the position is kept.</summary>
    bool IsPaused { get; }

    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    TimeSpan LoadTrack(PlaybackTrack track);
    void Start();

    /// <summary>Stops the audio without moving the decoder, so resuming continues the same run.</summary>
    void Pause();

    /// <summary>Continues exactly where <see cref="Pause"/> stopped.</summary>
    void Resume();

    void NotifyKeyPress();
    void Stop();
    void Unload();
}