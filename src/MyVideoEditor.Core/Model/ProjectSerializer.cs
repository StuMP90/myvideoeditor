using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyVideoEditor.Core.Model;

/// <summary>Reads and writes project files (JSON). Saves are atomic and keep the previous file as .bak.</summary>
public static class ProjectSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Older/newer files may carry fields this version doesn't know: ignore them.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json)
    {
        var project = JsonSerializer.Deserialize<Project>(json, Options) ?? throw new InvalidDataException("The project file is empty.");
        if (project.FormatVersion > Project.CurrentFormatVersion)
            throw new InvalidDataException("This project was saved by a newer version of MyVideoEditor. Update the app to open it.");
        project.Settings ??= new ProjectSettings();
        project.Media ??= [];
        project.Tracks ??= [];
        foreach (var track in project.Tracks)
        {
            track.Clips ??= [];
            foreach (var clip in track.Clips)
                clip.Effects ??= [];
            track.SortClips();
        }
        return project;
    }

    public static Project Load(string path)
    {
        var project = Deserialize(File.ReadAllText(path));
        ResolveRelativePaths(project, Path.GetDirectoryName(Path.GetFullPath(path))!);
        return project;
    }

    /// <summary>
    /// Saves atomically (write to a temp file, then replace). Media paths are stored as given;
    /// <see cref="ResolveRelativePaths"/> finds media moved together with the project.
    /// </summary>
    public static void Save(Project project, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, Serialize(project));
        if (File.Exists(full))
            File.Replace(temp, full, full + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(temp, full);
    }

    /// <summary>
    /// If a media file or title picture isn't where it was, looks for it next to the project file (a
    /// project folder copied to another computer or drive).
    /// </summary>
    internal static void ResolveRelativePaths(Project project, string projectDirectory)
    {
        string Find(string path)
        {
            if (string.IsNullOrEmpty(path) || File.Exists(path))
                return path;
            var candidate = Path.Combine(projectDirectory, Path.GetFileName(path));
            return File.Exists(candidate) ? candidate : path;
        }
        foreach (var media in project.Media)
            media.Path = Find(media.Path);
        foreach (var title in project.Tracks.SelectMany(t => t.Clips).OfType<TitleClip>())
            title.ImagePath = Find(title.ImagePath);
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
