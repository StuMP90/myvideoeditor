using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>One download for a platform: where from, how big, and its SHA-256.</summary>
public sealed record ComponentDownload(string Url, long Size, string Sha256);

/// <summary>
/// An optional upscaler part downloaded on first use: a GPU shader or an AI upscaling program
/// with its models. All are free and run entirely on this computer. Downloads are pinned to
/// an exact file and checksum, so what's installed is always what was tested.
/// </summary>
public sealed record UpscalerComponent
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Licence { get; init; }
    public required string Homepage { get; init; }
    /// <summary>Per runtime ("linux-x64", "win-x64"…, "any" for platform-independent files).</summary>
    public required IReadOnlyDictionary<string, ComponentDownload> Downloads { get; init; }

    public ComponentDownload? ForThisComputer =>
        Downloads.TryGetValue(UpscalerComponents.RuntimeId, out var d) ? d
        : Downloads.TryGetValue("any", out var any) ? any
        // Windows on Arm runs the x64 programs under emulation.
        : UpscalerComponents.RuntimeId == "win-arm64" && Downloads.TryGetValue("win-x64", out var x64) ? x64
        : null;

    public bool IsSupportedHere => ForThisComputer != null;
}

/// <summary>Installs and finds <see cref="UpscalerComponent"/>s (in the data folder, under "tools").</summary>
public static class UpscalerComponents
{
    public const string DirEnvironmentVariable = "MYVIDEOEDITOR_TOOLS_DIR";

    public static IReadOnlyList<UpscalerComponent> All { get; } = Load();

    public static UpscalerComponent Anime4K => Get("anime4k");
    public static UpscalerComponent RealEsrgan => Get("realesrgan");
    public static UpscalerComponent RealCugan => Get("realcugan");

    private static UpscalerComponent Get(string id) => All.First(c => c.Id == id);

    /// <summary>The pinned downloads, from components.json.</summary>
    private static List<UpscalerComponent> Load()
    {
        using var stream = typeof(UpscalerComponents).Assembly.GetManifestResourceStream("components.json")
                           ?? throw new InvalidOperationException("components.json is missing from the build.");
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("components").EnumerateArray().Select(c => new UpscalerComponent
        {
            Id = c.GetProperty("id").GetString()!,
            Label = c.GetProperty("label").GetString()!,
            Licence = c.GetProperty("licence").GetString()!,
            Homepage = c.GetProperty("homepage").GetString()!,
            Downloads = c.GetProperty("downloads").EnumerateObject().ToDictionary(
                d => d.Name,
                d => new ComponentDownload(d.Value.GetProperty("url").GetString()!, d.Value.GetProperty("size").GetInt64(), d.Value.GetProperty("sha256").GetString()!)),
        }).ToList();
    }


