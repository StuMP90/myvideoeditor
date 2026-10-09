using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.Core.Editing;

/// <summary>
/// Timeline operations, each one undoable. Clips on a track never overlap: placing or moving
/// a clip over others trims or removes what's underneath ("overwrite"), like most editors.
/// </summary>
public sealed class TimelineEditor
{
    /// <summary>How long stills, titles and annotations last when added.</summary>
    public const double DefaultStillDuration = 5;

    private readonly EditSession _session;

    internal TimelineEditor(EditSession session) => _session = session;

    private Project Project => _session.Project;

    /// <summary>One frame at the project's frame rate: the shortest a clip can be.</summary>
    public double MinDuration => 1.0 / Math.Max(1, Project.Settings.FrameRate);

    // ---- media -------------------------------------------------------------------------------

    /// <summary>Adds a file to the project's media (once per path) and returns its id.</summary>
    public string ImportMedia(MediaInfo info)
    {
        var existing = Project.Media.FirstOrDefault(m => string.Equals(m.Path, info.Path, PathComparison));
        if (existing != null)
            return existing.Id;
        var reference = MediaReference.FromInfo(info);
        _session.Apply($"Import {reference.Name}", p => p.Media.Add(reference));
        return reference.Id;
    }

    public void RemoveMedia(string mediaId)
    {
        var media = Project.FindMedia(mediaId) ?? throw new ArgumentException("No such media.");
        _session.Apply($"Remove {media.Name}", p =>
        {
            p.Media.RemoveAll(m => m.Id == mediaId);
            foreach (var track in p.Tracks)
                track.Clips.RemoveAll(c => c is MediaClip mc && mc.MediaId == mediaId);
        });
    }

    /// <summary>
    /// Puts a media file on the timeline at <paramref name="start"/>. Audio files go on an audio
    /// track, everything else on a video track (the given one if suitable). Returns the new clip's id.
    /// </summary>
    public string AddMediaClip(string mediaId, double start, string? trackId = null)
    {
        var media = Project.FindMedia(mediaId) ?? throw new ArgumentException("No such media.");
        var kind = media.HasVideo ? TrackKind.Video : TrackKind.Audio;
        var requested = trackId != null ? Project.FindTrack(trackId) : null;
        // A video file dropped on an audio track contributes just its sound.
        if (requested != null && requested.Kind == TrackKind.Audio && media.HasAudio)
            kind = TrackKind.Audio;
        var duration = media.Kind == MediaKind.Image ? DefaultStillDuration : Math.Max(MinDuration, media.Duration);
        var clip = new MediaClip
        {
            MediaId = mediaId,
            Name = Path.GetFileNameWithoutExtension(media.Name),
            Start = Math.Max(0, start),
            Duration = duration,
        };
        _session.Apply($"Add {media.Name}", p =>
        {
            var track = requested is { Locked: false } && requested.Kind == kind
                ? p.FindTrack(requested.Id)!
                : ChooseTrack(p, kind, clip.Start, clip.End, preferTop: false);
            Place(track, clip);
        });
        return clip.Id;
    }

    // ---- titles and annotations --------------------------------------------------------------

    public string AddTitle(double start, TitleClip? template = null, string? trackId = null)
    {
        var clip = template != null ? (TitleClip)template.CloneWithNewId() : new TitleClip();
        clip.Start = Math.Max(0, start);
        if (clip.Duration <= 0)
            clip.Duration = DefaultStillDuration;
        if (string.IsNullOrEmpty(clip.Name))
            clip.Name = FirstLine(clip.Text);
        AddOverlay(clip, "Add title", trackId);
        return clip.Id;
    }

    public string AddAnnotation(AnnotationClip clip, string? trackId = null)
    {
        clip.Start = Math.Max(0, clip.Start);
        if (clip.Duration <= 0)
            clip.Duration = DefaultStillDuration;
        if (string.IsNullOrEmpty(clip.Name))
            clip.Name = clip.Shape.ToString();
        AddOverlay(clip, $"Add {clip.Shape.ToString().ToLowerInvariant()}", trackId);
        return clip.Id;
    }

    private void AddOverlay(Clip clip, string description, string? trackId)
    {
        _session.Apply(description, p =>
        {
            var track = trackId != null && p.FindTrack(trackId) is { Kind: TrackKind.Video, Locked: false } t
                ? t
                : ChooseTrack(p, TrackKind.Video, clip.Start, clip.End, preferTop: true);
            Place(track, clip);
        });
    }

