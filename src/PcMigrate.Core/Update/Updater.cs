using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PcMigrate.Core.Models;

namespace PcMigrate.Core.Update;

public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string Notes, string PageUrl);

/// <summary>
/// Keeps the portable exes up to date from the project's GitHub releases. Every push to main
/// publishes a release tagged v{version}.{build}; the newest one is compared with this build's
/// own number. Updating downloads the new exe beside the running one, checks its size and
/// SHA-256 against what GitHub reports, then swaps the two files. Windows allows a running exe
/// to be renamed, so the old one becomes .old and is deleted the next time the new one starts.
/// </summary>
public static class Updater
{
    public const string Repo = "tylorfaber-arch/pc-migrate";
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repo}/releases/latest");

    /// <summary>The newer release, or null if this is the newest, there's no internet, or GitHub doesn't answer.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string assetName, Version current, CancellationToken ct = default, HttpClient? http = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var client = http is null ? NewClient() : null;
            using var response = await (http ?? client!).GetAsync(Latest, timeout.Token);
            if (!response.IsSuccessStatusCode) return null; // 404 until the first release exists
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            return Parse(json, assetName, current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>Reads GitHub's release JSON. Separate so it can be tested without the internet.</summary>
    public static UpdateInfo? Parse(string releaseJson, string assetName, Version current)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        if (Normalize(version) <= Normalize(current)) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase)) continue;
            // GitHub lists each asset's checksum as "sha256:<hex>".
            var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
            return new UpdateInfo(version, tag,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha,
                root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "",
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "");
        }
        return null;
    }

    // 0.1.0 and 0.1.0.0 should compare equal.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Downloads and checks the new exe, saved as {exe}.new. Reports progress as 0..1.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, string exePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var target = exePath + ".new";
        using var client = NewClient();
        using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(target))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(update.Size > 0 ? (double)total / update.Size : 0);
            }
        }

        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (total != update.Size || (update.Sha256 is not null && sha != update.Sha256))
        {
            File.Delete(target);
            throw new InvalidDataException("The download didn't match what GitHub says it should be, so it wasn't used. Try again later.");
        }
        return target;
    }

    /// <summary>Puts the downloaded exe in place of the running one. The old one is kept as .old until the next start.</summary>
    public static void Swap(string exePath, string newPath)
    {
        var old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newPath, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Deletes the previous version left by an update. Call once the app has started properly.</summary>
    public static void CleanUp(string exePath)
    {
        foreach (var leftover in new[] { exePath + ".old", exePath + ".new" })
        {
            try { if (File.Exists(leftover)) File.Delete(leftover); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pc-migrate", ToolInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
