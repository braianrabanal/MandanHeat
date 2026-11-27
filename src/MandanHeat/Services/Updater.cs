using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MandanHeat.Services;

/// <summary>Una versión publicada en GitHub más nueva que la instalada.</summary>
public sealed record UpdateInfo(Version Version, string PageUrl, string ZipUrl, string? Sha256)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// Actualización automática desde las Releases de GitHub.
/// Descarga el zip de la última Release, sustituye los archivos de la carpeta de la app (Windows permite
/// renombrar un .exe o .dll en uso) y reinicia la app. Solo funciona con la carpeta que publica GitHub Actions.
/// </summary>
internal static class Updater
{
    private const string Repo = "braianrabanal/MandanHeat";
    private const string AssetName = "MandanHeat-win-x64.zip";
    private const string ExeName = "MandanHeat.exe";
    private const string OldSuffix = ".old";
    public const string UpdatedArg = "--updated";

    public static string ReleasesPage => $"https://github.com/{Repo}/releases/latest";

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>Archivo que publicar.ps1 incluye en la carpeta publicada; una compilación de desarrollo no lo tiene.</summary>
    private const string ReleaseMarker = "MandanHeat.release";

    /// <summary>true si la app se ejecuta desde la carpeta descargada de las Releases (no desde una compilación de desarrollo).</summary>
    public static bool CanSelfUpdate =>
        !string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Path.Combine(AppContext.BaseDirectory, ReleaseMarker));

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MandanHeat/{CurrentVersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Consulta la última Release. Devuelve null si no hay una versión más nueva.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(20));
        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remote)) return null;
        remote = Normalize(remote);
        if (remote <= CurrentVersion) return null;

        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.IsNullOrEmpty(url)) return null;
            // GitHub publica la suma SHA-256 de cada archivo ("sha256:…"); si está, se comprueba la descarga.
            string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest &&
                          digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..]
                : null;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            return new UpdateInfo(remote, page, url, sha);
        }
        return null;
    }

    /// <summary>Descarga la versión nueva y sustituye los archivos de la app. Hay que llamar después a <see cref="Restart"/>.</summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var appDir = Path.GetFullPath(AppContext.BaseDirectory);
        var zipPath = Path.Combine(Path.GetTempPath(), $"MandanHeat-{info.VersionText}.zip");
        var stage = Path.Combine(Path.GetTempPath(), $"MandanHeat-{info.VersionText}-update");
        var applied = new List<(string Target, bool HadOld)>();

        try
        {
            using (var http = CreateClient(TimeSpan.FromMinutes(15)))
            using (var resp = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }

            if (info.Sha256 != null)
            {
                await using var fs = File.OpenRead(zipPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Loc.T("La descarga está dañada (la suma de comprobación no coincide)."));
            }

            // Se extrae todo a una carpeta temporal; el zip puede llevar los archivos en una carpeta raíz ("MandanHeat/").
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            Directory.CreateDirectory(stage);
            var stageFull = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var exeEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                               ?? throw new InvalidDataException(Loc.F("El zip no contiene {0}.", ExeName));
                var prefix = exeEntry.FullName[..^exeEntry.Name.Length];
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Length == 0 || !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.GetFullPath(Path.Combine(stage, entry.FullName[prefix.Length..]));
                    if (!dest.StartsWith(stageFull, StringComparison.OrdinalIgnoreCase)) continue; // ruta sospechosa
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            var newExe = Path.Combine(stage, ExeName);
            var fvi = FileVersionInfo.GetVersionInfo(newExe);
            var newVersion = new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart);
            if (fvi.ProductName != "MandanHeat" || newVersion != info.Version)
                throw new InvalidDataException(Loc.F("El ejecutable descargado no es MandanHeat {0}.", info.VersionText));

            ct.ThrowIfCancellationRequested();
            // Un .exe o .dll en uso no se puede sobrescribir, pero sí renombrar: se aparta cada archivo
            // antiguo (.old) y se pone el nuevo en su sitio. Si algo falla, se deshace todo.
            try
            {
                foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(appDir, Path.GetRelativePath(stage, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    bool hadOld = File.Exists(target);
                    if (hadOld) File.Move(target, target + OldSuffix, overwrite: true);
                    applied.Add((target, hadOld));
                    File.Move(file, target, overwrite: true);
                }
            }
            catch
            {
                foreach (var (target, hadOld) in Enumerable.Reverse(applied))
                {
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                        if (hadOld) File.Move(target + OldSuffix, target, overwrite: true);
                    }
                    catch { }
                }
                throw;
            }
        }
        finally
        {
            TryDelete(zipPath);
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    /// <summary>Arranca la versión recién instalada; quien llama debe cerrar la app justo después.</summary>
    public static void Restart(bool minimized)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Environment.GetCommandLineArgs().Contains("--no-elevate")) psi.ArgumentList.Add("--no-elevate");
        psi.ArgumentList.Add(UpdatedArg);
        if (minimized) psi.ArgumentList.Add("--minimized");
        Process.Start(psi);
    }

    /// <summary>Borra los archivos antiguos (.old) que deja una actualización, cuando el proceso anterior ya ha terminado.</summary>
    public static void CleanupLeftovers()
    {
        var dir = AppContext.BaseDirectory;
        if (!Directory.Exists(dir) || !HasLeftovers(dir)) return;
        Task.Run(async () =>
        {
            for (int i = 0; i < 10 && HasLeftovers(dir); i++)
            {
                try { foreach (var f in Directory.EnumerateFiles(dir, "*" + OldSuffix, SearchOption.AllDirectories)) TryDelete(f); } catch { }
                await Task.Delay(2000);
            }
        });
    }

    private static bool HasLeftovers(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*" + OldSuffix, SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
