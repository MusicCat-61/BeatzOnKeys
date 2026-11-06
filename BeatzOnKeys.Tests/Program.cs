namespace BeatzOnKeys.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("PlaybackController.EmptyPlaylistAndDefaults", PlaybackControllerTests.EmptyPlaylistAndDefaults),
        ("PlaybackController.NaturalEndModeMatrix", PlaybackControllerTests.NaturalEndModeMatrix),
        ("PlaybackController.SelectionUpsertRemoveAndClear", PlaybackControllerTests.SelectionUpsertRemoveAndClear),
        ("PlaybackController.EventsRunOutsidePlaylistLock", PlaybackControllerTests.EventsRunOutsidePlaylistLock),
        ("PlaylistOrder.MoveTrackStepsAndRespectsEdges", PlaylistOrderTests.MoveTrackStepsAndRespectsEdges),
        ("PlaylistOrder.ReorderFollowsVisualOrderAndKeepsSelection", PlaylistOrderTests.ReorderFollowsVisualOrderAndKeepsSelection),
        ("PlaylistOrder.NaturalEndFollowsReorderedList", PlaylistOrderTests.NaturalEndFollowsReorderedList),
        ("PlaylistOrder.RemoveCurrentTrackLeavesNoSelection", PlaylistOrderTests.RemoveCurrentTrackLeavesNoSelection),
        ("Volume.AttackHoldAndRelease", VolumeEnvelopeTests.AttackHoldAndRelease),
        ("Volume.RetriggerAndRapidKeysStaySmooth", VolumeEnvelopeTests.RetriggerAndRapidKeysStaySmooth),
        ("Volume.InstantResumeAndMasterVolumeCeiling", VolumeEnvelopeTests.InstantResumeAndMasterVolumeCeiling),
        ("Volume.ConsecutiveBuffersAreSampleSmoothAndStereoLinked", VolumeEnvelopeTests.ConsecutiveBuffersAreSampleSmoothAndStereoLinked),
        ("Volume.RenderingAheadDoesNotPrematurelyReleaseLogicalGain", VolumeEnvelopeTests.RenderingAheadDoesNotPrematurelyReleaseLogicalGain),
        ("Audio.SequentialProviderAdvancesDuringQuietAndEndsNormally", ContinuousAudioTests.SequentialProviderAdvancesDuringQuietAndEndsNormally),
        ("Audio.IdlePauseResumeKeepsDecoderAndPosition", ContinuousAudioTests.IdlePauseResumeKeepsDecoderAndPosition),
        ("Audio.DisabledPauseAndLiveSettings", ContinuousAudioTests.DisabledPauseAndLiveSettings),
        ("Audio.StaleIdleCallbacksCannotPauseNewActivityOrRun", ContinuousAudioTests.StaleIdleCallbacksCannotPauseNewActivityOrRun),
        ("Audio.StopStartResetsToZeroAndUnloadReleasesFile", ContinuousAudioTests.StopStartResetsToZeroAndUnloadReleasesFile),
        ("Audio.NaturalEndFailureAndExplicitStopAreDistinct", ContinuousAudioTests.NaturalEndFailureAndExplicitStopAreDistinct),
        ("Audio.StopWaitsForOutputAndInitializationAvoidsUiContext", ContinuousAudioTests.StopWaitsForOutputAndInitializationAvoidsUiContext),
        ("Audio.InvalidReplacementPreservesWorkingTrackAndLoaderValidatesFormat", ContinuousAudioTests.InvalidReplacementPreservesWorkingTrackAndLoaderValidatesFormat),
        ("MP3.SequentialPacketsMatchOriginalRecording", Mp3ContinuousTests.SequentialPacketsMatchOriginalRecording),
        ("MP3.RapidVolumePulsesNeverInterruptMp3Packets", Mp3ContinuousTests.RapidVolumePulsesNeverInterruptMp3Packets),
        ("MP3.Mp3StartAndIdleResumeKeepOneStream", Mp3ContinuousTests.Mp3StartAndIdleResumeKeepOneStream),
        ("KeyboardHook.NativeLifecycleDoesNotNeedCallerPump", KeyboardHookTests.NativeLifecycleDoesNotNeedCallerPump),
        ("KeyboardHook.StartupWaitsForQueueAndNativeInstallation", KeyboardHookTests.StartupWaitsForQueueAndNativeInstallation),
        ("KeyboardHook.InstallationFailurePropagatesAndCanRetry", KeyboardHookTests.InstallationFailurePropagatesAndCanRetry),
        ("KeyboardHook.ConcurrentLifecycleKeepsOneNativeOwner", KeyboardHookTests.ConcurrentLifecycleKeepsOneNativeOwner),
        ("KeyboardHook.StopBeforeQueueReadinessCancelsStartup", KeyboardHookTests.StopBeforeQueueReadinessCancelsStartup),
        ("KeyboardHook.DisposeWinsRacingInstallation", KeyboardHookTests.DisposeWinsRacingInstallation),
        ("KeyboardHook.StartupTimeoutCleansUpLateHandle", KeyboardHookTests.StartupTimeoutCleansUpLateHandle),
        ("KeyboardHook.CallbackCanStopOrDisposeWithoutSelfWait", KeyboardHookTests.CallbackCanStopOrDisposeWithoutSelfWait),
        ("KeyboardHook.ExternalStopAllowsReentrantCallbackShutdown", KeyboardHookTests.ExternalStopAllowsReentrantCallbackShutdown),
        ("KeyboardHook.ShutdownTimeoutDoesNotLoseCleanupOwnership", KeyboardHookTests.ShutdownTimeoutDoesNotLoseCleanupOwnership),
        ("KeyboardHook.PrivacyFilterForwardsButNeverPublishesInjectedOrModifierKeys", KeyboardHookTests.PrivacyFilterForwardsButNeverPublishesInjectedOrModifierKeys),
        ("KeyboardHook.MessageLoopFailureUnhooksAndAllowsRestart", KeyboardHookTests.MessageLoopFailureUnhooksAndAllowsRestart),
        ("WatcherHook.KeyPressBytesSurfaceAsKeyDown", WatcherHookTests.KeyPressBytesSurfaceAsKeyDown),
        ("WatcherHook.InstallFailsWhenNoWatcherConnects", WatcherHookTests.InstallFailsWhenNoWatcherConnects),
        ("TypingPlaybackService.StartRequiresTrackAndLifecycleHasOneWorker", TypingPlaybackServiceTests.StartRequiresTrackAndLifecycleHasOneWorker),
        ("TypingPlaybackService.StopAwaitsInFlightAndCancelsQueuedKeys", TypingPlaybackServiceTests.StopAwaitsInFlightAndCancelsQueuedKeys),
        ("TypingPlaybackService.FailedInstallAndStartRecoverWithoutOldWorker", TypingPlaybackServiceTests.FailedInstallAndStartRecoverWithoutOldWorker),
        ("TypingPlaybackService.SelectionDurationAndPlaybackFailure", TypingPlaybackServiceTests.SelectionDurationAndPlaybackFailure),
        ("TypingPlaybackService.NaturalEndAdvancesWithoutTypingAndLoopWraps", TypingPlaybackServiceTests.NaturalEndAdvancesWithoutTypingAndLoopWraps),
        ("TypingPlaybackService.LateTrackEndAfterUnloadDoesNotAdvance", TypingPlaybackServiceTests.LateTrackEndAfterUnloadDoesNotAdvance),
        ("TypingPlaybackService.LateFailureCannotStopReplacementOrNewSession", TypingPlaybackServiceTests.LateFailureCannotStopReplacementOrNewSession),
        ("UpdateService.ReleaseMetadataAndCurrentRelease", UpdateServiceTests.ReleaseMetadataAndCurrentRelease),
        ("UpdateService.NewReleaseAndMissingAssets", UpdateServiceTests.NewReleaseAndMissingAssets),
        ("UpdateService.DraftPrereleaseAndInvalidVersion", UpdateServiceTests.DraftPrereleaseAndInvalidVersion),
        ("UpdateService.HttpStatusesAndMalformedJson", UpdateServiceTests.HttpStatusesAndMalformedJson),
        ("UpdateService.RepositoryAssetUrlsRejectSpoofing", UpdateServiceTests.RepositoryAssetUrlsRejectSpoofing),
        ("UpdateService.TagLocationValidation", UpdateServiceTests.TagLocationValidation),
        ("UpdateService.PayloadRequiresOnlyCurrentRuntimeFiles", UpdateServiceTests.PayloadRequiresOnlyCurrentRuntimeFiles),
        ("Settings.DefaultsAndNormalization", SettingsTests.DefaultsAndNormalization),
        ("Settings.RoundTripAllFieldsAndDelete", SettingsTests.RoundTripAllFieldsAndDelete),
        ("Settings.LegacyVolumeMigratesAndNewFieldsTakePrecedence", SettingsTests.LegacyVolumeMigratesAndNewFieldsTakePrecedence),
        ("Settings.MissingAndCorruptJsonUseDefaults", SettingsTests.MissingAndCorruptJsonUseDefaults),
        ("Settings.ConcurrentSavesRemainAtomic", SettingsTests.ConcurrentSavesRemainAtomic)
    ];

    private static async Task<int> Main()
    {
        var failures = 0;
        Console.WriteLine($"BeatzOnKeys.Tests: running {Tests.Length} tests");
        foreach (var test in Tests)
        {
            try { await test.Run().ConfigureAwait(false); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}"); Console.Error.WriteLine(exception); }
        }
        Console.WriteLine($"RESULT: {Tests.Length - failures} passed, {failures} failed, {Tests.Length} total");
        return failures == 0 ? 0 : 1;
    }
}
