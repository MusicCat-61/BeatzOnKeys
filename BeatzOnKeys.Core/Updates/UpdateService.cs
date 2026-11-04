using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
    Failed
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

    public bool HasUpdate => Status == UpdateStatus.UpdateAvailable;
}

public sealed record PreparedUpdate(
    string Tag,
    string Version,
    string PayloadDirectory,
    string ArchivePath,
    string Sha256);

public sealed record UpdateApplyReport(bool Succeeded, string Detail);

/// <summary>Checks, verifies, stages, and applies portable GitHub release updates.</summary>
public static class UpdateService
{
    public const string Repository = "MusicCat-61/BeatzOnKeys";
    public const string AssetFileName = "BeatzOnKeys-win-x64.zip";
    public const string ChecksumFileName = "BeatzOnKeys-win-x64.zip.sha256";

    private const string LatestReleaseUrl = "https://github.com/" + Repository + "/releases/latest";
    private const string PreparedManifestName = "prepared.json";
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

    public static string ExecutableDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    public static string ApplyLogPath => Path.Combine(AppPaths.LogDirectory, "update-apply.log");

    private static string ApplyScriptPath => Path.Combine(AppPaths.RootDirectory, "apply-update.ps1");

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
            try
            {
                Checked?.Invoke(null, result);
            }
            catch (Exception exception)
            {
                AppLogger.Error("update", "An update-check subscriber failed.", exception);
            }

