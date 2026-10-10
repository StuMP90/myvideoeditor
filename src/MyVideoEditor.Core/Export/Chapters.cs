using System.Globalization;
using System.Text;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.Core.Export;

/// <summary>Where an export's chapters come from.</summary>
public enum ChapterSource
{
    None,
    /// <summary>Titles marked "starts a chapter" (the Chapter template is), named after their text.</summary>
    Titles,
    /// <summary>Each clip's start on the main video track (the lowest one with clips).</summary>
    Cuts,
    /// <summary>Every few minutes.</summary>
    Interval,
}

/// <summary>A chapter: when it starts (seconds from the start of the export) and its name.</summary>
public sealed record Chapter(double Start, string Title);

public static class Chapters
{
    /// <summary>DVD-Video's limit per title.</summary>
    public const int Maximum = 99;

    /// <summary>
    /// The chapters for an export of [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>): always
    /// one at the start, then the source's, at least a second apart, at most <see cref="Maximum"/>.
    /// Empty if there'd only be the one.
    /// </summary>
    public static IReadOnlyList<Chapter> For(Project project, ChapterSource source, double rangeStart, double rangeEnd, double intervalMinutes = 5)
    {
        var points = new List<(double Time, string Title)>();
        switch (source)
        {
            case ChapterSource.Titles:
                points.AddRange(project.Tracks.SelectMany(t => t.Clips).OfType<TitleClip>().Where(t => t.StartsChapter)
                    .Select(t => (t.Start, FirstLine(t.Text))));
                break;
            case ChapterSource.Cuts:
                var main = project.Tracks.LastOrDefault(t => t.Kind == TrackKind.Video && t.Clips.Count > 0);
                if (main != null)
                    points.AddRange(main.Clips.Select(c => (c.Start, "")));
                break;
            case ChapterSource.Interval:
                var step = Math.Max(1, intervalMinutes) * 60;
                for (var t = step; t < rangeEnd; t += step)
                    points.Add((t, ""));
                break;
            default:
                return [];
        }
        var chapters = new List<Chapter> { new(0, "") };
        foreach (var (time, title) in points.OrderBy(p => p.Time))
        {
            var at = time - rangeStart;
            if (at <= 0.5)
            {
                // At (or before) the start: names the first chapter.
                if (title.Length > 0 && chapters[0].Title.Length == 0)
                    chapters[0] = chapters[0] with { Title = title };
                continue;
            }
            if (time >= rangeEnd - 1 || at - chapters[^1].Start < 1)
                continue;
            chapters.Add(new Chapter(at, title));
            if (chapters.Count == Maximum)
                break;
        }
        // Unnamed chapters are numbered.
        for (var i = 0; i < chapters.Count; i++)
            if (chapters[i].Title.Length == 0)
                chapters[i] = chapters[i] with { Title = $"Chapter {i + 1}" };
        return chapters.Count > 1 ? chapters : [];
    }

    private static string FirstLine(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    /// <summary>ffmpeg's metadata file with the chapters (read with -map_chapters).</summary>
    public static string Metadata(IReadOnlyList<Chapter> chapters, double duration)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", "\\\n");
        var text = new StringBuilder(";FFMETADATA1\n");
        for (var i = 0; i < chapters.Count; i++)
        {
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : duration;
            text.Append(CultureInfo.InvariantCulture, $"[CHAPTER]\nTIMEBASE=1/1000\nSTART={(long)Math.Round(chapters[i].Start * 1000)}\nEND={(long)Math.Round(end * 1000)}\ntitle={Escape(chapters[i].Title)}\n");
        }
        return text.ToString();
    }

    /// <summary>The chapter times as dvdauthor's -c list (h:mm:ss.sss).</summary>
    public static string DvdAuthorList(IReadOnlyList<Chapter> chapters) =>
        string.Join(',', chapters.Select(c => TimeSpan.FromSeconds(c.Start).ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture)));

    /// <summary>ffmpeg's -force_key_frames list: a keyframe at each chapter, so it starts exactly there.</summary>
    public static string KeyFrames(IReadOnlyList<Chapter> chapters) =>
        string.Join(',', chapters.Select(c => c.Start.ToString("0.###", CultureInfo.InvariantCulture)));
}
