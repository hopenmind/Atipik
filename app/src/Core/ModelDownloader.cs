using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Atypik.Core;

/// <summary>
/// Downloads an optional correction model into the app-managed folder. Models are
/// deliberately NOT shipped in the repository (they are large); they are fetched
/// on demand from Settings, so the user picks which one - and how big - to pull.
/// </summary>
public static class ModelDownloader
{
    /// <summary>One downloadable model: what it is, where it lives, how big it is.</summary>
    public sealed record ModelOption(
        string Id,
        string DisplayName,
        string Url,
        long   SizeBytes,
        string Blurb)
    {
        /// <summary>Human download size, e.g. "0.99 GB" / "1.93 GB" (decimal GB, as Hugging Face shows).</summary>
        public string SizeText => $"{SizeBytes / 1_000_000_000.0:0.00} GB";

        /// <summary>"Name - 1.93 GB", for a one-line chooser entry.</summary>
        public string Label => $"{DisplayName} - {SizeText}";
    }

    /// <summary>
    /// Models offered in-app. Both are non-thinking, multilingual Qwen2.5 instruct
    /// models, abliterated so they never refuse to reproduce the user's own text
    /// (Q4_K_M quantisation). Ordered lightest first so the user can weigh size and
    /// speed against correction quality. Sizes are the exact byte lengths served by
    /// Hugging Face. Hosted on Hugging Face; override the effective URL at runtime
    /// via the <c>model_url</c> preference to point at your own mirror.
    /// </summary>
    public static readonly ModelOption[] Catalog =
    {
        new ModelOption(
            "qwen2.5-1.5b",
            "Qwen2.5 1.5B Instruct (abliterated)",
            "https://huggingface.co/mradermacher/Qwen2.5-1.5B-Instruct-abliterated-GGUF/resolve/main/Qwen2.5-1.5B-Instruct-abliterated.Q4_K_M.gguf",
            986_049_088L,
            "Lighter and faster - best on modest PCs. Corrections are simpler."),
        new ModelOption(
            "qwen2.5-3b",
            "Qwen2.5 3B Instruct (abliterated)",
            "https://huggingface.co/mradermacher/Qwen2.5-3B-Instruct-abliterated-GGUF/resolve/main/Qwen2.5-3B-Instruct-abliterated.Q4_K_M.gguf",
            1_929_903_520L,
            "More accurate correction. Needs more RAM and is slower on CPU."),
    };

    /// <summary>Default (recommended) model - the 3B, the best correction quality.</summary>
    public static ModelOption DefaultOption => Catalog[1];

    /// <summary>Back-compat default download URL (the recommended model's URL).</summary>
    public static string DefaultUrl => DefaultOption.Url;

    /// <summary>Look up a catalog entry by id; null if unknown.</summary>
    public static ModelOption? ById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in Catalog) if (m.Id == id) return m;
        return null;
    }

    /// <summary>The effective URL (pref override, else the default).</summary>
    public static string GetUrl()
    {
        string saved = AppPrefs.Get("model_url", DefaultUrl);
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
        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct);

            // Scope the file stream so its handle is CLOSED before the rename.
            // File.Move on a file that still holds an open FileShare.None handle
            // raises a sharing violation on Windows; that left a byte-complete
            // ".part" that was never promoted to the final model (the model then
            // "failed to load" because the .gguf simply did not exist).
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                 FileShare.None, 1 << 16, useAsync: true))
            {
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
            } // handle released here - the temp file can now be moved safely

            File.Move(tmp, destPath, overwrite: true);
            progress.Report(100);
        }
        catch
        {
            // Never leave a partial or locked temp file behind: the next attempt
            // starts clean instead of stacking on top of a stale ".part".
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
