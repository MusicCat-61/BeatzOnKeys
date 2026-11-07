using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeatzOnKeys.Core.Services;

namespace BeatzOnKeys.Core.Updates;

public enum UpdateStatus
{
    Unknown,
    UpToDate,
    UpdateAvailable,
    NoDownload,
    Failed,
    NoReleases
}

public sealed record UpdateCheckResult
{
    public UpdateStatus Status { get; init; }

    public ReleaseVersion Current { get; init; } = UpdateService.CurrentVersion;

    public ReleaseVersion? Latest { get; init; }

    public string? Tag { get; init; }

    public string? DownloadUrl { get; init; }

    public string? ChecksumUrl { get; init; }

    public string? ReleaseUrl { get; init; }

    public long AssetSize { get; init; }

    public string? Error { get; init; }

    /// <summary>The release notes, shown in the update dialog instead of a tray balloon.</summary>
    public string? Notes { get; init; }

    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

/// <summary>
/// A downloaded build parked beside the running executable. Only one file is involved: the
/// portable app is a single self-contained executable, so the update is that same file.
/// </summary>
public sealed record PreparedUpdate(string Version, string StagedFile, long Size);

public sealed record UpdateApplyReport(bool Succeeded, string Detail);

/// <summary>
/// Checks, downloads, and applies portable GitHub release updates by replacing the application
/// executable. Windows locks a running executable against being overwritten, so the swap is handed
/// to a detached script that waits for this process to exit.
/// </summary>
public static class UpdateService
{
    public const string Repository = "MusicCat-61/BeatzOnKeys";

    /// <summary>The portable executable; the updater replaces exactly this one file.</summary>
    public const string AssetFileName = "BeatzOnKeys.exe";

    public const string ChecksumFileName = "BeatzOnKeys.exe.sha256";

    /// <summary>Name the download is parked under, in the application folder.</summary>
    public const string StagedFileName = "BeatzOnKeys.update.exe";

    /// <summary>A real build is tens of megabytes; anything smaller is an error page or a stub.</summary>
    private const long MinimumAssetBytes = 1024 * 1024;

    private const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);
    private static readonly object ClientGate = new();
    private static HttpClient? _releaseClient;
    private static HttpClient? _downloadClient;
    private static ReleaseVersion? _currentVersion;

    public static ReleaseVersion CurrentVersion => _currentVersion ??= ReadCurrentVersion();

    public static UpdateCheckResult? LastResult { get; private set; }

    public static DateTimeOffset? LastCheckUtc { get; private set; }

    public static event EventHandler<UpdateCheckResult>? Checked;

    public static string RepositoryUrl => $"https://github.com/{Repository}";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BeatzOnKeys.exe");

    /// <summary>Folder the application was started from — where the update is written.</summary>
    public static string ExecutableDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    /// <summary>Full path the download is parked under until the app restarts.</summary>
    public static string StagedFilePath => Path.Combine(ExecutableDirectory, StagedFileName);

    /// <summary>Where the apply script records what it did; read on the next start.</summary>
    public static string ApplyLogPath => Path.Combine(AppPaths.LogDirectory, "update-apply.log");

    /// <summary>Script that performs the swap after this process is gone.</summary>
    private static string ApplyScriptPath => Path.Combine(AppPaths.RootDirectory, "apply-update.cmd");

    private static HttpClient ReleaseClient
    {
        get
        {
            lock (ClientGate)
            {
                return _releaseClient ??= CreateClient(allowRedirects: false);
            }
        }
    }

    private static HttpClient DownloadClient
    {
        get
        {
            lock (ClientGate)
            {
                return _downloadClient ??= CreateClient(allowRedirects: true);
            }
        }
    }

    public static bool IsCheckDue(TimeSpan normalInterval, TimeSpan retryAfterFailure)
    {
        if (LastCheckUtc is not { } checkedAt)
        {
            return true;
        }

        var interval = LastResult?.Status == UpdateStatus.Failed ? retryAfterFailure : normalInterval;
        return DateTimeOffset.UtcNow - checkedAt >= interval;
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        await CheckGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await CheckCoreAsync(cancellationToken).ConfigureAwait(false);
            LastResult = result;
            LastCheckUtc = DateTimeOffset.UtcNow;
            AppLogger.Info("update", Describe(result));
            Checked?.Invoke(null, result);
            return result;
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken) =>
        CheckUsingClientAsync(ReleaseClient, cancellationToken);

