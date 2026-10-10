using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.Core.Editing;
using MyVideoEditor.Core.Graphics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;
using CoreTextAlignment = MyVideoEditor.Core.Model.TextAlignment;

namespace MyVideoEditor.App.ViewModels;

/// <summary>
/// Settings of the selected clip. Every setter is an undoable edit; repeated changes to one
/// setting (dragging a slider) undo as one step. After any change to the project (including
/// undo), <see cref="Refresh"/> reloads the values from the model.
/// </summary>
public sealed partial class ClipInspectorViewModel : ObservableObject
{
    private readonly EditSession _session;

    public ClipInspectorViewModel(EditSession session, string clipId)
    {
        _session = session;
        ClipId = clipId;
        RebuildEffects();
    }

    public string ClipId { get; }

    private Clip? Clip => _session.Project.FindClip(ClipId)?.Clip;
    private MediaClip? Media => Clip as MediaClip;
    private TitleClip? Title => Clip as TitleClip;
    private AnnotationClip? Annotation => Clip as AnnotationClip;
    private MediaReference? MediaFile => Media is { } m ? _session.Project.FindMedia(m.MediaId) : null;

    public bool Exists => Clip != null;
    public bool IsMedia => Clip is MediaClip;
    public bool IsTitle => Clip is TitleClip;
    public bool IsAnnotation => Clip is AnnotationClip;
    public bool IsVisual => Clip is not MediaClip || MediaFile?.HasVideo == true && OnVideoTrack;
    public bool HasSound => IsMedia && MediaFile?.HasAudio == true && MediaFile.Kind != MediaKind.Image;
    public bool CanChangeSpeed => IsMedia && MediaFile?.Kind != MediaKind.Image;
    public bool OnVideoTrack => _session.Project.FindClip(ClipId)?.Track.Kind == TrackKind.Video;
    public bool ShowsTransform => IsMedia && IsVisual;
    public bool AnnotationHasText => Annotation?.Shape is AnnotationShape.Callout or AnnotationShape.Step;
    public bool AnnotationHasStroke => Annotation?.Shape is AnnotationShape.Arrow or AnnotationShape.Rectangle or AnnotationShape.Ellipse;
    public bool AnnotationHasStrength => Annotation?.Shape is AnnotationShape.Blur or AnnotationShape.Pixelate or AnnotationShape.Spotlight or AnnotationShape.Highlight;
    public bool AnnotationHasColor => Annotation is { IsFilterShape: false, Shape: not AnnotationShape.Spotlight };

    public string Heading => Clip switch
    {
        MediaClip => MediaFile?.Kind switch { MediaKind.Audio => "Audio clip", MediaKind.Image => "Image", MediaKind.Animation => "Animation (Lottie)", _ => "Video clip" },
        TitleClip => "Title",
        AnnotationClip a => a.Shape switch
        {
            AnnotationShape.Blur => "Blur (redact)",
            AnnotationShape.Pixelate => "Pixelate (redact)",
            AnnotationShape.Zoom => "Zoom",
            AnnotationShape.Step => "Step number",
            _ => a.Shape.ToString(),
        },
        _ => "",
    };

    public string SourceText => MediaFile is { } m ? $"{m.Name}\n{m.Path}" : "";

    // ---- timing ------------------------------------------------------------------------------

    public string Name
    {
        get => Clip?.Name ?? "";
        set => Set("Rename clip", c => c.Name = value, "name");
    }

    public string StartText
    {
        get => Core.Timecode.Format(Clip?.Start ?? 0);
        set
        {
            if (Core.Timecode.TryParse(value, out var t, _session.Project.Settings.FrameRate))
                _session.Editor.MoveClip(ClipId, t);
            else
                OnPropertyChanged();
        }
    }

    public string DurationText
    {
        get => Core.Timecode.Format(Clip?.Duration ?? 0);
        set
        {
            if (Core.Timecode.TryParse(value, out var d, _session.Project.Settings.FrameRate) && Clip is { } c)
                _session.Editor.TrimEnd(ClipId, c.Start + d);
            else
                OnPropertyChanged();
        }
    }

    public double FadeIn
    {
        get => Clip?.FadeIn ?? 0;
        set => Set("Fade in", c => c.FadeIn = value, "fadein");
    }

    public double FadeOut
    {
        get => Clip?.FadeOut ?? 0;
        set => Set("Fade out", c => c.FadeOut = value, "fadeout");
    }

