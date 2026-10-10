using System.Diagnostics;
using System.Globalization;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Export;

/// <summary>
/// What a DVD-Video export becomes: the standard (NTSC or PAL, by the project's frame rate) and
/// shape (4:3 or 16:9, by the project's), with DVD's sizes. The picture is drawn at its shown size,
/// fitted inside it with bars if the project is another shape, then stored 720 across with DVD's
/// pixel shape.
/// </summary>
public sealed record DvdVideo(bool Pal, bool Wide)
{
    public int Height => Pal ? 576 : 480;
    public const int StoredWidth = 720;
    /// <summary>The width the picture is shown at (720 × the pixel shape).</summary>
    public int ShownWidth => (Pal, Wide) switch { (false, false) => 640, (false, true) => 854, (true, false) => 768, _ => 1024 };
    /// <summary>The pixel shape (sample aspect ratio) DVD uses for this standard and shape.</summary>
    public string PixelShape => (Pal, Wide) switch { (false, false) => "8/9", (false, true) => "32/27", (true, false) => "16/15", _ => "64/45" };
    public string Aspect => Wide ? "16:9" : "4:3";
    public string FrameRate => Pal ? "25" : "30000/1001";
    /// <summary>Most frames between keyframes the standard allows.</summary>
    public int Gop => Pal ? 15 : 18;
    public string Name => $"{(Pal ? "PAL" : "NTSC")} {StoredWidth}×{Height} {Aspect}";

    /// <summary>PAL for 25 and 50 fps projects, NTSC otherwise; 16:9 for wide projects, 4:3 otherwise.</summary>
    public static DvdVideo For(ProjectSettings s)
    {
        var fps = s.FrameRate;
        var pal = Math.Abs(fps - 25) < 0.5 || Math.Abs(fps - 50) < 0.5;
        var wide = (double)s.Width / Math.Max(1, s.Height) > 1.55;
        return new DvdVideo(pal, wide);
    }

    /// <summary>The size to draw the project at: fitted inside the shown frame, keeping its shape.</summary>
    public (int Width, int Height) RenderSize(ProjectSettings s)
    {
        var scale = Math.Min((double)ShownWidth / s.Width, (double)Height / s.Height);
        return (Math.Max(2, (int)Math.Round(s.Width * scale / 2) * 2), Math.Max(2, (int)Math.Round(s.Height * scale / 2) * 2));
    }

    /// <summary>The plan's picture fitted into DVD's frame (with bars if needed), at its frame rate, stored 720 across.</summary>
    internal RenderPlan Apply(RenderPlan plan)
    {
        var graph = plan.FilterGraph +
                    $";\n[{plan.VideoOutput}]fps={FrameRate},pad={ShownWidth}:{Height}:(ow-iw)/2:(oh-ih)/2:black," +
                    $"scale={StoredWidth}:{Height}:flags=lanczos,setsar={PixelShape},format=yuv420p[dvd]";
        return plan with { FilterGraph = graph, VideoOutput = "dvd" };
    }

    /// <summary>The standard's options for the encoder and file.</summary>
    internal IEnumerable<string> OutputArguments() => ["-r", FrameRate, "-g", Gop.ToString(CultureInfo.InvariantCulture), "-aspect", Aspect];

    // ---- making a DVD folder ----------------------------------------------------------------

    /// <summary>dvdauthor (free, GPL), which turns a DVD MPEG-2 file into a disc's VIDEO_TS folder; null if it isn't installed.</summary>
    public static string? AuthorProgram
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "dvdauthor.exe" : "dvdauthor";
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
        }
    }

    /// <summary>
    /// Makes a disc folder (VIDEO_TS, and an empty AUDIO_TS) from <paramref name="mpeg"/> with dvdauthor:
    /// one title that plays straight away. Burn the folder's contents as a DVD-Video disc.
    /// </summary>
    public async Task<string> MakeFolderAsync(string mpeg, string folder, CancellationToken cancellationToken = default)
    {
        var program = AuthorProgram ?? throw new InvalidOperationException("dvdauthor isn't installed.");
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        var video = $"{(Pal ? "pal" : "ntsc")}+{Aspect}";
        await Run(program, ["-o", folder, "-t", "-v", video, "-a", "ac3+en", mpeg], video, cancellationToken).ConfigureAwait(false);
        await Run(program, ["-o", folder, "-T"], video, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(folder, "AUDIO_TS"));
        return folder;
    }

    private async Task Run(string program, IEnumerable<string> args, string video, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["VIDEO_FORMAT"] = Pal ? "PAL" : "NTSC";
        using var p = ChildProcess.Start(psi);
        var errors = p.StandardError.ReadToEndAsync(cancellationToken);
        _ = p.StandardOutput.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var log = await errors.ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            Log.Warn("dvd", $"dvdauthor ({video}) failed: {log}");
            throw new InvalidOperationException("Making the DVD folder failed: " + log.Split('\n').LastOrDefault(l => l.Contains("ERR", StringComparison.Ordinal))?.Trim());
        }
    }
}