    public static string RuntimeId =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public static string Directory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(DirEnvironmentVariable);
            return string.IsNullOrWhiteSpace(env) ? Path.Combine(AppPaths.DataDirectory, "tools") : env;
        }
    }

    /// <summary>Where downloads are installed (one folder per component).</summary>
    public static string InstallDirectory(UpscalerComponent component) => Path.Combine(Directory, component.Id);

    /// <summary>Installed, from the file this version of the app expects.</summary>
    public static bool IsInstalled(UpscalerComponent component) => Locate(component, Directory) != null;

    /// <summary>The folder holding a usable copy, or null.</summary>
    internal static string? Locate(UpscalerComponent component, string toolsRoot) =>
        Path.Combine(toolsRoot, component.Id) is var dir && IsValid(dir, component) ? dir : null;

    /// <summary>The folder has the ".installed" marker naming the expected download's checksum.</summary>
    private static bool IsValid(string folder, UpscalerComponent component)
    {
        var download = component.ForThisComputer;
        var marker = Path.Combine(folder, ".installed");
        try
        {
            return download != null && File.Exists(marker) && File.ReadAllText(marker).Trim() == download.Sha256;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? Folder(UpscalerComponent component) => Locate(component, Directory);

    /// <summary>The installed shader file (the Anime4K chain), or null.</summary>
    public static string? ShaderPath(UpscalerComponent component) =>
        Folder(component) is { } dir ? Path.Combine(dir, "shader.glsl") : null;

    /// <summary>The installed program for an AI component, or null.</summary>
    public static string? ProgramPath(UpscalerComponent component)
    {
        if (Folder(component) is not { } dir)
            return null;
        var exe = $"{component.Id}-ncnn-vulkan{(OperatingSystem.IsWindows() ? ".exe" : "")}";
        return System.IO.Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// Downloads, verifies and installs a component (replacing any older copy). The download is
    /// checked against its SHA-256 before anything is unpacked; on any failure nothing is left
    /// half-installed. Progress is 0..1.
    /// </summary>
    public static async Task InstallAsync(UpscalerComponent component, IProgress<double>? progress = null, CancellationToken cancellationToken = default,
        HttpMessageHandler? handler = null)
    {
        var download = component.ForThisComputer ?? throw new PlatformNotSupportedException($"{component.Label} isn't available for {RuntimeId}.");
        var target = InstallDirectory(component);
        var staging = target + ".installing-" + Guid.NewGuid().ToString("N")[..6];
        var file = staging + ".download";
        System.IO.Directory.CreateDirectory(Directory);
        try
        {
            Log.Info("tools", $"Downloading {component.Label} from {download.Url}.");
            await DownloadAsync(download, file, progress, cancellationToken, handler).ConfigureAwait(false);

            string hash;
            await using (var stream = File.OpenRead(file))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (hash != download.Sha256)
                throw new InvalidDataException($"The {component.Label} download doesn't match its expected checksum, so it wasn't installed. Try again later.");

            System.IO.Directory.CreateDirectory(staging);
            if (download.Url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(file, staging, overwriteFiles: true);
            else
                File.Copy(file, Path.Combine(staging, Path.GetFileName(new Uri(download.Url).AbsolutePath)));

            PrepareInstalled(component, staging);
            File.WriteAllText(Path.Combine(staging, ".installed"), download.Sha256);

            if (System.IO.Directory.Exists(target))
                System.IO.Directory.Delete(target, recursive: true);
            System.IO.Directory.Move(staging, target);
            Log.Info("tools", $"Installed {component.Label} in {target}.");
        }
        finally
        {
            TryDelete(file);
            if (System.IO.Directory.Exists(staging))
                try { System.IO.Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Removes a downloaded copy.</summary>
    public static void Uninstall(UpscalerComponent component)
    {
        var dir = InstallDirectory(component);
        if (System.IO.Directory.Exists(dir))
            System.IO.Directory.Delete(dir, recursive: true);
    }

    /// <summary>Per-component finishing touches: the shader file name, executable permission.</summary>
    private static void PrepareInstalled(UpscalerComponent component, string dir)
    {
        if (component == Anime4K)
        {
            // "Mode A" (fast, for 1080p and below): clamp highlights, restore lines, then a 2x CNN upscale.
            string Find(string name) => System.IO.Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).First();
            var chain = new[] { "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_M.glsl", "Anime4K_Upscale_CNN_x2_M.glsl" }
                .Select(n => File.ReadAllText(Find(n)));
            File.WriteAllText(Path.Combine(dir, "shader.glsl"), string.Join("\n\n", chain));
        }
        else if (!OperatingSystem.IsWindows())
        {
            foreach (var exe in System.IO.Directory.EnumerateFiles(dir, "*-ncnn-vulkan", SearchOption.AllDirectories))
                File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static async Task DownloadAsync(ComponentDownload download, string file, IProgress<double>? progress, CancellationToken cancellationToken, HttpMessageHandler? handler)
    {
        // file:// sources let tests (and offline installs) use a local copy.
        if (download.Url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(new Uri(download.Url).LocalPath, file, overwrite: true);
            progress?.Report(1);
            return;
        }
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(30);
        using var response = await http.GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? download.Size;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(file);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            progress?.Report(total > 0 ? Math.Min(1, (double)done / total) : 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