    public double TransitionIn
    {
        get => Clip?.TransitionIn ?? 0;
        set => Set("Dissolve", c => c.TransitionIn = value, "transition");
    }

    public static IReadOnlyList<string> TransitionStyles { get; } = Transitions.All.Select(t => t.Label).ToList();
    public static IReadOnlyList<string> TransitionDirections { get; } = ["Left", "Right", "Up", "Down"];

    public int TransitionStyleIndex
    {
        get => Math.Max(0, Transitions.All.ToList().FindIndex(t => t.Style == (Clip?.TransitionStyle ?? TransitionStyle.Dissolve)));
        set
        {
            if (value < 0 || value >= Transitions.All.Count)
                return;
            var style = Transitions.All[value].Style;
            // Choosing a style on a cut gives it a length, so the choice shows.
            Set("Transition", c =>
            {
                c.TransitionStyle = style;
                if (c.TransitionIn <= 0)
                    c.TransitionIn = Math.Min(1, c.Duration / 2);
            });
        }
    }

    public int TransitionDirectionIndex
    {
        get => (int)(Clip?.TransitionDirection ?? TransitionDirection.Left);
        set
        {
            if (value is >= 0 and <= 3)
                Set("Transition direction", c => c.TransitionDirection = (TransitionDirection)value);
        }
    }

    public bool TransitionHasDirection => Transitions.HasDirection(Clip?.TransitionStyle ?? TransitionStyle.Dissolve);

    /// <summary>Shown when a dissolve is set but there's no clip for it to start from (so it has no effect).</summary>
    public bool DissolveHasNoPartner => TransitionIn > 0 && _session.Editor.DissolvePartner(ClipId) == null;

    public double MaxFade => Math.Max(0.1, Math.Min(10, (Clip?.Duration ?? 1)));

    public double Opacity
    {
        get => (Clip?.Opacity ?? 1) * 100;
        set => Set("Opacity", c => c.Opacity = value / 100, "opacity");
    }

    // ---- media clips -------------------------------------------------------------------------

    public double Volume
    {
        get => (Media?.Volume ?? 1) * 100;
        set => Set("Volume", c => ((MediaClip)c).Volume = value / 100, "volume");
    }

    public bool AudioMuted
    {
        get => Media?.AudioMuted ?? false;
        set => Set(value ? "Mute clip" : "Unmute clip", c => ((MediaClip)c).AudioMuted = value);
    }

    public double Speed
    {
        get => Media?.Speed ?? 1;
        set => Set("Speed", c =>
        {
            // Keep the same stretch of footage: the clip gets shorter when faster.
            var m = (MediaClip)c;
            var source = m.Duration * m.Speed;
            m.Speed = Math.Clamp(value, 0.1, 16);
            m.Duration = source / m.Speed;
        }, "speed");
    }

    public static IReadOnlyList<FitMode> FitModes { get; } = Enum.GetValues<FitMode>();

    public FitMode Fit
    {
        get => Media?.Fit ?? FitMode.Fit;
        set => Set("Fit", c => ((MediaClip)c).Fit = value);
    }

    public double Scale
    {
        get => (Media?.Scale ?? 1) * 100;
        set => Set("Scale", c => ((MediaClip)c).Scale = value / 100, "scale");
    }

    public double OffsetX
    {
        get => Media?.OffsetX ?? 0;
        set => Set("Position", c => ((MediaClip)c).OffsetX = Math.Round(value), "offsetx");
    }

    public double OffsetY
    {
        get => Media?.OffsetY ?? 0;
        set => Set("Position", c => ((MediaClip)c).OffsetY = Math.Round(value), "offsety");
    }

    public double Rotation
    {
        get => Media?.Rotation ?? 0;
        set => Set("Rotation", c => ((MediaClip)c).Rotation = value, "rotation");
    }

    public int CropLeft { get => Media?.CropLeft ?? 0; set => Set("Crop", c => ((MediaClip)c).CropLeft = Math.Max(0, value), "cropl"); }
    public int CropRight { get => Media?.CropRight ?? 0; set => Set("Crop", c => ((MediaClip)c).CropRight = Math.Max(0, value), "cropr"); }
    public int CropTop { get => Media?.CropTop ?? 0; set => Set("Crop", c => ((MediaClip)c).CropTop = Math.Max(0, value), "cropt"); }
    public int CropBottom { get => Media?.CropBottom ?? 0; set => Set("Crop", c => ((MediaClip)c).CropBottom = Math.Max(0, value), "cropb"); }

