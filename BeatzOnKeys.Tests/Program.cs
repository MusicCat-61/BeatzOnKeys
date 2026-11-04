namespace BeatzOnKeys.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("PlaybackController.ZeroTrackMatrix", PlaybackControllerTests.ZeroTrackMatrix),
        ("PlaybackController.StepAndIdleCursor", PlaybackControllerTests.StepAndIdleCursor),
        ("PlaybackController.TailAndExactBoundary", PlaybackControllerTests.TailAndExactBoundary),
        ("PlaybackController.SingleTrackModeMatrix", PlaybackControllerTests.SingleTrackModeMatrix),
        ("PlaybackController.MultipleTrackModeMatrix", PlaybackControllerTests.MultipleTrackModeMatrix),
        ("PlaybackController.SelectionUpsertRemoveAndClear", PlaybackControllerTests.SelectionUpsertRemoveAndClear),
        ("PlaybackController.VolumeAndStepClamping", PlaybackControllerTests.VolumeAndStepClamping),
        ("PlaybackController.EventsRunOutsideControllerLock", PlaybackControllerTests.EventsRunOutsideControllerLock),
        ("Audio.FragmentUsesExactFramesAndSilenceDoesNotAdvance", AudioFragmentTests.FragmentUsesExactFramesAndSilenceDoesNotAdvance),
        ("Audio.FragmentTailVolumeAndValidation", AudioFragmentTests.FragmentTailVolumeAndValidation),
        ("Audio.PlayerLifecycleAndFailureForwarding", AudioFragmentTests.PlayerLifecycleAndFailureForwarding),
        ("Audio.OldIdleTimerCannotPauseNewFragment", AudioFragmentTests.OldIdleTimerCannotPauseNewFragment),
        ("Audio.ReplayWaitsForDelayedStopAndOutputInitializesOffContext", AudioFragmentTests.ReplayWaitsForDelayedStopAndOutputInitializesOffContext),
        ("Audio.TrackLoaderValidatesPathAndFormat", AudioFragmentTests.TrackLoaderValidatesPathAndFormat),
        ("MP3.RawAudioFileReaderSeekReadProfile", Mp3ShortReadRegressionTests.RawAudioFileReaderSeekReadProfile),
        ("MP3.SequentialReadAndPrerollAvoidSeekPriming", Mp3ShortReadRegressionTests.SequentialReadAndPrerollAvoidSeekPriming),
        ("MP3.ProviderFillsEveryPacketUntilFragmentBudget", Mp3ShortReadRegressionTests.ProviderFillsEveryPacketUntilFragmentBudget),
        ("MP3.RapidMp3SeeksHaveNoIntermediateSilentPacket", Mp3ShortReadRegressionTests.RapidMp3SeeksHaveNoIntermediateSilentPacket),
        ("TypingPlaybackService.StartRequiresTrackAndLifecycleHasOneWorker", TypingPlaybackServiceTests.StartRequiresTrackAndLifecycleHasOneWorker),
        ("TypingPlaybackService.StopAwaitsInFlightAndCancelsQueuedKeys", TypingPlaybackServiceTests.StopAwaitsInFlightAndCancelsQueuedKeys),
        ("TypingPlaybackService.FailedInstallDoesNotLeaveOldWorker", TypingPlaybackServiceTests.FailedInstallDoesNotLeaveOldWorker),
        ("TypingPlaybackService.SelectionDurationAndPlaybackFailure", TypingPlaybackServiceTests.SelectionDurationAndPlaybackFailure),
        ("UpdateService.ReleaseMetadataAndCurrentRelease", UpdateServiceTests.ReleaseMetadataAndCurrentRelease),
        ("UpdateService.NewReleaseAndMissingAssets", UpdateServiceTests.NewReleaseAndMissingAssets),
        ("UpdateService.DraftPrereleaseAndInvalidVersion", UpdateServiceTests.DraftPrereleaseAndInvalidVersion),
        ("UpdateService.HttpStatusesAndMalformedJson", UpdateServiceTests.HttpStatusesAndMalformedJson),
        ("UpdateService.RepositoryAssetUrlsRejectSpoofing", UpdateServiceTests.RepositoryAssetUrlsRejectSpoofing),
        ("UpdateService.TagLocationValidation", UpdateServiceTests.TagLocationValidation),
        ("UpdateService.PayloadRequiresOnlyCurrentRuntimeFiles", UpdateServiceTests.PayloadRequiresOnlyCurrentRuntimeFiles),
        ("Settings.DefaultsAndNormalization", SettingsTests.DefaultsAndNormalization),
        ("Settings.RoundTripAllFieldsAndDelete", SettingsTests.RoundTripAllFieldsAndDelete),
        ("Settings.LegacyUnknownPropertiesAreIgnored", SettingsTests.LegacyUnknownPropertiesAreIgnored),
        ("Settings.MissingAndCorruptJsonUseDefaults", SettingsTests.MissingAndCorruptJsonUseDefaults),
        ("Settings.ConcurrentSavesRemainAtomic", SettingsTests.ConcurrentSavesRemainAtomic)
    ];

    private static async Task<int> Main()
    {
        var failures = 0;
        Console.WriteLine($"BeatzOnKeys.Tests: running {Tests.Length} tests");

        foreach (var test in Tests)
        {
            try
            {
                await test.Run().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"RESULT: {Tests.Length - failures} passed, {failures} failed, {Tests.Length} total");
        return failures == 0 ? 0 : 1;
    }
}
