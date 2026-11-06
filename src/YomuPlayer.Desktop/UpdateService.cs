using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace YomuPlayer.Desktop;

public sealed record UpdateAsset(string Url, string? Sha256);
public sealed record UpdateDownloadProgress(double Fraction, long Bytes, long Total);
public sealed record UpdateInfo(
    string Version,
    string Channel,
    DateTimeOffset Published,
    IReadOnlyList<string> Notes,
    UpdateAsset? Setup,
    UpdateAsset? Portable)
{
    public string NotesText => string.Join("\n", Notes);
}

public enum UpdateLaunchResult { None, ExitApp }

// Small, serverless updater. The manifest URL is supplied by update-source.json next to the exe (or
// YOMU_UPDATE_MANIFEST for local testing), so GitHub details are not hardcoded into the app.
public static class UpdateService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    { Timeout = TimeSpan.FromMinutes(20) };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static UpdateInfo? _current;

    public static UpdateInfo? Current => _current;
    public static event Action<UpdateInfo>? UpdateAvailable;

    public static async Task<UpdateInfo?> CheckAsync(bool manual = false, CancellationToken ct = default)
    {
        var source = ResolveManifestSource(manual);
        await Gate.WaitAsync(ct);
        try
        {
            var json = await ReadSourceAsync(source, ct);
            var info = ParseManifest(json, source);
            if (!VersionUtil.IsNewer(info.Version, AppPaths.Version)) return null;

            _current = info;
            UpdateAvailable?.Invoke(info);
            return info;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<UpdateLaunchResult> DownloadAndInstallAsync(
        UpdateInfo info, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var asset = AppPaths.IsPortable ? info.Portable : info.Setup;
        if (asset is null || string.IsNullOrWhiteSpace(asset.Url))
            throw new InvalidOperationException(
                AppPaths.IsPortable
                    ? "This release does not include a portable update."
                    : "This release does not include an installer update.");

        Directory.CreateDirectory(AppPaths.UpdatesRoot);
        var kind = AppPaths.IsPortable ? "portable" : "setup";
        var extension = AppPaths.IsPortable ? ".zip" : ".exe";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
        var path = Path.Combine(AppPaths.UpdatesRoot,
            $"YOMU-Player-{kind}-{info.Version}-{stamp}{extension}");
        var temp = path + ".part";

        try
        {
            await DownloadAsync(asset.Url, temp, progress, ct);
            if (!string.IsNullOrWhiteSpace(asset.Sha256))
            {
                var actual = await HashFileAsync(temp, ct);
                if (!string.Equals(actual, asset.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The downloaded update failed its SHA-256 check.");
            }
            File.Move(temp, path);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        if (AppPaths.IsPortable)
        {
            StartPortableUpdater(path);
            return UpdateLaunchResult.ExitApp;
        }

        StartInstallerAfterExit(path);
        return UpdateLaunchResult.ExitApp;
    }

    // The installer must not touch the install folder while this process still has the exe and DLLs mapped.
    // Start a tiny hidden helper that waits for YOMU to exit before launching setup.
    private static void StartInstallerAfterExit(string setupPath)
    {
        var scriptPath = Path.Combine(AppPaths.UpdatesRoot,
            "apply-installer-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script = """
            param(
                [Parameter(Mandatory=$true)][int]$ProcessId,
                [Parameter(Mandatory=$true)][string]$SetupPath
            )
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { exit 1 }
            Start-Sleep -Milliseconds 600
            Start-Process -FilePath $SetupPath -ArgumentList '/SILENT','/SUPPRESSMSGBOXES','/CLOSEAPPLICATIONS','/NORESTART','/RELAUNCH'
            """;
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ProcessId");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-SetupPath");
        psi.ArgumentList.Add(setupPath);
        Process.Start(psi);
    }

    private static string ResolveManifestSource(bool manual)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("YOMU_UPDATE_MANIFEST");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        try
        {
            if (File.Exists(AppPaths.UpdateSourcePath))
            {
                var json = JsonNode.Parse(File.ReadAllText(AppPaths.UpdateSourcePath));
                var url = json?["manifestUrl"]?.GetValue<string>()
                          ?? json?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(url)) return url.Trim();
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("update-source.json could not be read: " + e.Message);
        }

        throw new InvalidOperationException(manual
            ? "No update server is configured. Add update-source.json or set YOMU_UPDATE_MANIFEST."
            : "No update server is configured.");
    }

    private static async Task<string> ReadSourceAsync(string source, CancellationToken ct)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return await Http.GetStringAsync(uri, ct);

        var path = Path.IsPathRooted(source) ? source : Path.Combine(AppPaths.BaseDirectory, source);
        if (!File.Exists(path)) throw new InvalidOperationException("Update manifest not found: " + path);
        return await File.ReadAllTextAsync(path, ct);
    }

    private static UpdateInfo ParseManifest(string json, string source)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (Exception e) { throw new InvalidOperationException("The update manifest is not valid JSON.", e); }

        var channel = root?["channel"]?.GetValue<string>() ?? "";
        if (!string.Equals(channel, AppPaths.Channel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The update manifest is for {channel}, but this app is on {AppPaths.Channel}.");

        var version = root?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("The update manifest has no version.");

        var notes = root?["notes"]?.AsArray()
            .Select(n => n?.GetValue<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList() ?? new List<string>();

        DateTimeOffset published = DateTimeOffset.UtcNow;
        if (DateTimeOffset.TryParse(root?["published"]?.GetValue<string>(), out var parsed))
            published = parsed;

        return new UpdateInfo(
            version.Trim(),
            channel,
            published,
            notes,
            ReadAsset(root?["setup"]),
            ReadAsset(root?["portable"]));
    }

    private static UpdateAsset? ReadAsset(JsonNode? node)
    {
        var url = node?["url"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url)) return null;
        return new UpdateAsset(url.Trim(), node?["sha256"]?.GetValue<string>());
    }

    private static async Task DownloadAsync(
        string url, string path, IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        await using var input = await res.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report(new UpdateDownloadProgress(
                Math.Clamp((double)done / total, 0, 1), done, total));
        }
        if (total <= 0) progress?.Report(new UpdateDownloadProgress(1, done, 0));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    // Portable updates cannot replace a running exe. A short hidden PowerShell helper waits for this process
    // to exit, unpacks the portable zip over the app folder while keeping data, then relaunches the app.
    private static void StartPortableUpdater(string zipPath)
    {
        var scriptPath = Path.Combine(AppPaths.UpdatesRoot,
            "apply-portable-update-" + Guid.NewGuid().ToString("N") + ".ps1");
        var script = """
            param(
                [Parameter(Mandatory=$true)][int]$ProcessId,
                [Parameter(Mandatory=$true)][string]$ZipPath,
                [Parameter(Mandatory=$true)][string]$Target,
                [Parameter(Mandatory=$true)][string]$ExeName
            )
            $ErrorActionPreference = 'Stop'
            Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { exit 1 }
            Start-Sleep -Milliseconds 700
            $stage = Join-Path $env:TEMP ('YomuUpdate-' + [guid]::NewGuid().ToString('N'))
            try {
                Expand-Archive -LiteralPath $ZipPath -DestinationPath $stage -Force
                Get-ChildItem -LiteralPath $stage -Force | ForEach-Object {
                    if ($_.Name -ne 'data') {
                        Copy-Item -LiteralPath $_.FullName -Destination $Target -Recurse -Force
                    }
                }
            }
            finally {
                Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
            }
            Start-Process -FilePath (Join-Path $Target $ExeName)
            """;
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ProcessId");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("-ZipPath");
        psi.ArgumentList.Add(zipPath);
        psi.ArgumentList.Add("-Target");
        psi.ArgumentList.Add(AppPaths.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        psi.ArgumentList.Add("-ExeName");
        psi.ArgumentList.Add(Path.GetFileName(Environment.ProcessPath) ?? "YomuPlayer.exe");
        Process.Start(psi);
    }
}

internal static class VersionUtil
{
    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    private static int Compare(string a, string b)
    {
        var (aCore, aPre) = Split(a);
        var (bCore, bPre) = Split(b);
        var core = CompareCore(aCore, bCore);
        if (core != 0) return core;

        if (aPre is null) return bPre is null ? 0 : 1;
        if (bPre is null) return -1;
        return ComparePre(aPre, bPre);
    }

    // Semver-style prerelease ordering: nightly.9 is older than nightly.10 (plain string
    // comparison would put ".11" before ".9" and hide newer nightlies).
    private static int ComparePre(string a, string b)
    {
        var ap = a.Split('.');
        var bp = b.Split('.');
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var aNumeric = int.TryParse(ap[i], out var aNumber);
            var bNumeric = int.TryParse(bp[i], out var bNumber);
            if (aNumeric && bNumeric)
            {
                var number = aNumber.CompareTo(bNumber);
                if (number != 0) return number;
                continue;
            }
            if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
            var text = string.Compare(ap[i], bp[i], StringComparison.OrdinalIgnoreCase);
            if (text != 0) return text;
        }
        return ap.Length.CompareTo(bp.Length);
    }

    private static (string Core, string? Pre) Split(string version)
    {
        var clean = version.Split('+')[0].Trim();
        var dash = clean.IndexOf('-');
        return dash < 0 ? (clean, null) : (clean[..dash], clean[(dash + 1)..]);
    }

    private static int CompareCore(string a, string b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