    [RelayCommand]
    private void ResetTransform() => Set("Reset position", c =>
    {
        var m = (MediaClip)c;
        m.Scale = 1;
        m.OffsetX = m.OffsetY = m.Rotation = 0;
        m.CropLeft = m.CropRight = m.CropTop = m.CropBottom = 0;
        m.Fit = FitMode.Fit;
    });

    // ---- titles ------------------------------------------------------------------------------

    public string Text
    {
        get => Title?.Text ?? Annotation?.Text ?? "";
        set => Set("Text", c =>
        {
            if (c is TitleClip t)
            {
                t.Text = value;
                t.Name = value.Split('\n')[0];
            }
            else if (c is AnnotationClip a)
            {
                a.Text = value;
            }
        }, "text");
    }

    public static IReadOnlyList<string> Fonts => FontResolver.InstalledFamilies;

    public string FontFamily
    {
        get => string.IsNullOrEmpty(Title?.FontFamily) ? FontResolver.DefaultFamily : Title!.FontFamily;
        set => Set("Font", c => ((TitleClip)c).FontFamily = value ?? "");
    }

    public double FontSize
    {
        get => Title?.FontSize ?? Annotation?.FontSize ?? 48;
        set => Set("Text size", c =>
        {
            if (c is TitleClip t)
                t.FontSize = value;
            else if (c is AnnotationClip a)
                a.FontSize = value;
        }, "fontsize");
    }

    public bool Bold { get => Title?.Bold ?? false; set => Set("Bold", c => ((TitleClip)c).Bold = value); }
    public bool Italic { get => Title?.Italic ?? false; set => Set("Italic", c => ((TitleClip)c).Italic = value); }
    public bool Shadow { get => Title?.Shadow ?? false; set => Set("Shadow", c => ((TitleClip)c).Shadow = value); }

    public static IReadOnlyList<CoreTextAlignment> Alignments { get; } = Enum.GetValues<CoreTextAlignment>();

    public CoreTextAlignment Alignment
    {
        get => Title?.Alignment ?? CoreTextAlignment.Center;
        set => Set("Alignment", c => ((TitleClip)c).Alignment = value);
    }

    public double OutlineWidth
    {
        get => Title?.OutlineWidth ?? 0;
        set => Set("Outline", c => ((TitleClip)c).OutlineWidth = value, "outline");
    }

    public Color TextColor
    {
        get => ParseColor(Title?.Color ?? Annotation?.Color, Colors.White);
        set => Set("Colour", c =>
        {
            if (c is TitleClip t)
                t.Color = Hex(value);
            else if (c is AnnotationClip a)
                a.Color = Hex(value);
        }, "color");
    }

    public Color OutlineColor
    {
        get => ParseColor(Title?.OutlineColor, Colors.Black);
        set => Set("Outline colour", c => ((TitleClip)c).OutlineColor = Hex(value), "outlinecolor");
    }

    public bool HasBox
    {
        get => !string.IsNullOrEmpty(Title?.BoxColor);
        set => Set(value ? "Add text box" : "Remove text box", c => ((TitleClip)c).BoxColor = value ? "#000000" : "");
    }

    public Color BoxColor
    {
        get => ParseColor(Title?.BoxColor, Colors.Black);
        set => Set("Box colour", c => ((TitleClip)c).BoxColor = Hex(value), "boxcolor");
    }

    public double BoxOpacity
    {
        get => (Title?.BoxOpacity ?? 0.6) * 100;
        set => Set("Box opacity", c => ((TitleClip)c).BoxOpacity = value / 100, "boxopacity");
    }

    public bool HasBackground
    {
        get => !string.IsNullOrEmpty(Title?.BackgroundColor);
        set => Set(value ? "Add background" : "Remove background", c => ((TitleClip)c).BackgroundColor = value ? "#101418" : "");
    }

    public Color BackgroundColor
    {
        get => ParseColor(Title?.BackgroundColor, Colors.Black);
        set => Set("Background colour", c => ((TitleClip)c).BackgroundColor = Hex(value), "bgcolor");
    }

    // ---- title picture --------------------------------------------------------------------

    /// <summary>Asks for a picture file (set by the editor, which owns the dialogs).</summary>
    public Func<Task<string?>>? PickImage { get; set; }

    public bool HasImage => Title is { ImagePath.Length: > 0 };
    public string ImageName => Title is { ImagePath.Length: > 0 } t ? Path.GetFileName(t.ImagePath) : "No picture";
    public bool ImageMissing => Title is { ImagePath.Length: > 0 } t && !File.Exists(t.ImagePath);

