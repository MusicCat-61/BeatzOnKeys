using System.Net;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.Updates;

namespace BeatzOnKeys.Tests;

internal static class UpdateServiceTests
{
    private static readonly string ValidArchiveUrl =
        "https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip";
    private static readonly string ValidChecksumUrl =
        "https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip.sha256";

    public static Task ReleaseMetadataAndCurrentRelease()
    {
        var assemblyVersion = AssertEx.NotNull(typeof(UpdateService).Assembly.GetName().Version);
        AssertEx.Equal(1, assemblyVersion.Major);
        AssertEx.Equal(0, assemblyVersion.Minor);
        AssertEx.Equal(3, assemblyVersion.Build);
        AssertEx.Equal("1.0.3", UpdateService.CurrentVersion.Display);

        var current = Version("1.0.3");
        var result = UpdateService.ParseRelease(ReleaseJson("v1.0.3"), current);
        AssertEx.Equal(UpdateStatus.UpToDate, result.Status);
        AssertEx.False(result.HasUpdate);
        AssertEx.Equal(current, result.Current);
        AssertEx.Equal(current, result.Latest);
        AssertEx.Equal("v1.0.3", result.Tag);
        AssertEx.Equal(
            "https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3",
            result.ReleaseUrl);
        return Task.CompletedTask;
    }

    public static Task NewReleaseAndMissingAssets()
    {
        var current = Version("1.0.1");
        var available = UpdateService.ParseRelease(
            ReleaseJson(
                "v1.0.3",
                assets:
                [
                    new FixtureAsset(UpdateService.AssetFileName, ValidArchiveUrl, 123_456),
                    new FixtureAsset(UpdateService.ChecksumFileName, ValidChecksumUrl, 80)
                ]),
            current);
        AssertEx.Equal(UpdateStatus.UpdateAvailable, available.Status);
        AssertEx.True(available.HasUpdate);
        AssertEx.Equal("1.0.3", AssertEx.NotNull(available.Latest).Display);
        AssertEx.Equal(ValidArchiveUrl, available.DownloadUrl);
        AssertEx.Equal(ValidChecksumUrl, available.ChecksumUrl);
        AssertEx.Equal(123_456L, available.AssetSize);
        AssertEx.Null(available.Error);

        var noAssets = UpdateService.ParseRelease(ReleaseJson("v1.0.3"), current);
        AssertEx.Equal(UpdateStatus.NoDownload, noAssets.Status);
        AssertEx.False(noAssets.HasUpdate);
        AssertEx.Null(noAssets.DownloadUrl);
        AssertEx.Null(noAssets.ChecksumUrl);
        AssertEx.True(!string.IsNullOrWhiteSpace(noAssets.Error));

        var withoutChecksum = UpdateService.ParseRelease(
            ReleaseJson(
                "v1.0.3",
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
        var draft = UpdateService.ParseRelease(ReleaseJson("v1.0.3", draft: true), current);
        AssertEx.Equal(UpdateStatus.NoReleases, draft.Status);
        AssertEx.Equal(current, draft.Current);

        var prerelease = UpdateService.ParseRelease(ReleaseJson("v1.0.3", prerelease: true), current);
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
            "http://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip",
            "https://github.com.evil.example/MusicCat-61/BeatzOnKeys/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip",
            "https://github.com@evil.example/MusicCat-61/BeatzOnKeys/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip",
            "https://github.com/MusicCat-61/Other/releases/download/v1.0.3/BeatzOnKeys-win-x64.zip"
        };

        foreach (var spoofedUrl in spoofedUrls)
        {
            var spoofed = UpdateService.ParseRelease(
                ReleaseJson(
                    "v1.0.3",
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
                "v1.0.3",
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
            "v1.0.3",
            UpdateService.TagFromLocation(
                new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3")));
        AssertEx.Equal(
            "v1.0.3",
            UpdateService.TagFromLocation(
                new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3/")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/Other/releases/tag/v1.0.3")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://example.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3", UriKind.Relative)));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/latest")));
        AssertEx.Null(UpdateService.TagFromLocation(
            new Uri("https://github.com/MusicCat-61/BeatzOnKeys/releases/tag/v1.0.3/extra")));
        return Task.CompletedTask;
    }

    public static Task PayloadRequiresOnlyCurrentRuntimeFiles()
    {
        using var directory = new TemporaryDirectory();
        var required = new[]
        {
            "BeatzOnKeys.exe",
            "BeatzOnKeys.dll",
            "BeatzOnKeys.Core.dll",
            "BeatzOnKeys.deps.json",
            "BeatzOnKeys.runtimeconfig.json",
            "NAudio.dll",
            "NAudio.Core.dll",
            "NAudio.WinMM.dll",
            "NAudio.Wasapi.dll"
        };
        foreach (var fileName in required)
        {
            File.WriteAllText(System.IO.Path.Combine(directory.Path, fileName), "x");
        }

        AssertEx.False(Directory.Exists(System.IO.Path.Combine(directory.Path, "python")));
        UpdateService.ValidatePayload(directory.Path);

        foreach (var fileName in required)
        {
            var path = System.IO.Path.Combine(directory.Path, fileName);
            File.Delete(path);
            var exception = AssertEx.Throws<InvalidDataException>(() => UpdateService.ValidatePayload(directory.Path));
            AssertEx.True(exception.Message.Contains(fileName, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(path, "x");
        }

        var zeroLength = System.IO.Path.Combine(directory.Path, "NAudio.Core.dll");
        File.WriteAllBytes(zeroLength, []);
        AssertEx.Throws<InvalidDataException>(() => UpdateService.ValidatePayload(directory.Path));
        return Task.CompletedTask;
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
