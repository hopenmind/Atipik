using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Atypik.Core;

/// <summary>
/// Downloads the optional correction model from GitHub into the right folder.
/// The model is deliberately NOT shipped in the repository (it is large); it is
/// published as a GitHub release asset and fetched on demand from Settings.
/// </summary>
public static class ModelDownloader
{
    /// <summary>
    /// Default release-asset URL. Replace OWNER/REPO with the actual GitHub
    /// location, or override at runtime via the <c>model_url</c> preference.
    /// </summary>
    public const string DefaultUrl =
        "https://github.com/OWNER/REPO/releases/download/model/textualiser.gguf";

    /// <summary>The effective URL (pref override, else the default).</summary>
    public static string GetUrl()
    {
        string? saved = AppPrefs.Get("model_url", DefaultUrl);
        return string.IsNullOrWhiteSpace(saved) ? DefaultUrl : saved;
    }

    /// <summary>
    /// Download <paramref name="url"/> to <paramref name="destPath"/> atomically
    /// (a .part temp file is moved into place on success). <paramref name="progress"/>
    /// receives 0-100 (null if the server does not report a length).
    /// </summary>
    public static async Task DownloadAsync(
        string url,
        string destPath,
        IProgress<int?> progress,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        if (total is null || total <= 0) total = null;

        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = destPath + ".part";
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                            FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress.Report(total is long t ? (int?)(read * 100 / t) : null);
        }
        await fs.FlushAsync(ct);

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);

        progress.Report(100);
    }
}