    [RelayCommand]
    private async Task ChooseImage()
    {
        if (PickImage != null && await PickImage() is { } path)
            Set("Title picture", c => ((TitleClip)c).ImagePath = path);
    }

    [RelayCommand]
    private void RemoveImage() => Set("Remove picture", c => ((TitleClip)c).ImagePath = "");

    public static IReadOnlyList<string> ImagePlacements { get; } =
        ["Above the text", "Below the text", "Left of the text", "Right of the text", "Background (fills the frame)"];

    public int ImagePlacementIndex
    {
        get => (int)(Title?.ImagePlacement ?? TitleImagePlacement.Above);
        set
        {
            if (value >= 0 && value < ImagePlacements.Count)
                Set("Picture placement", c => ((TitleClip)c).ImagePlacement = (TitleImagePlacement)value);
        }
    }

    public bool ImageHasSize => HasImage && Title!.ImagePlacement != TitleImagePlacement.Background;

    public double ImageSize
    {
        get => Title?.ImageSize ?? 240;
        set => Set("Picture size", c => ((TitleClip)c).ImageSize = value, "imagesize");
    }

    // ---- title animation ----------------------------------------------------------------------

    /// <summary>Names for <see cref="TitleMotion"/>, in its order: coming in, and going out.</summary>
    public static IReadOnlyList<string> MotionsIn { get; } =
    [
        "None", "Slide in from the left", "Slide in from the right", "Slide in from the top", "Slide in from the bottom",
        "Rise and fade in", "Grow", "Wipe in", "Typewriter", "Word by word", "Line by line",
    ];

    public static IReadOnlyList<string> MotionsOut { get; } =
    [
        "None", "Slide out to the left", "Slide out to the right", "Slide out to the top", "Slide out to the bottom",
        "Sink and fade out", "Shrink", "Wipe out", "Delete letters", "Word by word", "Line by line",
    ];

    public int AnimationInIndex
    {
        get => (int)(Title?.AnimationIn ?? TitleMotion.None);
        set
        {
            if (value >= 0 && value < MotionsIn.Count)
                Set("Animation in", c => ((TitleClip)c).AnimationIn = (TitleMotion)value);
        }
    }

    public int AnimationOutIndex
    {
        get => (int)(Title?.AnimationOut ?? TitleMotion.None);
        set
        {
            if (value >= 0 && value < MotionsOut.Count)
                Set("Animation out", c => ((TitleClip)c).AnimationOut = (TitleMotion)value);
        }
    }

    public bool HasAnimationIn => Title is { AnimationIn: not TitleMotion.None, Scroll: TitleScroll.None };
    public bool HasAnimationOut => Title is { AnimationOut: not TitleMotion.None, Scroll: TitleScroll.None };

    public static IReadOnlyList<string> Scrolls { get; } =
        ["None", "Credits roll (up the screen)", "Opening crawl (into the distance)", "Ticker (right to left)"];

    public int ScrollIndex
    {
        get => (int)(Title?.Scroll ?? TitleScroll.None);
        set
        {
            if (value >= 0 && value < Scrolls.Count)
                Set("Scrolling", c => ((TitleClip)c).Scroll = (TitleScroll)value);
        }
    }

    public bool IsScrolling => Title is { Scroll: not TitleScroll.None };
    public bool IsNotScrolling => Title is { Scroll: TitleScroll.None };

    public bool IsTicker => Title is { Scroll: TitleScroll.Ticker };

    /// <summary>Pixels per second at 1080p; 0 fits the scroll to the clip's length.</summary>
    public double ScrollSpeed
    {
        get => Title?.ScrollSpeed ?? 0;
        set => Set("Scroll speed", c => ((TitleClip)c).ScrollSpeed = Math.Round(value), "scrollspeed");
    }

    public string ScrollSpeedText => ScrollSpeed > 0 ? $"{ScrollSpeed:0}"
        : IsTicker && ScrollLoop ? $"{TitleClip.DefaultTickerSpeed:0}" : "Fit";

    public bool ScrollLoop
    {
        get => Title?.ScrollLoop ?? false;
        set => Set(value ? "Loop the ticker" : "Stop looping", c => ((TitleClip)c).ScrollLoop = value);
    }