    internal static async Task<UpdateCheckResult> CheckUsingClientAsync(
        HttpClient client, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = CurrentVersion };
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"GitHub answered HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRelease(json, CurrentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Error("update", "Update check failed.", exception);
            return Failed(exception.Message);
        }
    }

    public static UpdateCheckResult ParseRelease(string json, ReleaseVersion current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = current };
        }

        if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
        {
            return new UpdateCheckResult { Status = UpdateStatus.NoReleases, Current = current };
        }

        var tag = release.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString() : null;
        if (!ReleaseVersion.TryParse(tag, out var latest))
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Failed, Current = current, Error = "GitHub returned an invalid release version."
            };
        }

        var releaseUrl = $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag!)}";
        var notes = release.TryGetProperty("body", out var bodyValue) ? bodyValue.GetString() : null;
        if (latest <= current)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate, Current = current, Latest = latest, Tag = tag,
                ReleaseUrl = releaseUrl, Notes = notes
            };
        }

        string? downloadUrl = null;
        string? checksumUrl = null;
        long assetSize = 0;
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameValue) ||
                    !asset.TryGetProperty("browser_download_url", out var urlValue)) { continue; }
                var name = nameValue.GetString();
                var url = urlValue.GetString();
                if (!IsRepositoryAssetUrl(url)) { continue; }
                if (name == AssetFileName)
                {
                    downloadUrl = url;
                    if (asset.TryGetProperty("size", out var sizeValue)) { sizeValue.TryGetInt64(out assetSize); }
                }
                else if (name == ChecksumFileName) { checksumUrl = url; }
            }
        }

        var downloadable = downloadUrl is not null && checksumUrl is not null;
        return new UpdateCheckResult
        {
            Status = downloadable ? UpdateStatus.UpdateAvailable : UpdateStatus.NoDownload,
            Current = current, Latest = latest, Tag = tag, ReleaseUrl = releaseUrl, Notes = notes,
            DownloadUrl = downloadUrl, ChecksumUrl = checksumUrl, AssetSize = assetSize,
            Error = downloadable ? null : $"Release {tag} must contain {AssetFileName} and {ChecksumFileName}."
        };
    }

    private static bool IsRepositoryAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme == "https" &&
        address.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        address.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    public static string? TagFromLocation(Uri location)
    {
        if (!location.IsAbsoluteUri || location.Scheme != "https" ||
            !location.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) { return null; }
        var prefix = $"/{Repository}/releases/tag/";
        if (!location.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return null; }
        var tag = Uri.UnescapeDataString(location.AbsolutePath[prefix.Length..].TrimEnd('/'));
        return !tag.Contains('/') && ReleaseVersion.TryParse(tag, out _) ? tag : null;
    }

    /// <summary>
    /// Downloads the new executable beside the running one and validates it: checksum, size, a real
    /// PE header, and a version newer than this build. Anything that fails is deleted, so a broken
    /// download can never be applied later.
    /// </summary>
    public static async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateCheckResult result,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasUpdate ||
            string.IsNullOrWhiteSpace(result.DownloadUrl) ||
            string.IsNullOrWhiteSpace(result.ChecksumUrl) ||
            result.Latest is null)
        {
            throw new InvalidOperationException("No downloadable update is available.");
        }

        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The update replaces the executable, so the application folder itself must accept a
            // write. Checking first turns a failed swap into an immediate, explainable message.
            if (!CanWriteToInstallFolder())
            {
                throw new UpdateStorageException(
                    $"The application folder is not writable: {ExecutableDirectory}. "
                    + "Move BeatzOnKeys to a folder you own, or download the release manually.",
                    [ExecutableDirectory]);
            }

            var expectedHash = await DownloadChecksumAsync(result.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);

            var staged = StagedFilePath;
            // The partial carries the version, so a leftover from an older attempt can never be
            // appended to a different build.
            var partial = staged + "." + result.Latest.Display + ".part";
            // Leftovers from other versions can never be resumed into this one, so they are swept
            // here instead of growing into a pile of half-downloaded builds.
            DeleteStalePartials(staged, partial);
            try
            {
                await DownloadFileAsync(result.DownloadUrl, partial, result.AssetSize, progress, cancellationToken)
                    .ConfigureAwait(false);

                var actualHash = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Wrong bytes are the one thing worth discarding: keeping them would only
                    // fail the same check on the next attempt.
                    TryDelete(partial);
                    throw new InvalidDataException(
                        $"Update checksum mismatch. Expected {expectedHash}, got {actualHash}.");
                }

                progress?.Report(0.95);
                var size = ValidateDownloadedExecutable(partial, result.AssetSize, CurrentVersion);
                File.Move(partial, staged, overwrite: true);
                progress?.Report(1);

                var prepared = new PreparedUpdate(result.Latest.Display, staged, size);
                AppLogger.Info("update",
                    $"Update {prepared.Version} verified ({size} bytes) and staged at '{staged}'.");
                return prepared;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancelled transfer keeps its bytes, so the next attempt continues from where
                // this one stopped instead of pulling the whole asset again.
                AppLogger.Info("update",
                    $"The update download was interrupted; keeping '{partial}' to resume later.");
                throw;
            }
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    /// <summary>
    /// Rejects a truncated file, a proxy error page saved as ".exe", and a build that is not newer
    /// than the running one — each of those would either fail the swap or silently downgrade.
    /// </summary>
    internal static long ValidateDownloadedExecutable(string path, long expectedSize, ReleaseVersion current)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MinimumAssetBytes)
        {
            throw new InvalidDataException($"The downloaded update is too small to be a build ({info.Length} bytes).");
        }

        if (expectedSize > 0 && info.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"The downloaded update is {info.Length} bytes, but the release says {expectedSize}.");
        }

        // A portable executable starts with "MZ". An HTML or JSON error page does not.
        Span<byte> header = stackalloc byte[2];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header) != 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                throw new InvalidDataException("The downloaded file is not a Windows executable.");
            }
        }

        var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (ReleaseVersion.TryParse(text, out var version) && version < current)
        {
            throw new InvalidDataException(
                $"The downloaded build is {version.Display}, which is older than {current.Display}.");
        }

        return info.Length;
    }

    /// <summary>
    /// The staged build, when one is on disk and newer than the running one. A leftover of an
    /// already installed release is deleted: it would be offered for ever otherwise, and applying
    /// it would quietly downgrade the app.
    /// </summary>
    public static PreparedUpdate? FindPreparedUpdate()
    {
        try
        {
            var staged = StagedFilePath;
            if (!File.Exists(staged))
            {
                return null;
            }

            var size = new FileInfo(staged).Length;
            if (size < MinimumAssetBytes || IsNotNewer(staged))
            {
                AppLogger.Info("update", $"Discarding a stale staged build at '{staged}'.");
                TryDelete(staged);
                return null;
            }

            var text = FileVersionInfo.GetVersionInfo(staged).FileVersion;
            var version = ReleaseVersion.TryParse(text, out var parsed) ? parsed.Display : "unknown";
            return new PreparedUpdate(version, staged, size);
        }
        catch (Exception exception)
        {
            AppLogger.Error("update", "Could not inspect the staged update.", exception);
            return null;
        }
    }

    /// <summary>True when the staged build is not newer than the running one.</summary>
    private static bool IsNotNewer(string staged)
    {
        // A file without a readable version is kept: refusing a real update would be worse
        // than offering it twice.
        var text = FileVersionInfo.GetVersionInfo(staged).FileVersion;
        return ReleaseVersion.TryParse(text, out var stagedVersion) && stagedVersion <= CurrentVersion;
    }

    public static bool CanWriteToInstallFolder()
    {
        try
        {
            var probe = Path.Combine(ExecutableDirectory, $".beatzonkeys-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hands the staged file to a detached script that replaces the executable and starts it again.
    /// Windows keeps a running executable locked, so the swap has to happen after this process is
    /// gone.
    /// </summary>
    public static bool TryLaunchApplyScript(PreparedUpdate prepared, out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(prepared.StagedFile))
            {
                error = "The downloaded file is gone.";
                return false;
            }

            if (new FileInfo(prepared.StagedFile).Length != prepared.Size)
            {
                error = "The downloaded file changed after verification.";
                return false;
            }

            if (!CanWriteToInstallFolder())
            {
                error = $"The application folder is not writable: {ExecutableDirectory}";
                return false;
            }

            Directory.CreateDirectory(AppPaths.RootDirectory);
            Directory.CreateDirectory(AppPaths.LogDirectory);

            // The script text stays pure ASCII and takes every path from the environment. cmd.exe
            // reads a .cmd file in the OEM code page, so a path like "C:\Users\Маksim\..." would be
            // mangled if it were written into the file itself.
            File.WriteAllLines(ApplyScriptPath, ApplyScriptLines, Encoding.ASCII);

            // A report left behind by an earlier attempt would be mistaken for this one's.
            TryDelete(ApplyLogPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{ApplyScriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.Environment["BOK_STAGED"] = prepared.StagedFile;
            startInfo.Environment["BOK_TARGET"] = ExecutablePath;
            startInfo.Environment["BOK_LOG"] = ApplyLogPath;
            startInfo.Environment["BOK_PID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["BOK_EXPECT"] = prepared.Size.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Process.Start(startInfo);
            AppLogger.Info("update", $"Detached updater launched for {prepared.Version}.");
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            AppLogger.Error("update", "Could not launch the detached updater.", exception);
            return false;
        }
    }

    /// <summary>
    /// Outcome of the last apply attempt, or null when the script never got as far as writing one.
    /// Reading it also cleans up the script and its log, so a failed update is reported once and
    /// then forgotten.
    /// </summary>
    public static UpdateApplyReport? ConsumeLastApplyReport()
    {
        try
        {
            var report = ReadApplyReport();
            if (report is not null)
            {
                AppLogger.Info("update", $"Previous apply result: {report.Detail}");
            }

            return report;
        }
        catch (Exception exception)
        {
            AppLogger.Error("update", "Could not read the previous update report.", exception);
            return null;
        }
        finally
        {
            TryDelete(ApplyLogPath);
            TryDelete(ApplyScriptPath);
        }
    }

    private static UpdateApplyReport? ReadApplyReport()
    {
        if (!File.Exists(ApplyLogPath))
        {
            return null;
        }

        var resultLine = File.ReadLines(ApplyLogPath)
            .LastOrDefault(line => line.Contains("RESULT=", StringComparison.Ordinal));
        if (resultLine is null)
        {
            return null;
        }

        var result = resultLine[(resultLine.IndexOf("RESULT=", StringComparison.Ordinal) + 7)..].Trim();
        return result.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
            ? new UpdateApplyReport(true, result)
            : new UpdateApplyReport(false, result);
    }

    private static async Task<string> DownloadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await DownloadClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var hash = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The release checksum file is invalid.");
        }

        return hash;
    }

    /// <summary>
    /// Downloads the asset, continuing a file left by an interrupted attempt. The partial file is
    /// kept on cancellation and on any transport failure, because the alternative is asking the
    /// user to pull another 72 MB over a connection that just proved it cannot finish the job.
    /// </summary>
    /// <summary>Test only: a handler-backed client, so a resumed transfer can be exercised offline.</summary>
    internal static HttpClient? DownloadClientOverride
    {
        get => _downloadClient;
        set => _downloadClient = value;
    }

    internal static async Task DownloadFileAsync(
        string url,
        string targetPath,
        long expectedLength,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var alreadyHave = ExistingBytes(targetPath);
        // A complete file needs no network at all; it only has to survive the checks below.
        if (expectedLength > 0 && alreadyHave == expectedLength)
        {
            progress?.Report(0.9);
            return;
        }

        // A partial longer than the asset is junk from another build, not something to extend.
        if (expectedLength > 0 && alreadyHave > expectedLength)
        {
            TryDelete(targetPath);
            alreadyHave = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (alreadyHave > 0)
        {
            request.Headers.Range = new RangeHeaderValue(alreadyHave, null);
        }

        using var response = await DownloadClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // A server that ignores Range answers 200 with the whole body, so the file restarts.
        var resumed = alreadyHave > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) { alreadyHave = 0; }
        var totalLength = resumed
            ? alreadyHave + (response.Content.Headers.ContentLength ?? 0)
            : response.Content.Headers.ContentLength ?? expectedLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            resumed ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long totalRead = alreadyHave;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            if (totalLength > 0)
            {
                progress?.Report(Math.Clamp(totalRead / (double)totalLength * 0.9, 0, 0.9));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalLength > 0 && totalRead != totalLength)
        {
            throw new IOException($"The update download stopped at {totalRead} of {totalLength} bytes.");
        }
    }

    /// <summary>Removes partial downloads that belong to a different version than the one wanted.</summary>
    private static void DeleteStalePartials(string stagedPath, string keep)
    {
        try
        {
            var folder = Path.GetDirectoryName(stagedPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return; }
            var pattern = Path.GetFileName(stagedPath) + ".*.part";
            foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) { TryDelete(file); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("update", $"Could not clear stale update parts: {exception.Message}");
        }
    }

    /// <summary>Size of a file that may not exist; a missing partial simply means "start over".</summary>
    internal static long ExistingBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0L;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch
        {
            // A leftover file is harmless.
        }
    }

    private static UpdateCheckResult Failed(string reason) => new()
    {
        Status = UpdateStatus.Failed,
        Current = CurrentVersion,
        Error = reason
    };

    private static string Describe(UpdateCheckResult result) => result.Status switch
    {
        UpdateStatus.UpToDate => $"Up to date ({CurrentVersion.Display}).",
        UpdateStatus.UpdateAvailable => $"Update {result.Latest?.Display} is available.",
        UpdateStatus.NoDownload => result.Error ?? "The latest release has no portable executable.",
        UpdateStatus.NoReleases => "No public releases have been published yet.",
        UpdateStatus.Failed => $"Update check failed: {result.Error}",
        _ => "Update state is unknown."
    };

    private static ReleaseVersion ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var value = plus >= 0 ? informational[..plus] : informational;
            if (ReleaseVersion.TryParse(value, out var parsed))
            {
                return parsed;
            }
        }

        if (ReleaseVersion.TryParse(assembly.GetName().Version?.ToString(), out var fallback))
        {
            return fallback;
        }

        return ReleaseVersion.TryParse("0.0.0", out var zero)
            ? zero
            : throw new InvalidOperationException("Could not create the zero version.");
    }

    private static HttpClient CreateClient(bool allowRedirects)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"BeatzOnKeys/{CurrentVersion.Display}");
        return client;
    }

    /// <summary>
    /// The apply script as it is written to disk. Exposed so a test can guard the two properties
    /// that matter: it is pure ASCII, and it carries no literal path.
    /// </summary>
    internal static string ApplyScriptText => string.Join('\n', ApplyScriptLines);

    /// <summary>
    /// The replacement script. It decides nothing on its own: it waits for the app to exit, swaps
    /// the file, checks the result, starts the app again and records every step in the apply log,
    /// errors included. Pure ASCII, because cmd.exe reads a .cmd file in the OEM code page.
    /// </summary>
    private static readonly string[] ApplyScriptLines =
    {
        "@echo off",
        "setlocal",
        "set \"STAGED=%BOK_STAGED%\"",
        "set \"TARGET=%BOK_TARGET%\"",
        "set \"LOG=%BOK_LOG%\"",
        "set \"APPPID=%BOK_PID%\"",
        "set \"EXPECT=%BOK_EXPECT%\"",
        "call :note \"apply start: pid=%APPPID% expected bytes=%EXPECT%\"",
        "call :note \"staged: %STAGED%\"",
        "call :note \"target: %TARGET%\"",
        "",
        "rem Windows keeps a running executable locked, so retry the move itself: that is the",
        "rem condition we actually care about, and it cannot be fooled by a process id that",
        "rem shows up in unrelated tasklist output.",
        "set /a tries=0",
        ":swap",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 30 goto stuck",
        "call :sleep",
        "goto swap",
        "",
        ":stuck",
        "rem Still locked after half a minute, so the process holding the file did not go",
        "rem away by itself. The lock it is still holding proves that the id is ours.",
        "call :note \"still locked after 30s, ending pid %APPPID%\"",
        "taskkill /PID %APPPID% /T /F >nul 2>nul",
        "call :sleep",
        "set /a tries=0",
        ":swapagain",
        "move /Y \"%STAGED%\" \"%TARGET%\" >nul 2>nul",
        "if not errorlevel 1 goto swapped",
        "set /a tries+=1",
        "if %tries% geq 15 goto copyfile",
        "call :sleep",
        "goto swapagain",
        "",
        ":copyfile",
        "rem A rename can also fail for reasons no retry can fix (another volume, a filter",
        "rem driver holding the file). Copying fails differently, so give it one chance.",
        "call :note \"rename failed, trying a copy\"",
        "copy /Y /B \"%STAGED%\" \"%TARGET%\" >>\"%LOG%\" 2>&1",
        "if errorlevel 1 goto failed",
        "del /F /Q \"%STAGED%\" >nul 2>nul",
        "",
        ":swapped",
        "call :verify",
        "if errorlevel 1 goto failed",
        "call :note \"RESULT=OK\"",
        "call :note \"the new build is in place, starting it\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":failed",
        "call :note \"RESULT=FAILED the downloaded file could not replace the running one\"",
        "call :note \"kept for another try: %STAGED%\"",
        "start \"\" \"%TARGET%\"",
        "goto done",
        "",
        ":done",
        "rem Nothing left to do here: the app deletes this script when it starts again.",
        "exit /b 0",
        "",
        ":verify",
        "if not exist \"%TARGET%\" exit /b 1",
        "for %%A in (\"%TARGET%\") do if %%~zA LSS 1024 exit /b 1",
        "if \"%EXPECT%\"==\"\" exit /b 0",
        "for %%A in (\"%TARGET%\") do if not \"%%~zA\"==\"%EXPECT%\" exit /b 1",
        "exit /b 0",
        "",
        ":note",
        ">>\"%LOG%\" echo [%DATE% %TIME%] %~1",
        "exit /b 0",
        "",
        ":sleep",
        "rem A wait that works without a console window.",
        "ping -n 2 127.0.0.1 >nul 2>nul",
        "exit /b 0"
    };
}