            return result;
        }
        finally
        {
            CheckGate.Release();
        }
    }

    private static async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            using var response = await ReleaseClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.Headers.Location is { } location)
            {
                if (!location.IsAbsoluteUri)
                {
                    location = new Uri(new Uri("https://github.com"), location);
                }

                return await FromReleaseRedirectAsync(location, cancellationToken).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Failed("The GitHub repository or its first release does not exist yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"GitHub answered HTTP {(int)response.StatusCode}.");
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Current = CurrentVersion,
                Latest = CurrentVersion
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed("The update check was cancelled.");
        }
        catch (Exception exception)
        {
            return Failed(exception.Message);
        }
    }

    private static async Task<UpdateCheckResult> FromReleaseRedirectAsync(
        Uri location,
        CancellationToken cancellationToken)
    {
        var tag = TagFromLocation(location);
        if (tag is null || !ReleaseVersion.TryParse(tag, out var latest))
        {
            return Failed($"Unexpected release address: {location}");
        }

        var releaseUrl = $"https://github.com/{Repository}/releases/tag/{tag}";
        if (latest <= CurrentVersion)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                Current = CurrentVersion,
                Latest = latest,
                Tag = tag,
                ReleaseUrl = releaseUrl
            };
        }

        var assetUrl = $"https://github.com/{Repository}/releases/download/{tag}/{AssetFileName}";
        var asset = await ProbeAssetAsync(assetUrl, cancellationToken).ConfigureAwait(false);
        if (!asset.Exists)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.NoDownload,
                Current = CurrentVersion,
                Latest = latest,
                Tag = tag,
                ReleaseUrl = releaseUrl,
                Error = $"Release {tag} does not contain {AssetFileName}."
            };
        }

        return new UpdateCheckResult
        {
            Status = UpdateStatus.UpdateAvailable,
            Current = CurrentVersion,
            Latest = latest,
            Tag = tag,
            DownloadUrl = assetUrl,
            ChecksumUrl = assetUrl + ".sha256",
            ReleaseUrl = releaseUrl,
            AssetSize = asset.Size
        };
    }

    public static string? TagFromLocation(Uri location)
    {
        var segments = location.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 2 && segments[^2].Equals("tag", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[^1])
            : null;
    }

    public static async Task<PreparedUpdate> DownloadAndPrepareAsync(
        UpdateCheckResult result,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!result.HasUpdate ||
            string.IsNullOrWhiteSpace(result.Tag) ||
            string.IsNullOrWhiteSpace(result.DownloadUrl) ||
            string.IsNullOrWhiteSpace(result.ChecksumUrl) ||
            result.Latest is null)
        {
            throw new InvalidOperationException("No downloadable update is available.");
        }

        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var safeTag = string.Concat(result.Tag.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
            if (string.IsNullOrWhiteSpace(safeTag))
            {
                throw new InvalidDataException("The release tag cannot be used as an update directory.");
            }

            var updateRoot = Path.Combine(AppPaths.UpdateDirectory, safeTag);
            if (Directory.Exists(updateRoot))
            {
                Directory.Delete(updateRoot, recursive: true);
            }

            Directory.CreateDirectory(updateRoot);
            var archivePath = Path.Combine(updateRoot, AssetFileName);
            var payloadDirectory = Path.Combine(updateRoot, "payload");
            Directory.CreateDirectory(payloadDirectory);

            var expectedHash = await DownloadChecksumAsync(result.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);
            await DownloadFileAsync(
                result.DownloadUrl,
                archivePath,
                result.AssetSize,
                progress,
                cancellationToken).ConfigureAwait(false);

            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Update checksum mismatch. Expected {expectedHash}, got {actualHash}.");
            }

            progress?.Report(0.86);
            ExtractArchiveSafely(archivePath, payloadDirectory);
            ValidatePayload(payloadDirectory);

            var prepared = new PreparedUpdate(
                result.Tag,
                result.Latest.Display,
                payloadDirectory,
                archivePath,
                actualHash);
            var manifestPath = Path.Combine(updateRoot, PreparedManifestName);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(prepared, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            progress?.Report(1);
            AppLogger.Info("update", $"Update {prepared.Version} verified and staged at '{payloadDirectory}'.");
            return prepared;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    public static PreparedUpdate? FindPreparedUpdate()
    {
        try
        {
            AppPaths.EnsureCreated();
            foreach (var manifestPath in Directory.EnumerateFiles(
                         AppPaths.UpdateDirectory,
                         PreparedManifestName,
                         SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var prepared = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(manifestPath));
                if (prepared is null ||
                    !PreparedPathsAreSafe(prepared) ||
                    !ReleaseVersion.TryParse(prepared.Version, out var version) ||
                    version <= CurrentVersion)
                {
                    continue;
                }

                ValidatePayload(prepared.PayloadDirectory);
                return prepared;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("update", "Could not inspect staged updates.", exception);
        }

        return null;
    }

    private static bool PreparedPathsAreSafe(PreparedUpdate prepared)
    {
        var root = Path.GetFullPath(AppPaths.UpdateDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var payload = Path.GetFullPath(prepared.PayloadDirectory);
        var archive = Path.GetFullPath(prepared.ArchivePath);
        return payload.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               archive.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void RevalidatePreparedUpdate(PreparedUpdate prepared)
    {
        if (!PreparedPathsAreSafe(prepared))
        {
            throw new InvalidDataException("The staged update points outside the BeatzOnKeys update directory.");
        }

        if (!File.Exists(prepared.ArchivePath))
        {
            throw new FileNotFoundException("The verified update archive is missing.", prepared.ArchivePath);
        }

        using (var stream = File.OpenRead(prepared.ArchivePath))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!actualHash.Equals(prepared.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The staged update archive changed after verification.");
            }
        }

        if (Directory.Exists(prepared.PayloadDirectory))
        {
            Directory.Delete(prepared.PayloadDirectory, recursive: true);
        }

        Directory.CreateDirectory(prepared.PayloadDirectory);
        ExtractArchiveSafely(prepared.ArchivePath, prepared.PayloadDirectory);
        ValidatePayload(prepared.PayloadDirectory);
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

    public static bool TryLaunchApplyScript(PreparedUpdate prepared, out string error)
    {
        error = string.Empty;
        try
        {
            RevalidatePreparedUpdate(prepared);
            if (!CanWriteToInstallFolder())
            {
                error = $"The application folder is not writable: {ExecutableDirectory}";
                return false;
            }

            AppPaths.EnsureCreated();
            File.WriteAllText(ApplyScriptPath, ApplyScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(ApplyScriptPath);
            startInfo.ArgumentList.Add("-ApplicationProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-Payload");
            startInfo.ArgumentList.Add(prepared.PayloadDirectory);
            startInfo.ArgumentList.Add("-Target");
            startInfo.ArgumentList.Add(ExecutableDirectory);
            startInfo.ArgumentList.Add("-Executable");
            startInfo.ArgumentList.Add(ExecutablePath);
            startInfo.ArgumentList.Add("-Log");
            startInfo.ArgumentList.Add(ApplyLogPath);
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

    public static UpdateApplyReport? ConsumeLastApplyReport()
    {
        try
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
            var report = result.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
                ? new UpdateApplyReport(true, result)
                : new UpdateApplyReport(false, result);
            AppLogger.Info("update", $"Previous apply result: {result}");
            File.Delete(ApplyLogPath);
            return report;
        }
        catch (Exception exception)
        {
            AppLogger.Error("update", "Could not read the previous update report.", exception);
            return null;
        }
    }

    private static async Task<(bool Exists, long Size)> ProbeAssetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await DownloadClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            return (response.IsSuccessStatusCode, response.Content.Headers.ContentLength ?? 0);
        }
        catch
        {
            return (true, 0);
        }
    }

    private static async Task<string> DownloadChecksumAsync(
        string url,
        CancellationToken cancellationToken)
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

    private static async Task DownloadFileAsync(
        string url,
        string targetPath,
        long expectedLength,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await DownloadClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalLength = response.Content.Headers.ContentLength ?? expectedLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            if (totalLength > 0)
            {
                progress?.Report(Math.Clamp(totalRead / (double)totalLength * 0.82, 0, 0.82));
            }
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalLength > 0 && totalRead != totalLength)
        {
            throw new IOException("The update archive download was incomplete.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void ExtractArchiveSafely(string archivePath, string payloadDirectory)
    {
        var root = Path.GetFullPath(payloadDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(payloadDirectory, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe path in update archive: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void ValidatePayload(string payloadDirectory)
    {
        var required = new[]
        {
            Path.Combine(payloadDirectory, "BeatzOnKeys.exe"),
            Path.Combine(payloadDirectory, "python", "python.exe"),
            Path.Combine(payloadDirectory, "Tools", "transcribe.py")
        };
        var missing = required.FirstOrDefault(path => !File.Exists(path) || new FileInfo(path).Length == 0);
        if (missing is not null)
        {
            throw new InvalidDataException($"The update package is incomplete: {missing}");
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
        UpdateStatus.NoDownload => result.Error ?? "The latest release has no portable archive.",
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

    private const string ApplyScript = """
param(
    [Parameter(Mandatory = $true)][int] $ApplicationProcessId,
    [Parameter(Mandatory = $true)][string] $Payload,
    [Parameter(Mandatory = $true)][string] $Target,
    [Parameter(Mandatory = $true)][string] $Executable,
    [Parameter(Mandatory = $true)][string] $Log
)
$ErrorActionPreference = 'Stop'
function Write-UpdateLog([string] $Message) {
    $directory = Split-Path -Parent $Log
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Add-Content -LiteralPath $Log -Value "[$([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff'))] $Message" -Encoding UTF8
}
try {
    Write-UpdateLog "Waiting for process $ApplicationProcessId."
    $exited = $false
    for ($attempt = 0; $attempt -lt 120; $attempt++) {
        if (-not (Get-Process -Id $ApplicationProcessId -ErrorAction SilentlyContinue)) {
            $exited = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $exited) { throw "The application did not exit within 60 seconds." }
    Write-UpdateLog "Copying verified payload from $Payload to $Target."
    & robocopy.exe $Payload $Target /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $copyCode = $LASTEXITCODE
    if ($copyCode -ge 8) { throw "Robocopy failed with code $copyCode." }
    if (-not (Test-Path -LiteralPath $Executable)) { throw "Updated executable is missing: $Executable" }
    Write-UpdateLog "RESULT=OK"
    Start-Process -FilePath $Executable -WorkingDirectory $Target
    $updateRoot = Split-Path -Parent $Payload
    Remove-Item -LiteralPath $updateRoot -Recurse -Force -ErrorAction SilentlyContinue
}
catch {
    Write-UpdateLog "RESULT=FAILED $($_.Exception.Message)"
    if (Test-Path -LiteralPath $Executable) {
        Start-Process -FilePath $Executable -WorkingDirectory $Target
    }
}
finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";
}