    // ---- moving and trimming -----------------------------------------------------------------

    /// <summary>Moves a clip to a new start time and (optionally) another track of a suitable kind.</summary>
    public void MoveClip(string clipId, double newStart, string? targetTrackId = null)
    {
        var (track, clip) = Find(clipId);
        var target = targetTrackId != null ? Project.FindTrack(targetTrackId) ?? track : track;
        if (track.Locked || target.Locked)
            return;
        if (!CanHold(target, clip))
            target = track;
        newStart = Math.Max(0, newStart);
        if (Math.Abs(newStart - clip.Start) < 1e-9 && target == track)
            return;
        _session.Apply("Move clip", p =>
        {
            var (from, c) = p.FindClip(clipId)!.Value;
            from.Clips.Remove(c);
            c.Start = newStart;
            Place(p.FindTrack(target.Id)!, c);
        });
    }

    /// <summary>Moves several clips by the same amount of time, each on its own track (one undo step).</summary>
    public void MoveClips(IReadOnlyCollection<string> clipIds, double delta)
    {
        var targets = clipIds.Select(id => Project.FindClip(id)).OfType<(Track Track, Clip Clip)>().Where(x => !x.Track.Locked).ToList();
        if (targets.Count == 0 || Math.Abs(delta) < 1e-9)
            return;
        // Never before zero: limit the move by the earliest clip.
        delta = Math.Max(delta, -targets.Min(x => x.Clip.Start));
        _session.Apply(targets.Count == 1 ? "Move clip" : "Move clips", p =>
        {
            var moving = targets.Select(x => p.FindClip(x.Clip.Id)!.Value).ToList();
            foreach (var (track, clip) in moving)
                track.Clips.Remove(clip);
            foreach (var (track, clip) in moving)
            {
                clip.Start += delta;
                Place(track, clip);
            }
        });
    }

    /// <summary>Whether a clip may go on a track (audio clips on audio tracks, the rest on video tracks).</summary>
    public bool CanHold(Track track, Clip clip)
    {
        if (track.Kind == TrackKind.Video)
            return clip is TitleClip or AnnotationClip || (clip is MediaClip mc && Project.FindMedia(mc.MediaId)?.HasVideo == true);
        return clip is MediaClip m && Project.FindMedia(m.MediaId)?.HasAudio == true;
    }

    /// <summary>The nearest allowed start for trimming a clip's beginning to <paramref name="newStart"/>.</summary>
    public double ClampTrimStart(string clipId, double newStart)
    {
        var (track, clip) = Find(clipId);
        var min = 0.0;
        var previous = track.Clips.Where(c => c != clip && c.End <= clip.Start + 1e-9).Select(c => c.End).DefaultIfEmpty(0).Max();
        min = Math.Max(min, previous);
        if (clip is MediaClip mc && HasSourceLimit(mc))
            min = Math.Max(min, clip.Start - mc.InPoint / mc.Speed);
        return Math.Clamp(newStart, min, clip.End - MinDuration);
    }

    /// <summary>The nearest allowed end for trimming a clip's end to <paramref name="newEnd"/>.</summary>
    public double ClampTrimEnd(string clipId, double newEnd)
    {
        var (track, clip) = Find(clipId);
        var max = double.MaxValue;
        var next = track.Clips.Where(c => c != clip && c.Start >= clip.End - 1e-9).Select(c => c.Start).DefaultIfEmpty(double.MaxValue).Min();
        max = Math.Min(max, next);
        if (clip is MediaClip mc && HasSourceLimit(mc))
        {
            var media = Project.FindMedia(mc.MediaId)!;
            max = Math.Min(max, clip.Start + (media.Duration - mc.InPoint) / mc.Speed);
        }
        return Math.Clamp(newEnd, clip.Start + MinDuration, Math.Max(clip.Start + MinDuration, max));
    }

    public void TrimStart(string clipId, double newStart)
    {
        if (Find(clipId).Track.Locked)
            return;
        newStart = ClampTrimStart(clipId, newStart);
        _session.Apply("Trim clip", p => TrimStartOf(p.FindClip(clipId)!.Value.Clip, newStart));
    }

