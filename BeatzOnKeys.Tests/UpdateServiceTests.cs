using System.Net;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.Updates;

namespace BeatzOnKeys.Tests;

internal static class UpdateServiceTests
{
    private static readonly string ValidArchiveUrl =
        "https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip";
    private static readonly string ValidChecksumUrl =
        "https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip.sha256";

    public static Task ReleaseMetadataAndCurrentRelease()
    {
        var assemblyVersion = AssertEx.NotNull(typeof(UpdateService).Assembly.GetName().Version);
        AssertEx.Equal(1, assemblyVersion.Major);
        AssertEx.Equal(2, assemblyVersion.Minor);
        AssertEx.Equal(6, assemblyVersion.Build);
        AssertEx.Equal("1.2.6", UpdateService.CurrentVersion.Display);

        var current = Version("1.1.1");
        var result = UpdateService.ParseRelease(ReleaseJson("v1.1.1"), current);
        AssertEx.Equal(UpdateStatus.UpToDate, result.Status);
        AssertEx.False(result.HasUpdate);
        AssertEx.Equal(current, result.Current);
        AssertEx.Equal(current, result.Latest);
        AssertEx.Equal("v1.1.1", result.Tag);
        AssertEx.Equal(
            "https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1",
            result.ReleaseUrl);
        return Task.CompletedTask;
    }

    public static Task NewReleaseAndMissingAssets()
    {
        var current = Version("1.0.1");
        var available = UpdateService.ParseRelease(
            ReleaseJson(
                "v1.1.1",
                assets:
                [
                    new FixtureAsset(UpdateService.AssetFileName, ValidArchiveUrl, 123_456),
                    new FixtureAsset(UpdateService.ChecksumFileName, ValidChecksumUrl, 80)
                ]),
            current);
        AssertEx.Equal(UpdateStatus.UpdateAvailable, available.Status);
        AssertEx.True(available.HasUpdate);
        AssertEx.Equal("1.1.1", AssertEx.NotNull(available.Latest).Display);
        AssertEx.Equal(ValidArchiveUrl, available.DownloadUrl);
        AssertEx.Equal(ValidChecksumUrl, available.ChecksumUrl);
        AssertEx.Equal(123_456L, available.AssetSize);
        AssertEx.Null(available.Error);

        var noAssets = UpdateService.ParseRelease(ReleaseJson("v1.1.1"), current);
        AssertEx.Equal(UpdateStatus.NoDownload, noAssets.Status);
        AssertEx.False(noAssets.HasUpdate);
        AssertEx.Null(noAssets.DownloadUrl);
        AssertEx.Null(noAssets.ChecksumUrl);
        AssertEx.True(!string.IsNullOrWhiteSpace(noAssets.Error));

        var withoutChecksum = UpdateService.ParseRelease(
            ReleaseJson(
                "v1.1.1",
                assets: [new FixtureAsset(UpdateService.AssetFileName, ValidArchiveUrl, 44)]),
            current);
        AssertEx.Equal(UpdateStatus.NoDownload, withoutChecksum.Status);
        AssertEx.Equal(ValidArchiveUrl, withoutChecksum.DownloadUrl);
        AssertEx.Null(withoutChecksum.ChecksumUrl);
        AssertEx.False(withoutChecksum.HasUpdate);
        return Task.CompletedTask;
    }

    public static Task DraftPrereleaseAndInvalidVersion()
    {
        var current = Version("1.0.1");
        var draft = UpdateService.ParseRelease(ReleaseJson("v1.1.1", draft: true), current);
        AssertEx.Equal(UpdateStatus.NoReleases, draft.Status);
        AssertEx.Equal(current, draft.Current);

        var prerelease = UpdateService.ParseRelease(ReleaseJson("v1.1.1", prerelease: true), current);
        AssertEx.Equal(UpdateStatus.NoReleases, prerelease.Status);
        AssertEx.Equal(current, prerelease.Current);

        var invalid = UpdateService.ParseRelease(ReleaseJson("latest"), current);
        AssertEx.Equal(UpdateStatus.Failed, invalid.Status);
        AssertEx.True(!string.IsNullOrWhiteSpace(invalid.Error));
        return Task.CompletedTask;
    }

