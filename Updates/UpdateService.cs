using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Clario_Presenter.Updates;

internal sealed class UpdateService
{
    private const string Feed = "https://api.github.com/repos/msaririzki/Clario-Presenter/releases/latest";
    private static readonly HttpClient DefaultClient = new() { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1048576 };
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clario Presenter", "Updates");
    internal static readonly Version Installed = Version.Parse(typeof(UpdateService).Assembly.GetName().Version!.ToString(3));
    private static readonly string Pin = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdatePublisherSHA256")?.Value ?? "";
    internal static bool InstalledCopy { get; } = IsInstalled();

    private static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return false;
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5EC3B50D-5396-4381-8E97-491EF6D3AC27}_is1";
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var user = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, view);
            using var entry = user.OpenSubKey(uninstall);
            if (entry?.GetValue("InstallLocation") is string location
                && string.Equals(Path.Combine(location, "Clario.Presenter.exe"), exe, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static bool CanStage => InstalledCopy && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && Pin.Length == 64 && Pin.All(Uri.IsHexDigit);
    internal UpdateRelease? Release { get; private set; }
    internal string? ReadyPath { get; private set; }
    internal string Message { get; private set; } = "Clario memeriksa versi terbaru sekali sehari.";
    internal bool Busy => _operation is not null;
    internal bool Deferred { get; private set; }
    private CancellationTokenSource? _operation;
    private DateTime _retryAfter;
    internal event Action? Changed;
    private readonly HttpClient _client;
    private readonly string _cacheFolder;
    internal UpdateService(HttpClient? client = null, string? cacheFolder = null)
    {
        _client = client ?? DefaultClient;
        _cacheFolder = cacheFolder ?? Folder;
    }

    static UpdateService() => DefaultClient.DefaultRequestHeaders.UserAgent.ParseAdd("Clario-Presenter/" + Installed);

    internal void Pause() => _operation?.Cancel();
    internal void Later() { Deferred = true; Changed?.Invoke(); }

    internal async Task CheckAsync(bool manual)
    {
        if (Busy || (!manual && DateTime.UtcNow < _retryAfter)) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _operation = stop;
        var token = stop.Token;
        try
        {
            Message = "Memeriksa pembaruan…";
            Changed?.Invoke();
            Directory.CreateDirectory(_cacheFolder);
            var cache = Path.Combine(_cacheFolder, "release.json");
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cache);
            bool cached = !manual && File.Exists(cache) && age >= TimeSpan.Zero && age < TimeSpan.FromDays(1);
            string json = cached ? await File.ReadAllTextAsync(cache, token) : await _client.GetStringAsync(Feed, token);
            UpdateRelease? latest;
            try { latest = UpdateRelease.Parse(json, Installed); }
            catch when (cached) { File.Delete(cache); throw; }
            if (!cached)
            {
                var temporary = cache + "." + Guid.NewGuid().ToString("N");
                try { await File.WriteAllTextAsync(temporary, json, token); File.Move(temporary, cache, true); }
                finally { File.Delete(temporary); }
            }
            token.ThrowIfCancellationRequested();
            if (Release != latest) { ReadyPath = null; Deferred = false; }
            Release = latest;
            if (latest is null) Message = "Belum ada versi yang lebih baru.";
            else if (!CanStage) Message = $"Clario {latest.Version} tersedia. Unduh installer dari rilis resmi.";
            else
            {
                var path = Path.Combine(Folder, $"Clario-{latest.Version}.exe");
                Message = $"Menyiapkan Clario {latest.Version}…";
                Changed?.Invoke();
                if (!File.Exists(path) || !await VerifyAsync(path, latest, token))
                {
                    await latest.DownloadAsync(_client, path, token);
                    if (!await VerifyAsync(path, latest, token))
                        throw new InvalidDataException("Tanda tangan installer belum terverifikasi. Buka rilis resmi.");
                }
                token.ThrowIfCancellationRequested();
                ReadyPath = path;
                foreach (var old in Directory.EnumerateFiles(Folder, "Clario-*.exe"))
                {
                    if (old == path) continue;
                    try { File.Delete(old); } catch (IOException) { }
                }
                Message = $"Clario {latest.Version} siap dipasang.";
            }
            _retryAfter = File.GetLastWriteTimeUtc(cache).AddDays(1);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Message = "Pemeriksaan ditunda. Coba lagi saat Clario senggang.";
            _retryAfter = DateTime.UtcNow.AddMinutes(1);
        }
        catch (Exception ex)
        {
            ReadyPath = null;
            Message = ex is InvalidDataException ? ex.Message : "Pembaruan belum bisa diperiksa. Coba lagi nanti.";
            _retryAfter = DateTime.UtcNow.AddMinutes(30);
        }
        finally { _operation = null; Changed?.Invoke(); }
    }

    private static ProcessStartInfo Helper(string path, UpdateRelease release)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(AppContext.BaseDirectory, "Updates", "Install-Update.ps1"), "-Installer", path,
            "-Digest", release.Digest, "-Size", release.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-Publisher", Pin, "-Version", release.Version.ToString() }) psi.ArgumentList.Add(arg);
        return psi;
    }

    private static async Task<bool> VerifyAsync(string path, UpdateRelease release, CancellationToken token)
    {
        using var process = Process.Start(Helper(path, release))!;
        try { await process.WaitForExitAsync(token); return process.ExitCode == 0; }
        finally { if (!process.HasExited) process.Kill(); }
    }

    internal async Task StartInstallationAsync()
    {
        if (!CanStage || Busy || ReadyPath is null || Release is null) throw new InvalidOperationException("Installer belum siap.");
        var psi = Helper(ReadyPath, Release);
        psi.ArgumentList.Add("-ParentId");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        using var helper = Process.Start(psi) ?? throw new InvalidOperationException("Installer belum bisa dibuka.");
        try
        {
            if (await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)) != "READY")
                throw new InvalidOperationException("Windows belum dapat memverifikasi installer. Coba lagi atau buka rilis resmi.");
        }
        catch { if (!helper.HasExited) helper.Kill(); throw; }
    }
}