    public void TrimEnd(string clipId, double newEnd)
    {
        if (Find(clipId).Track.Locked)
            return;
        newEnd = ClampTrimEnd(clipId, newEnd);
        _session.Apply("Trim clip", p =>
        {
            var c = p.FindClip(clipId)!.Value.Clip;
            c.Duration = newEnd - c.Start;
        });
    }

    // ---- split, delete, duplicate ------------------------------------------------------------

    /// <summary>
    /// Cuts clips in two at <paramref name="time"/>: the given clips, or, if none are given,
    /// every clip under the playhead on unlocked tracks. Returns how many clips were split.
    /// </summary>
    public int Split(double time, IReadOnlyCollection<string>? clipIds = null)
    {
        var targets = Project.AllClips()
            .Where(x => !x.Track.Locked && x.Clip.Start + MinDuration <= time && time <= x.Clip.End - MinDuration)
            .Where(x => clipIds == null || clipIds.Count == 0 || clipIds.Contains(x.Clip.Id))
            .Select(x => x.Clip.Id)
            .ToList();
        if (targets.Count == 0)
            return 0;
        _session.Apply(targets.Count == 1 ? "Split clip" : "Split clips", p =>
        {
            foreach (var id in targets)
            {
                var (track, clip) = p.FindClip(id)!.Value;
                var right = SplitOff(clip, time);
                track.Clips.Add(right);
                track.SortClips();
            }
        });
        return targets.Count;
    }

    public void Delete(IReadOnlyCollection<string> clipIds, bool ripple = false)
    {
        var targets = clipIds.Where(id => Project.FindClip(id) is { Track.Locked: false }).ToList();
        if (targets.Count == 0)
            return;
        _session.Apply(ripple ? "Ripple delete" : targets.Count == 1 ? "Delete clip" : "Delete clips", p =>
        {
            // Latest first, so each ripple shift only moves clips after it.
            foreach (var (track, clip) in targets.Select(id => p.FindClip(id)!.Value).OrderByDescending(x => x.Clip.Start).ToList())
            {
                track.Clips.Remove(clip);
                if (!ripple)
                    continue;
                foreach (var later in track.Clips.Where(c => c.Start >= clip.End - 1e-9))
                    later.Start -= clip.Duration;
            }
        });
    }

    /// <summary>Puts a copy straight after the clip (or at <paramref name="at"/>). Returns the copy's id.</summary>
    public string? Duplicate(string clipId, double? at = null)
    {
        var (track, clip) = Find(clipId);
        if (track.Locked)
            return null;
        var copy = clip.CloneWithNewId();
        copy.Start = at ?? clip.End;
        copy.TransitionIn = 0;
        _session.Apply("Duplicate clip", p => Place(p.FindTrack(track.Id)!, copy));
        return copy.Id;
    }

    /// <summary>
    /// Copies a video clip's sound to an audio track, so it can be moved and edited on its own,
    /// and mutes the original. Returns the new audio clip's id.
    /// </summary>
    public string? DetachAudio(string clipId)
    {
        var (track, clip) = Find(clipId);
        if (clip is not MediaClip mc || track.Kind != TrackKind.Video || mc.AudioMuted || Project.FindMedia(mc.MediaId)?.HasAudio != true)
            return null;
        var audio = (MediaClip)mc.CloneWithNewId();
        audio.Effects = mc.Effects.Where(e => Rendering.EffectCatalog.Find(e.Type)?.IsAudio == true).ToList();
        audio.Opacity = 1;
        _session.Apply("Detach audio", p =>
        {
            ((MediaClip)p.FindClip(clipId)!.Value.Clip).AudioMuted = true;
            var target = ChooseTrack(p, TrackKind.Audio, audio.Start, audio.End, preferTop: true);
            Place(target, audio);
        });
        return audio.Id;
    }

    /// <summary>Changes a clip's properties. Durations and fades are kept within sensible limits afterwards.</summary>
    /// <summary>The clip a dissolve on this one starts from: the one ending where it starts, on the same track; null if none.</summary>
    public Clip? DissolvePartner(string clipId) =>
        Project.FindClip(clipId) is var (track, clip)
            ? track.Clips.FirstOrDefault(c => c != clip && Math.Abs(c.End - clip.Start) <= 0.002)
            : null;

