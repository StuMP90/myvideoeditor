using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MyVideoEditor.App.Views;

/// <summary>Keyboard shortcuts and how things work (Help button / F1).</summary>
public partial class HelpWindow : Window
{
    // (keys, what it does); a null key is a plain note line.
    private static readonly (string Title, (string? Keys, string Text)[] Rows)[] Sections =
    {
        ("Playback", new (string?, string)[]
        {
            ("Space", "Play / pause"),
            ("←  /  →", "Previous / next frame"),
            ("Shift+←  /  Shift+→", "Back / forward one second"),
            ("↑  /  ↓", "Previous / next cut (clip edge)"),
            ("Home  /  End", "Start / end of the project"),
            ("Click or drag the ruler", "Move the playhead (scrub)"),
            (null, "The preview is rendered by the same engine as the export, so what you see is what you get. It plays at a reduced size for smoothness."),
        }),
        ("Editing", new (string?, string)[]
        {
            ("Drag a clip", "Move it, also to another track. It snaps to clip edges and the playhead (hold Alt not to snap)"),
            ("Drag a clip's edge", "Trim it (shorten or lengthen, within its footage)"),
            ("Ctrl+click / Shift+click", "Select several clips (then drag to move them together)"),
            ("S", "Split clips under the playhead (just the selected ones, if any)"),
            ("Delete  /  Shift+Delete", "Delete / delete and close the gap (ripple)"),
            ("Ctrl+D", "Duplicate the selected clip"),
            ("Ctrl+Z  /  Ctrl+Shift+Z or Ctrl+Y", "Undo / redo"),
            ("Ctrl+wheel  /  Ctrl+=  /  Ctrl+−  /  Ctrl+0", "Zoom the timeline in, out, or to fit"),
            ("Wheel  /  Shift+wheel", "Scroll the timeline sideways / up and down"),
            ("Right-click a clip", "Split, duplicate, dissolve, detach audio, delete"),
            ("M  H  L on a track", "Mute its sound, hide its picture, lock it against changes"),
            (null, "Placing a clip over another trims or splits what's underneath, like most editors. Upper video tracks are drawn in front of lower ones."),
            (null, "Dissolve: two clips that touch on the same track can cross-fade (\"Dissolve in\" in the clip's settings). It uses footage from before the second clip's start, or after the first clip's end."),
        }),
        ("Titles, effects and annotations", new (string?, string)[]
        {
            ("T  /  Title ▾", "Add a title at the playhead (title, title card, lower third, caption, chapter, end card)"),
            ("Annotate buttons", "Choose a tool, then drag on the preview: arrow, box, circle, highlight, spotlight, callout, numbered step, blur, pixelate, zoom"),
            ("Shift while drawing", "Keep the tool for another annotation; squares and circles stay square"),
            ("Drag in the preview", "Move the selected title, annotation or video; drag the white handles to resize annotations"),
            ("Esc", "Put the tool down / clear the selection"),
            (null, "Blur and pixelate hide passwords, emails and other personal details in recordings before you share them. Zoom enlarges an area for a while (it fades in and out)."),
            (null, "Effects (in the clip's settings): colour correction, black and white, sharpen, blur, stabilise, green screen, and for sound: normalise loudness, reduce noise, cut rumble or hiss, compressor and more."),
        }),
        ("Projects and export", new (string?, string)[]
        {
            ("Ctrl+N  /  Ctrl+O  /  Ctrl+S", "New / open / save project (Ctrl+Shift+S: save as)"),
            ("Ctrl+I", "Import media (or drag files onto the window)"),
            ("Ctrl+E", "Export: MP4 (H.264/H.265/AV1), WebM, MKV, ProRes, GIF or audio, at any size"),
            (null, "The first video you add sets the project's size and frame rate. Change them with ⚙ Project."),
            (null, "Projects are saved as .mveproj files that refer to your media; keep media next to the project to move it to another computer. Unsaved work is autosaved every minute and offered back if the app closes unexpectedly."),
        }),
        ("Convert", new (string?, string)[]
        {
            (null, "Converts files without a timeline: change format, resize (fit with bars, fill and crop, or stretch), upscale, change the frame rate, trim, rotate, or copy into a new container without re-encoding."),
            (null, "Upscaling: CPU methods (Lanczos, Enhanced) work everywhere. With a graphics card (integrated is fine), Sharper detail (camera footage) and Cleaner lines (animation, screen recordings) are nearly as fast and crisper. AI methods (Real-ESRGAN, Real-CUGAN) are the strongest but slow without a dedicated graphics card. All are free and run on this computer; GPU and AI parts download on first use."),
            (null, "Choose what the video is, set a size, then Test speed on this PC: it times each method on a few frames of your video and recommends the best one that finishes within an hour."),
        }),
        ("Capture", new (string?, string)[]
        {
            (null, "Records a window, a screen or a dragged-out region, with the mouse pointer and optionally the microphone and/or computer sound. Recordings are saved as MP4 in your Videos folder; Edit & annotate opens one in the editor."),
            (null, "Linux: recording uses X11. In a Wayland session only XWayland apps can be captured; choose an Xorg session at login to record everything. Computer sound is recorded from the default output's monitor."),
            (null, "Windows: computer sound needs a loopback device such as Stereo Mix (enable it in Sound settings → Recording)."),
        }),
    };

    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.DisplayVersion;
        foreach (var (title, rows) in Sections)
        {
            SectionsPanel.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*") };
            foreach (var (keys, text) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var row = grid.RowDefinitions.Count - 1;
                if (keys == null)
                {
                    var note = new TextBlock { Text = text, Classes = { "note" } };
                    Grid.SetRow(note, row);
                    Grid.SetColumnSpan(note, 2);
                    grid.Children.Add(note);
                    continue;
                }
                var key = new TextBlock { Text = keys, Classes = { "key" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                var what = new TextBlock { Text = text, Classes = { "what" } };
                Grid.SetRow(key, row);
                Grid.SetRow(what, row);
                Grid.SetColumn(what, 1);
                grid.Children.Add(key);
                grid.Children.Add(what);
            }
            SectionsPanel.Children.Add(grid);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnLicences(object? sender, RoutedEventArgs e) => _ = new LicensesWindow().ShowDialog(this);
}
