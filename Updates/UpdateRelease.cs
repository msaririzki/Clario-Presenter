using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Clario_Presenter.Updates;

internal sealed record UpdateRelease(Version Version, Uri Installer, long Size, string Digest, string Summary)
{
    internal const string Repository = "https://github.com/msaririzki/Clario-Presenter";
    internal const string Filename = "Clario-Presenter-Setup-x64.exe";
    internal Uri Page => new($"{Repository}/releases/tag/v{Version}");

    internal static UpdateRelease? Parse(string json, Version installed)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString();
        if (tag is not { Length: > 1 } || tag[0] != 'v' || !Version.TryParse(tag[1..], out var version)
            || version.Build < 0 || version.Revision >= 0 || tag != $"v{version}" || version <= installed) return null;
        var url = new Uri($"{Repository}/releases/download/{tag}/{Filename}");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != Filename) continue;
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (asset.GetProperty("browser_download_url").GetString() != url.AbsoluteUri || size is <= 0 or > 536870912
                || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                || !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("Informasi installer tidak cocok.");
            var body = release.TryGetProperty("body", out var note) ? note.GetString() : null;
            var summary = body?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => !line.StartsWith('#'))?.Replace("**", "");
            if (summary?.Length > 300) summary = summary[..297] + "…";
            return new(version, url, size, digest[7..].ToUpperInvariant(), summary ?? "");
        }
        throw new InvalidDataException("Installer Windows belum tersedia.");
    }

    internal async Task DownloadAsync(HttpClient client, string path, CancellationToken token)
    {
        var part = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await client.GetAsync(Installer, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != Size)
                throw new InvalidDataException("Ukuran installer tidak cocok.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > Size) throw new InvalidDataException("Installer melebihi ukuran rilis.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (written != Size || Convert.ToHexString(hash.GetHashAndReset()) != Digest)
                    throw new InvalidDataException("Installer belum lolos pemeriksaan.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(part, path, true);
        }
        finally { File.Delete(part); }
    }
}