    /// <summary>Stretches the title to the end of the footage (stopping at the next clip on its track).</summary>
    [RelayCommand]
    private void CoverVideo()
    {
        if (Title is not { } title)
            return;
        var end = _session.Project.AllClips().Where(x => x.Clip is MediaClip && x.Clip.Id != ClipId).Select(x => x.Clip.End).DefaultIfEmpty(0).Max();
        if (end <= title.Start + 0.1)
            return;
        _session.Editor.TrimEnd(ClipId, _session.Editor.ClampTrimEnd(ClipId, end));
    }

    public string ScrollNote => Title?.Scroll switch
    {
        TitleScroll.Credits => "Speed \"Fit\" scrolls it once over the title's length. Write \"Role | Name\" for two columns.",
        TitleScroll.Crawl => "Speed \"Fit\" scrolls it once over the title's length. Paragraphs are justified, as in the films.",
        TitleScroll.Ticker => "Each line becomes an item; Box draws a band behind it. Loop repeats the text for as long as the title lasts: " +
                              "stretch it over the video. A picture stays at the left end of the band (or the right, with \"Right of the text\").",
        _ => "",
    };

    public double AnimationInDuration
    {
        get => Title?.AnimationInDuration ?? 0.8;
        set => Set("Animation in length", c => ((TitleClip)c).AnimationInDuration = value, "animin");
    }

    public double AnimationOutDuration
    {
        get => Title?.AnimationOutDuration ?? 0.6;
        set => Set("Animation out length", c => ((TitleClip)c).AnimationOutDuration = value, "animout");
    }

    // ---- Lottie text ----------------------------------------------------------------------------

    public bool IsAnimationMedia => MediaFile?.Kind == MediaKind.Animation;

    private IReadOnlyList<AnimationTextViewModel>? _animationTexts;

    /// <summary>
    /// The animation's text layers, each with the text this clip shows. Built once per inspector (so
    /// typing isn't interrupted when the inspector refreshes after each change).
    /// </summary>
    public IReadOnlyList<AnimationTextViewModel> AnimationTexts
    {
        get
        {
            if (_animationTexts != null)
                return _animationTexts;
            var layers = Array.Empty<(string Name, string Text)>() as IReadOnlyList<(string Name, string Text)>;
            if (IsAnimationMedia && MediaFile!.Path is { } path && File.Exists(path))
            {
                try
                {
                    var json = LottieMedia.ReadJson(path);
                    layers = LottieMedia.TextLayers(json);
                    _animationHasPictures = LottieMedia.HasPictures(json);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    Core.Diagnostics.Log.Warn("inspector", $"Couldn't read the text layers of {path}.", ex);
                }
            }
            _animationTexts = layers.Select(l => new AnimationTextViewModel(this, l.Name, l.Text)).ToList();
            return _animationTexts;
        }
    }

    public bool HasAnimationTexts => AnimationTexts.Count > 0;

    private bool _animationHasPictures;

    /// <summary>The animation uses pictures, which aren't drawn: said in its settings.</summary>
    public bool AnimationHasPictures
    {
        get
        {
            _ = AnimationTexts; // reads the file (once)
            return _animationHasPictures;
        }
    }

    public string AnimationPicturesNote => "This animation uses pictures, which aren't shown yet: only its shapes and text play.";

    internal string? AnimationText(string layer) => Media?.TextOverrides.GetValueOrDefault(layer);

    internal void SetAnimationText(string layer, string original, string text) =>
        Set("Animation text", c =>
        {
            var overrides = ((MediaClip)c).TextOverrides;
            if (text == original)
                overrides.Remove(layer);
            else
                overrides[layer] = text;
        }, "lottie:" + layer);

    public double PositionX
    {
        get => (Title?.X ?? 0.5) * 100;
        set => Set("Position", c => ((TitleClip)c).X = value / 100, "tx");
    }

    public double PositionY
    {
        get => (Title?.Y ?? 0.5) * 100;
        set => Set("Position", c => ((TitleClip)c).Y = value / 100, "ty");
    }

    [RelayCommand]
    private void PlaceTitle(string where) => Set("Position", c =>
    {
        var t = (TitleClip)c;
        // Lower thirds sit on the left; the others are centred.
        var centre = where != "lower";
        t.Alignment = centre ? CoreTextAlignment.Center : CoreTextAlignment.Left;
        (t.X, t.Y) = where switch
        {
            "top" => (0.5, 0.12),
            "lower" => (0.06, 0.82),
            "bottom" => (0.5, 0.92),
            _ => (0.5, 0.5),
        };
    });

    // ---- annotations -------------------------------------------------------------------------

    public static IReadOnlyList<AnnotationShape> Shapes { get; } = Enum.GetValues<AnnotationShape>();