    /// <summary>
    /// Adds a dissolve from the clip ending where this one starts. If that clip is on another track (and
    /// this one's time there is free), this clip moves onto it first, as one undo step. Returns why not, or null.
    /// </summary>
    public string? AddDissolve(string clipId, double seconds)
    {
        var (track, clip) = Find(clipId);
        if (track.Locked)
            return "This track is locked.";
        Track? target = null;
        if (DissolvePartner(clipId) == null)
        {
            target = Project.Tracks.FirstOrDefault(t => t != track && t.Kind == track.Kind && !t.Locked && CanHold(t, clip)
                && t.Clips.Any(c => Math.Abs(c.End - clip.Start) <= 0.002)
                && !t.Clips.Any(c => c.Start < clip.End - 1e-6 && c.End > clip.Start + 1e-6));
            if (target == null)
                return "A dissolve joins two clips that touch: move this clip right up against the end of the one before it.";
        }
        _session.Apply("Add dissolve", p =>
        {
            var (from, c) = p.FindClip(clipId)!.Value;
            if (target != null)
            {
                from.Clips.Remove(c);
                Place(p.FindTrack(target.Id)!, c);
            }
            c.TransitionIn = seconds;
            Normalise(p, p.FindClip(clipId)!.Value.Track, c);
        });
        return null;
    }

    public void UpdateClip(string clipId, string description, Action<Clip> change, string? mergeKey = null)
    {
        _session.Apply(description, p =>
        {
            var (track, clip) = p.FindClip(clipId) ?? throw new ArgumentException("No such clip.");
            change(clip);
            Normalise(p, track, clip);
        }, mergeKey);
    }

    // ---- tracks ------------------------------------------------------------------------------

    public string AddTrack(TrackKind kind)
    {
        var track = new Track { Kind = kind, Name = NextTrackName(Project, kind) };
        _session.Apply($"Add {kind.ToString().ToLowerInvariant()} track", p => InsertTrack(p, track, top: kind == TrackKind.Video));
        return track.Id;
    }

    public void RemoveTrack(string trackId)
    {
        var track = Project.FindTrack(trackId) ?? throw new ArgumentException("No such track.");
        _session.Apply($"Remove track {track.Name}", p => p.Tracks.RemoveAll(t => t.Id == trackId));
    }

    public void UpdateTrack(string trackId, string description, Action<Track> change, string? mergeKey = null) =>
        _session.Apply(description, p => change(p.FindTrack(trackId) ?? throw new ArgumentException("No such track.")), mergeKey);

    public void UpdateSettings(ProjectSettings settings) =>
        _session.Apply("Project settings", p => p.Settings = settings.Clone());

    /// <summary>Clip edges and zero, for snapping while dragging.</summary>
    public List<double> SnapPoints(IReadOnlyCollection<string>? excludeClipIds = null)
    {
        var points = new List<double> { 0 };
        foreach (var (_, clip) in Project.AllClips())
        {
            if (excludeClipIds?.Contains(clip.Id) == true)
                continue;
            points.Add(clip.Start);
            points.Add(clip.End);
        }
        return points;
    }

    // ---- internals (operate on the project passed to Apply) ----------------------------------

    private (Track Track, Clip Clip) Find(string clipId) =>
        Project.FindClip(clipId) ?? throw new ArgumentException("No such clip.");

    private bool HasSourceLimit(MediaClip clip) => Project.FindMedia(clip.MediaId) is { Kind: not MediaKind.Image };

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Adds a clip to a track, overwriting whatever was under it.</summary>
    internal static void Place(Track track, Clip clip)
    {
        Overwrite(track, clip.Start, clip.End, clip.Id);
        track.Clips.Add(clip);
        track.SortClips();
    }

    /// <summary>Clears [start, end) on a track: clips there are trimmed, split or removed.</summary>
    internal static void Overwrite(Track track, double start, double end, string? exceptClipId)
    {
        const double eps = 1e-9;
        foreach (var clip in track.Clips.ToList())
        {
            if (clip.Id == exceptClipId || clip.End <= start + eps || clip.Start >= end - eps)
                continue;
            var coversLeft = clip.Start < start - eps;
            var coversRight = clip.End > end + eps;
            if (coversLeft && coversRight)
            {
                var right = SplitOff(clip, end);
                right.TransitionIn = 0;
                clip.Duration = start - clip.Start;
                track.Clips.Add(right);
            }
            else if (coversLeft)
            {
                clip.Duration = start - clip.Start;
            }
            else if (coversRight)
            {
                TrimStartOf(clip, end);
                clip.TransitionIn = 0;
            }
            else
            {
                track.Clips.Remove(clip);
            }
        }
        track.SortClips();
    }