    public static async Task HttpStatusesAndMalformedJson()
    {
        Uri? requested = null;
        var acceptedGitHubJson = false;
        using (var client = Client(request =>
               {
                   requested = request.RequestUri;
                   acceptedGitHubJson = request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json");
                   return new HttpResponseMessage(HttpStatusCode.NotFound);
               }))
        {
            var notFound = await UpdateService.CheckUsingClientAsync(client).ConfigureAwait(false);
            AssertEx.Equal(UpdateStatus.NoReleases, notFound.Status);
        }
        AssertEx.Equal(
            new Uri("https://api.github.com/repos/MusicCat-61/BeatzOnKeys/releases/latest"),
            requested);
        AssertEx.True(acceptedGitHubJson);

        using (var client = Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            var nonSuccess = await UpdateService.CheckUsingClientAsync(client).ConfigureAwait(false);
            AssertEx.Equal(UpdateStatus.Failed, nonSuccess.Status);
            AssertEx.True(nonSuccess.Error?.Contains("503", StringComparison.Ordinal) == true);
        }

        using (var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
               {
                   Content = new StringContent("not-json", Encoding.UTF8, "application/json")
               }))
        {
            var malformed = await UpdateService.CheckUsingClientAsync(client).ConfigureAwait(false);
            AssertEx.Equal(UpdateStatus.Failed, malformed.Status);
            AssertEx.True(!string.IsNullOrWhiteSpace(malformed.Error));
        }
    }

    public static Task RepositoryAssetUrlsRejectSpoofing()
    {
        var current = Version("1.0.1");
        var spoofedUrls = new[]
        {
            "http://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip",
            "https://github.com.evil.example/MusicCat-61/BeatzOnKeys/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip",
            "https://github.com@evil.example/MusicCat-61/BeatzOnKeys/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip",
            "https://github.com/MusicCat-61/Other/releases/download/v1.1.1/BeatzOnKeys-win-x64.zip"
        };

        foreach (var spoofedUrl in spoofedUrls)
        {
            var spoofed = UpdateService.ParseRelease(
                ReleaseJson(
                    "v1.1.1",
                    assets:
                    [
                        new FixtureAsset(UpdateService.AssetFileName, spoofedUrl, 10),
                        new FixtureAsset(UpdateService.ChecksumFileName, spoofedUrl + ".sha256", 10)
                    ]),
                current);
            AssertEx.Equal(UpdateStatus.NoDownload, spoofed.Status, $"Accepted spoofed asset URL: {spoofedUrl}");
            AssertEx.Null(spoofed.DownloadUrl);
            AssertEx.Null(spoofed.ChecksumUrl);
        }

        var spoofedChecksum = UpdateService.ParseRelease(
            ReleaseJson(
                "v1.1.1",
                assets:
                [
                    new FixtureAsset(UpdateService.AssetFileName, ValidArchiveUrl, 10),
                    new FixtureAsset(
                        UpdateService.ChecksumFileName,
                        "https://evil.example/BeatzOnKeys-win-x64.zip.sha256",
                        10)
                ]),
            current);
        AssertEx.Equal(UpdateStatus.NoDownload, spoofedChecksum.Status);
        AssertEx.Equal(ValidArchiveUrl, spoofedChecksum.DownloadUrl);
        AssertEx.Null(spoofedChecksum.ChecksumUrl);
        return Task.CompletedTask;
    }

    public static Task TagLocationValidation()
    {
        AssertEx.Equal(
            "v1.1.1",
            UpdateService.TagFromLocation(
                new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1")));
        AssertEx.Equal(
            "v1.1.1",
            UpdateService.TagFromLocation(
                new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1/")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/Other/releases/tag/v1.1.1")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://example.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1", UriKind.Relative)));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/latest")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.1.1/extra")));
        return Task.CompletedTask;
    }

    public static Task DownloadedExecutableIsValidatedBeforeTheSwap()
    {
        using var directory = new TemporaryDirectory();
        var current = Version("1.0.0");
        var target = System.IO.Path.Combine(directory.Path, UpdateService.StagedFileName);
        var host = Environment.ProcessPath ?? throw new TestFailureException("No process path.");

        // A real build passes and reports its size.
        WritePaddedCopy(host, target, 2 * 1024 * 1024);
        var size = new FileInfo(target).Length;
        AssertEx.Equal(size, UpdateService.ValidateDownloadedExecutable(target, size, current),
            "A genuine executable of the advertised size must be accepted.");

        // An error page saved under the asset name must not reach the swap. It is padded past the
        // size floor so the signature check, not the size check, is what rejects it.
        var errorPage = "<!DOCTYPE html><html><body>404 Not Found</body></html>";
        File.WriteAllText(target, errorPage + new string(' ', 2 * 1024 * 1024));
        var notExecutable = AssertEx.Throws<InvalidDataException>(
            () => UpdateService.ValidateDownloadedExecutable(target, 0, current));
        AssertEx.True(notExecutable.Message.Contains("not a Windows executable", StringComparison.Ordinal),
            notExecutable.Message);

        // A truncated download is caught by the advertised size.
        WritePaddedCopy(host, target, 2 * 1024 * 1024);
        var truncated = AssertEx.Throws<InvalidDataException>(
            () => UpdateService.ValidateDownloadedExecutable(target, size + 1, current));
        AssertEx.True(truncated.Message.Contains("but the release says", StringComparison.Ordinal), truncated.Message);

        // A build older than the running one would be a silent downgrade.
        var newer = Version("9.9.9");
        var downgrade = AssertEx.Throws<InvalidDataException>(
            () => UpdateService.ValidateDownloadedExecutable(target, size, newer));
        AssertEx.True(downgrade.Message.Contains("older than", StringComparison.Ordinal), downgrade.Message);
        return Task.CompletedTask;
    }

    private static void WritePaddedCopy(string source, string destination, int size)
    {
        var bytes = File.ReadAllBytes(source);
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.SetLength(Math.Max(size, bytes.Length));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(new StubHttpMessageHandler(responseFactory), disposeHandler: true);

    private static ReleaseVersion Version(string value)
    {
        AssertEx.True(ReleaseVersion.TryParse(value, out var version), $"Could not parse test version {value}.");
        return version;
    }

    private static string ReleaseJson(
        string tag,
        bool draft = false,
        bool prerelease = false,
        IReadOnlyList<FixtureAsset>? assets = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = (assets ?? []).Select(asset => new
            {
                name = asset.Name,
                browser_download_url = asset.Url,
                size = asset.Size
            })
        });

    private sealed record FixtureAsset(string Name, string Url, long Size);

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