    public AnnotationShape Shape
    {
        get => Annotation?.Shape ?? AnnotationShape.Rectangle;
        set => Set("Shape", c =>
        {
            var a = (AnnotationClip)c;
            a.Shape = value;
            a.Strength = AnnotationClip.DefaultStrength(value);
        });
    }

    public double StrokeWidth
    {
        get => Annotation?.StrokeWidth ?? 6;
        set => Set("Line width", c => ((AnnotationClip)c).StrokeWidth = value, "stroke");
    }

    public double Strength
    {
        get => Annotation is { } a ? (a.Shape is AnnotationShape.Spotlight or AnnotationShape.Highlight ? a.Strength * 100 : a.Strength) : 0;
        set => Set("Strength", c =>
        {
            var a = (AnnotationClip)c;
            a.Strength = a.Shape is AnnotationShape.Spotlight or AnnotationShape.Highlight ? value / 100 : value;
        }, "strength");
    }

    public string StrengthLabel => Annotation?.Shape switch
    {
        AnnotationShape.Blur => "Blur",
        AnnotationShape.Pixelate => "Block size",
        AnnotationShape.Spotlight => "Darkness %",
        AnnotationShape.Highlight => "Opacity %",
        _ => "Strength",
    };

    public double StrengthMax => Annotation?.Shape is AnnotationShape.Spotlight or AnnotationShape.Highlight ? 100 : 80;

    // ---- effects -----------------------------------------------------------------------------

    public ObservableCollection<EffectViewModel> Effects { get; } = [];

    public IEnumerable<EffectDefinition> AvailableEffects => EffectCatalog.All.Where(e =>
        (e.IsAudio && HasSound) || (!e.IsAudio && IsVisual));

    public bool HasEffects => Effects.Count > 0;

    [RelayCommand]
    private void AddEffect(EffectDefinition? definition)
    {
        if (definition == null)
            return;
        Set($"Add {definition.Label}", c => c.Effects.Add(definition.CreateDefault()));
        RebuildEffects();
    }

    internal void UpdateEffect(int index, string description, Action<Core.Model.Effect> change, string? mergeKey) =>
        _session.Editor.UpdateClip(ClipId, description, c =>
        {
            if (index < c.Effects.Count)
                change(c.Effects[index]);
        }, mergeKey == null ? null : mergeKey + ClipId);

    internal void RemoveEffect(int index)
    {
        Set("Remove effect", c =>
        {
            if (index < c.Effects.Count)
                c.Effects.RemoveAt(index);
        });
        RebuildEffects();
    }

    private string _effectsSignature = "";

    private void RebuildEffects()
    {
        var clip = Clip;
        var signature = clip == null ? "" : string.Join("|", clip.Effects.Select(e => e.Type + e.Enabled + string.Join(",", e.Parameters.Values)));
        if (signature == _effectsSignature && Effects.Count == (clip?.Effects.Count ?? 0))
            return;
        _effectsSignature = signature;
        Effects.Clear();
        if (clip == null)
            return;
        for (var i = 0; i < clip.Effects.Count; i++)
        {
            var def = EffectCatalog.Find(clip.Effects[i].Type);
            if (def != null)
                Effects.Add(new EffectViewModel(this, i, def, clip.Effects[i]));
        }
        OnPropertyChanged(nameof(HasEffects));
    }

    // ---- plumbing ----------------------------------------------------------------------------

    /// <summary>Reloads everything from the model (after an edit, undo or redo).</summary>
    public void Refresh()
    {
        RebuildEffects();
        foreach (var text in _animationTexts ?? [])
            text.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private void Set(string description, Action<Clip> change, string? mergeKey = null)
    {
        if (Clip == null)
            return;
        _session.Editor.UpdateClip(ClipId, description, change, mergeKey == null ? null : mergeKey + ":" + ClipId);
    }

    private static Color ParseColor(string? text, Color fallback) =>
        !string.IsNullOrEmpty(text) && Color.TryParse(text, out var c) ? c : fallback;

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>One text layer of a Lottie animation, with the text the clip shows in it.</summary>
public sealed partial class AnimationTextViewModel(ClipInspectorViewModel owner, string name, string original) : ObservableObject
{
    public string Name { get; } = name;

    public string Text
    {
        get => owner.AnimationText(Name) ?? original;
        set => owner.SetAnimationText(Name, original, value);
    }

    public void Refresh() => OnPropertyChanged(nameof(Text));
}