    /// <summary>Shortens <paramref name="clip"/> to end at <paramref name="time"/> and returns the rest as a new clip.</summary>
    internal static Clip SplitOff(Clip clip, double time)
    {
        var right = clip.CloneWithNewId();
        clip.Duration = time - clip.Start;
        clip.FadeOut = 0;
        TrimStartOf(right, time);
        right.FadeIn = 0;
        right.TransitionIn = 0;
        return right;
    }

    internal static void TrimStartOf(Clip clip, double newStart)
    {
        var delta = newStart - clip.Start;
        clip.Start = newStart;
        clip.Duration -= delta;
        if (clip is MediaClip mc)
            mc.InPoint = Math.Max(0, mc.InPoint + delta * mc.Speed);
    }

    private void Normalise(Project p, Track track, Clip clip)
    {
        clip.Duration = Math.Max(MinDuration, clip.Duration);
        clip.Start = Math.Max(0, clip.Start);
        clip.Opacity = Math.Clamp(clip.Opacity, 0, 1);
        clip.FadeIn = Math.Clamp(clip.FadeIn, 0, clip.Duration);
        clip.FadeOut = Math.Clamp(clip.FadeOut, 0, clip.Duration - clip.FadeIn);
        clip.TransitionIn = Math.Clamp(clip.TransitionIn, 0, clip.Duration / 2);
        if (clip is MediaClip mc)
        {
            mc.Speed = Math.Clamp(mc.Speed, 0.1, 16);
            mc.Volume = Math.Clamp(mc.Volume, 0, 4);
            mc.Scale = Math.Clamp(mc.Scale, 0.05, 20);
            if (p.FindMedia(mc.MediaId) is { Kind: not MediaKind.Image } media && media.Duration > 0)
            {
                // A faster/slower speed changes how much source the clip covers; keep it inside the file.
                mc.InPoint = Math.Clamp(mc.InPoint, 0, Math.Max(0, media.Duration - MinDuration));
                var maxDuration = (media.Duration - mc.InPoint) / mc.Speed;
                clip.Duration = Math.Min(clip.Duration, Math.Max(MinDuration, maxDuration));
            }
        }
        // Growing a clip must not run into the next one.
        Overwrite(track, clip.Start, clip.End, clip.Id);
    }

    /// <summary>
    /// A track of the given kind with nothing in [start, end); prefers the top (overlays) or
    /// the bottom (main footage) track; adds a new track if all are busy.
    /// </summary>
    private static Track ChooseTrack(Project p, TrackKind kind, double start, double end, bool preferTop)
    {
        var candidates = p.Tracks.Where(t => t.Kind == kind && !t.Locked).ToList();
        if (!preferTop)
            candidates.Reverse();
        var free = candidates.FirstOrDefault(t => !t.Clips.Any(c => c.Start < end - 1e-9 && c.End > start + 1e-9));
        if (free != null)
            return free;
        var track = new Track { Kind = kind, Name = NextTrackName(p, kind) };
        InsertTrack(p, track, top: kind == TrackKind.Video ? preferTop : !preferTop);
        return track;
    }

    private static void InsertTrack(Project p, Track track, bool top)
    {
        var sameKind = p.Tracks.Select((t, i) => (t, i)).Where(x => x.t.Kind == track.Kind).Select(x => x.i).ToList();
        if (sameKind.Count == 0)
        {
            // Video tracks above audio tracks.
            if (track.Kind == TrackKind.Video)
                p.Tracks.Insert(0, track);
            else
                p.Tracks.Add(track);
            return;
        }
        p.Tracks.Insert(top ? sameKind[0] : sameKind[^1] + 1, track);
    }

    private static string NextTrackName(Project p, TrackKind kind)
    {
        var prefix = kind == TrackKind.Video ? "V" : "A";
        for (var n = 1; ; n++)
            if (p.Tracks.All(t => t.Name != prefix + n))
                return prefix + n;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        return line.Length > 40 ? line[..40] + "…" : line;
    }
}
