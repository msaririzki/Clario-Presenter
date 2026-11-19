using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Clario_Presenter.Updates;

internal static class UpdateChecks
{
    internal static async Task RunAsync()
    {
        var temp = Directory.CreateTempSubdirectory("Clario-update-check-");
        try
        {
            var payload = "test installer"u8.ToArray();
            const string tag = "v99.0.0";
            string Feed(string version = tag, bool draft = false, bool beta = false, string? url = null) =>
                JsonSerializer.Serialize(new { tag_name = version, draft, prerelease = beta, assets = new[] { new
                {
                    name = UpdateRelease.Filename, size = payload.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)),
                    browser_download_url = url ?? $"{UpdateRelease.Repository}/releases/download/{version}/{UpdateRelease.Filename}"
                } } });
            var installed = new Version(1, 0, 17);
            foreach (var json in new[] { Feed("v1.0.16"), Feed("v1.0.17"), Feed("v1.0.18-beta"), Feed(draft:true), Feed(beta:true) })
                if (UpdateRelease.Parse(json, installed) is not null) throw new Exception("Unexpected update.");
            try { UpdateRelease.Parse(Feed(url:"https://example.invalid/setup.exe"), installed); throw new Exception("Wrong origin accepted."); }
            catch (InvalidDataException) { }
            var release = UpdateRelease.Parse(Feed(), installed)!;
            var handler = new Handler();
            using var client = new HttpClient(handler);
            var path = Path.Combine(temp.FullName, "verified.exe");
            handler.Serve = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            await release.DownloadAsync(client, path, default);
            foreach (var damaged in new[] { payload[..^1], new byte[payload.Length], new byte[payload.Length + 1] })
            {
                handler.Serve = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(damaged) });
                try { await release.DownloadAsync(client, path, default); throw new Exception("Damaged download accepted."); }
                catch (InvalidDataException) { }
            }
            if (!File.ReadAllBytes(path).SequenceEqual(payload) || temp.GetFiles("*.part").Length != 0)
                throw new Exception("Retry damaged the verified copy.");
            var update = new UpdateService(client, temp.FullName);
            handler.Serve = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed()) });
            await update.CheckAsync(true);
            var count = handler.Count;
            var restarted = new UpdateService(client, temp.FullName);
            await restarted.CheckAsync(false);
            if (handler.Count != count || restarted.Release != release || restarted.ReadyPath is not null)
                throw new Exception("Daily caching or unsigned fallback failed.");
            handler.Serve = async token => { await Task.Delay(Timeout.Infinite, token); throw new Exception(); };
            var pending = restarted.CheckAsync(true);
            await restarted.CheckAsync(true);
            restarted.Pause();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            if (restarted.Busy || handler.Count != count + 1) throw new Exception("Concurrent or cancelled operation failed.");
            handler.Serve = _ => throw new HttpRequestException("offline");
            await restarted.CheckAsync(true);
            if (restarted.Busy || restarted.ReadyPath is not null) throw new Exception("Offline fallback failed.");
            Console.WriteLine("PASS: stable versions, official origin, integrity, atomic download, caching, single-flight, cancellation and offline.");
        }
        finally { temp.Delete(true); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal Func<CancellationToken, Task<HttpResponseMessage>> Serve = null!;
        internal int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Serve(cancellationToken); }
    }
}
