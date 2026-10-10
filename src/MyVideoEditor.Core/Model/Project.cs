using System.Text.Json.Serialization;

namespace MyVideoEditor.Core.Model;

/// <summary>
/// An editing project: the media it uses, and tracks of clips on a timeline. Saved as JSON
/// (<see cref="ProjectSerializer"/>). Times are seconds. Track order is the order shown in the
/// timeline, top first: for video, earlier tracks are drawn in front of later ones.
/// </summary>
public sealed class Project
{
    public const int CurrentFormatVersion = 1;
    public const string FileExtension = ".mveproj";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = "Untitled";
    public ProjectSettings Settings { get; set; } = new();
    public List<MediaReference> Media { get; set; } = [];
    public List<Track> Tracks { get; set; } = [];

    /// <summary>End of the last clip on any track (seconds).</summary>
    [JsonIgnore]
    public double Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    public MediaReference? FindMedia(string id) => Media.FirstOrDefault(m => m.Id == id);

    public Track? FindTrack(string id) => Tracks.FirstOrDefault(t => t.Id == id);

    public (Track Track, Clip Clip)? FindClip(string clipId)
    {
        foreach (var track in Tracks)
            foreach (var clip in track.Clips)
                if (clip.Id == clipId)
                    return (track, clip);
        return null;
    }

    public IEnumerable<(Track Track, Clip Clip)> AllClips() =>
        Tracks.SelectMany(t => t.Clips.Select(c => (t, c)));

    /// <summary>A new project with one video and one audio track.</summary>
    public static Project CreateDefault(string name = "Untitled", ProjectSettings? settings = null) => new()
    {
        Name = name,
        Settings = settings ?? new ProjectSettings(),
        Tracks =
        [
            new Track { Kind = TrackKind.Video, Name = "V2" },
            new Track { Kind = TrackKind.Video, Name = "V1" },
            new Track { Kind = TrackKind.Audio, Name = "A1" },
        ],
    };
}

public sealed class ProjectSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30;
    public int SampleRate { get; set; } = 48000;
    /// <summary>Canvas colour where no clip covers it, "#RRGGBB".</summary>
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>
    /// For video stored with non-square pixels (DVDs): the width exports are stored at, with the pixels
    /// marked to be shown wider or narrower, so players show <see cref="Width"/>×<see cref="Height"/>.
    /// 0 = square pixels. <see cref="Width"/>×<see cref="Height"/> stays the picture as seen, which
    /// editing, titles and previews use.
    /// </summary>
    public int OutputWidth { get; set; }

    [JsonIgnore]
    public bool HasNonSquarePixels => OutputWidth > 0 && OutputWidth != Width;

    public ProjectSettings Clone() => (ProjectSettings)MemberwiseClone();

    public static readonly IReadOnlyList<SizePreset> CommonSizes =
    [
        new("4K UHD (3840×2160)", 3840, 2160),
        new("1440p (2560×1440)", 2560, 1440),
        new("1080p (1920×1080)", 1920, 1080),
        new("720p (1280×720)", 1280, 720),
        new("480p (854×480)", 854, 480),
        // DVDs store 720 pixels across, shown narrower (4:3) or wider (16:9).
        new("DVD NTSC 4:3 (720×480)", 640, 480, 720, 29.97),
        new("DVD NTSC 16:9 widescreen (720×480)", 854, 480, 720, 29.97),
        new("DVD PAL 4:3 (720×576)", 768, 576, 720, 25),
        new("DVD PAL 16:9 widescreen (720×576)", 1024, 576, 720, 25),
        new("Vertical 1080×1920 (Shorts, Reels, TikTok)", 1080, 1920),
        new("Square 1080×1080", 1080, 1080),
        new("Portrait 1080×1350", 1080, 1350),
    ];
}

/// <summary>
/// A project size: the picture as seen (<paramref name="Width"/>×<paramref name="Height"/>), the width
/// it's stored at when its pixels aren't square (DVDs), and the frame rate that goes with it.
/// </summary>
public sealed record SizePreset(string Name, int Width, int Height, int OutputWidth = 0, double? FrameRate = null);

/// <summary>A media file the project uses, with what was known about it when it was imported.</summary>
public sealed class MediaReference
{
    public string Id { get; set; } = Ids.New();
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public Media.MediaKind Kind { get; set; }
    public double Duration { get; set; }
    public bool HasVideo { get; set; }
    /// <summary>Where the picture ends (seconds); 0 if unknown (it can end before the sound).</summary>
    public double VideoEnd { get; set; }
    /// <summary>The width the file stores its pictures at, if its pixels aren't square (DVDs: 720); 0 otherwise.</summary>
    public int StoredWidth { get; set; }
    public bool HasAudio { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }

    public static MediaReference FromInfo(Media.MediaInfo info) => new()
    {
        Path = info.Path,
        Name = System.IO.Path.GetFileName(info.Path),
        Kind = info.Kind,
        Duration = info.Duration,
        HasVideo = info.HasVideo,
        StoredWidth = info.StoredWidth,
        VideoEnd = info.VideoEnd,
        HasAudio = info.HasAudio,
        Width = info.Width,
        Height = info.Height,
        FrameRate = info.FrameRate,
    };

    [JsonIgnore]
    public bool IsMissing => !File.Exists(Path);
}

public enum TrackKind
{
    Video,
    Audio,
}

public sealed class Track
{
    public string Id { get; set; } = Ids.New();
    public TrackKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Audio is not heard (audio tracks, and the sound of clips on video tracks).</summary>
    public bool Muted { get; set; }
    /// <summary>Video is not shown.</summary>
    public bool Hidden { get; set; }
    /// <summary>Clips can't be moved, trimmed or deleted.</summary>
    public bool Locked { get; set; }
    public double Volume { get; set; } = 1.0;
    /// <summary>Clips in timeline order. They never overlap (see <see cref="Editing.TimelineEditor"/>).</summary>
    public List<Clip> Clips { get; set; } = [];

    public void SortClips() => Clips.Sort((a, b) => a.Start.CompareTo(b.Start));
}

internal static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
