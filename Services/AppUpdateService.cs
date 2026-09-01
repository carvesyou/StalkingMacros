using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace D2MacroNative.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string Tag,
    string DownloadUrl,
    string ReleasePageUrl);

public sealed class AppUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/carvesyou/StalkingMacros/releases/latest";
    private const string ExecutableAssetName = "stalking-macro.exe";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("stalking-macro-updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public async Task<AppUpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(LatestReleaseApi, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latestVersion))
            throw new InvalidOperationException($"The latest release tag '{tag}' is not a valid version.");
        if (latestVersion <= CurrentVersion) return null;

        string? executableUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name?.Equals(ExecutableAssetName, StringComparison.OrdinalIgnoreCase) == true)
                executableUrl = url;
        }

        if (string.IsNullOrWhiteSpace(executableUrl))
            throw new InvalidOperationException("The latest release is missing its EXE asset.");

        var releasePage = root.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() ?? string.Empty
            : string.Empty;
        return new AppUpdateInfo(latestVersion, tag, executableUrl, releasePage);
    }

    public async Task<string> DownloadAsync(
        AppUpdateInfo update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "D2MacroNative", "updates", update.Version.ToString());
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, ExecutableAssetName);
        var temporary = destination + ".download";

        using var client = CreateClient();
        using var response = await client.GetAsync(update.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
        {
            var buffer = new byte[1024 * 128];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total is > 0) progress?.Report((int)Math.Clamp(received * 100 / total.Value, 0, 100));
            }
        }

        File.Move(temporary, destination, true);
        progress?.Report(100);
        return destination;
    }

    public static void LaunchInstaller(string downloadedExecutable)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not provide the current executable path.");
        var arguments = $"--apply-update {Environment.ProcessId} \"{currentExecutable.Replace("\"", "\\\"")}\"";
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = downloadedExecutable,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(downloadedExecutable)!
        }) ?? throw new InvalidOperationException("Windows could not start the update installer.");
    }

    public static bool TryApplyUpdate(string[] args, out string? error)
    {
        error = null;
        if (args.Length != 3 || !args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            if (!int.TryParse(args[1], out var parentProcessId) || parentProcessId <= 0)
                throw new InvalidOperationException("The update process ID is invalid.");
            var target = Path.GetFullPath(args[2]);
            var source = Environment.ProcessPath
                ?? throw new InvalidOperationException("The updater executable path is unavailable.");

            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                parent.WaitForExit(60000);
            }
            catch (ArgumentException)
            {
                // The old version already exited.
            }

            Exception? lastError = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    File.Copy(source, target, true);
                    lastError = null;
                    break;
                }
                catch (IOException ex) { lastError = ex; Thread.Sleep(250); }
                catch (UnauthorizedAccessException ex) { lastError = ex; Thread.Sleep(250); }
            }
            if (lastError is not null) throw lastError;

            _ = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target)!
            }) ?? throw new InvalidOperationException("The updated application could not restart.");
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return true;
    }
}
