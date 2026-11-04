using BeatzOnKeys.Core.Models;

namespace BeatzOnKeys.Core.Audio;

public interface IAudioFragmentPlayer : IDisposable
{
    event Action<Exception>? PlaybackFailed;
    double Volume { get; set; }
    bool IsPlaying { get; }
    TimeSpan LoadTrack(PlaybackTrack track);
    void PrepareOutput();
    void PlayFragment(PlaybackStep step);
    void Pause();
    void Unload();
}
