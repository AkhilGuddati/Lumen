using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;

namespace Lumen;

public class GameDialog : Window
{
    public TextBox NameBox = new(), PathBox = new(), ArgsBox = new(),
                   GridBox = new(), HeroBox = new(), LogoBox = new(),
                   MainBox = new(), ExtraBox = new(), FullBox = new(), DescBox = new(), TrailerLinkBox = new(), MusicLinkBox = new(), HltbLinkBox = new();

    public static double Num(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static string Hrs(double? v) => v is > 0 ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) : "";

    public GameDialog(Game? g)
    {
        Title = g is null ? "Add game" : "Edit game";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Bg"];
        Foreground = (Brush)Application.Current.Resources["Fg"];
        SourceInitialized += (_, _) => Win.Mica(this, !Win.Light());

        NameBox.Text = g?.Name ?? "";
        PathBox.Text = g?.Path ?? "";
        ArgsBox.Text = g?.Args ?? "";
        GridBox.Text = g?.GridUrl ?? "";
        HeroBox.Text = g?.HeroUrl ?? "";
        LogoBox.Text = g?.LogoUrl ?? "";
        MainBox.Text = Hrs(g?.Main);
        ExtraBox.Text = Hrs(g?.Extra);
        FullBox.Text = Hrs(g?.Full);
        TrailerLinkBox.Text = g?.TrailerUrl ?? "";
        MusicLinkBox.Text = g?.MusicUrl ?? "";
        HltbLinkBox.Text = g?.HltbUrl ?? "";
        foreach (var t2 in new[] { TrailerLinkBox, MusicLinkBox, HltbLinkBox }) t2.Padding = new Thickness(6, 5, 6, 5);
        DescBox.Text = g?.Description ?? "";
        DescBox.AcceptsReturn = true;
        DescBox.TextWrapping = TextWrapping.Wrap;
        DescBox.MinHeight = 72;
        DescBox.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        DescBox.Padding = new Thickness(6, 5, 6, 5);
        foreach (var t in new[] { NameBox, PathBox, ArgsBox, GridBox, HeroBox, LogoBox, MainBox, ExtraBox, FullBox })
            t.Padding = new Thickness(6, 5, 6, 5);

        var browse = new Button { Content = "Browse…", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Games & launchers|*.exe;*.lnk;*.bat;*.url|All files|*.*" };
            if (d.ShowDialog() != true) return;
            PathBox.Text = d.FileName;
            if (NameBox.Text.Trim() == "")
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(d.FileName).Replace('_', ' ').Replace('-', ' ');
        };

        var ok = new Button { Content = "Save", IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        ok.Click += (_, _) =>
        {
            if (NameBox.Text.Trim() == "" || PathBox.Text.Trim() == "") return;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };

        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(PathBox);

        var hours = new UniformGrid { Columns = 3 };
        hours.Children.Add(Small("Main story (h)", MainBox));
        hours.Children.Add(Small("Main + extras (h)", ExtraBox));
        hours.Children.Add(Small("Completionist (h)", FullBox));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        root.Children.Add(L("Name"));
        root.Children.Add(NameBox);
        root.Children.Add(L("Launcher path (exe, shortcut, or a URL like steam://rungameid/12345)"));
        root.Children.Add(pathRow);
        root.Children.Add(L("Launch arguments (optional)"));
        root.Children.Add(ArgsBox);
        root.Children.Add(Head("Custom artwork — image link or file path (PNG/JPG). Empty = find automatically"));
        root.Children.Add(L("Cover (portrait, ~600×900)"));
        root.Children.Add(GridBox);
        root.Children.Add(L("Banner (wide hero image)"));
        root.Children.Add(HeroBox);
        root.Children.Add(L("Logo (transparent PNG) — shown as the title over the banner"));
        root.Children.Add(LogoBox);
        root.Children.Add(Head("Trailer & music — paste a YouTube link (or video/audio link or file). Empty = find automatically"));
        root.Children.Add(L("Trailer link (saved as a small mp4)"));
        root.Children.Add(TrailerLinkBox);
        root.Children.Add(L("Theme music link (YouTube → saved as m4a audio)"));
        root.Children.Add(MusicLinkBox);
        root.Children.Add(Head("Description — filled from HowLongToBeat when available; type your own to override"));
        root.Children.Add(DescBox);
        root.Children.Add(Head("Play time — filled from HowLongToBeat; type values here to override"));
        root.Children.Add(hours);
        root.Children.Add(L("HowLongToBeat game page link (optional), e.g. howlongtobeat.com/game/68151"));
        root.Children.Add(HltbLinkBox);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static TextBlock L(string t) => new() { Text = t, Margin = new Thickness(0, 12, 0, 5), Opacity = .7, TextWrapping = TextWrapping.Wrap };

    static TextBlock Head(string t) => new()
    { Text = t, Margin = new Thickness(0, 24, 0, 0), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    static StackPanel Small(string label, TextBox box)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        p.Children.Add(L(label));
        p.Children.Add(box);
        return p;
    }
}

public static class Dialogs
{
    public static string? Ask(Window owner, string title, string text, string initial)
    {
        var w = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.Resources["Bg"],
            Foreground = (Brush)Application.Current.Resources["Fg"]
        };
        w.SourceInitialized += (_, _) => Win.Mica(w, !Win.Light());
        var tb = new TextBox { Text = initial, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 12, 0, 18) };
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { result = tb.Text.Trim(); w.DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(tb);
        root.Children.Add(ok);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    // SteamGridDB key + UI scale + text size. `preview` (optional) shows both scales live while a slider moves.
    public static (string key, double scale, double text)? AppSettings(Window owner, Action<double, double>? preview = null)
    {
        var w = new Window
        {
            Title = "Settings", Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.Resources["Bg"],
            Foreground = (Brush)Application.Current.Resources["Fg"]
        };
        w.SourceInitialized += (_, _) => Win.Mica(w, !Win.Light());

        var key = new TextBox { Text = Store.Settings.SgdbKey, Padding = new Thickness(6, 5, 6, 5) };
        var start = Store.Settings.UiScale <= 0 ? 1.0 : Store.Settings.UiScale;
        var startText = Store.Settings.TextScale <= 0 ? 1.0 : Store.Settings.TextScale;

        Slider? slider = null, textSlider = null;
        void Changed() { if (slider != null && textSlider != null) preview?.Invoke(slider.Value, textSlider.Value); }

        DockPanel ScaleRow(double value, double min, double max, out Slider sl)
        {
            var pct = new TextBlock
            {
                Text = $"{value:0%}", VerticalAlignment = VerticalAlignment.Center, MinWidth = 52,
                HorizontalAlignment = HorizontalAlignment.Right, Opacity = .8
            };
            var s = new Slider
            {
                Minimum = min, Maximum = max, Value = value, TickFrequency = .05, IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0)
            };
            s.ValueChanged += (_, _) => { pct.Text = $"{s.Value:0%}"; Changed(); };
            var row = new DockPanel();
            DockPanel.SetDock(pct, Dock.Right);
            row.Children.Add(pct);
            row.Children.Add(s);
            sl = s;
            return row;
        }

        var scaleRow = ScaleRow(start, .6, 1.6, out var sl1);
        var textRow = ScaleRow(startText, .7, 1.6, out var sl2);
        slider = sl1; textSlider = sl2;

        (string, double, double)? result = null;
        var ok = new Button { Content = "Save", IsDefault = true, Width = 90, Padding = new Thickness(0, 6, 0, 6) };
        ok.Click += (_, _) => { result = (key.Text.Trim(), slider.Value, textSlider.Value); w.DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => preview?.Invoke(start, startText);   // put the old sizes back
        w.Closed += (_, _) => { if (result is null) preview?.Invoke(start, startText); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        static TextBlock L(string t) => new() { Text = t, Margin = new Thickness(0, 16, 0, 5), Opacity = .7, TextWrapping = TextWrapping.Wrap };

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(L("SteamGridDB API key (free: steamgriddb.com → Preferences → API). Used for covers, banners and logos."));
        root.Children.Add(key);
        root.Children.Add(L("Interface scale — makes everything bigger or smaller; useful on small screens."));
        root.Children.Add(scaleRow);
        root.Children.Add(L("Text size — play times, genres, developer and description, on top of the interface scale. The same for every game."));
        root.Children.Add(textRow);
        root.Children.Add(buttons);
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.ShowDialog();
        return result;
    }

    // Paste links (or file paths) for cover / banner / logo. The caller downloads them automatically.
    public static (string cover, string banner, string logo)? ArtLinks(Window owner, string cover, string banner, string logo)
    {
        var w = new Window
        {
            Title = "Artwork links", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.Resources["Bg"],
            Foreground = (Brush)Application.Current.Resources["Fg"]
        };
        w.SourceInitialized += (_, _) => Win.Mica(w, !Win.Light());

        TextBox Box(string text) => new() { Text = text, Padding = new Thickness(6, 5, 6, 5) };
        var coverBox = Box(cover);
        var bannerBox = Box(banner);
        var logoBox = Box(logo);

        (string, string, string)? result = null;
        var ok = new Button { Content = "Download", IsDefault = true, Width = 110, Padding = new Thickness(0, 6, 0, 6) };
        ok.Click += (_, _) => { result = (coverBox.Text.Trim(), bannerBox.Text.Trim(), logoBox.Text.Trim()); w.DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        TextBlock Lbl(string t) => new() { Text = t, Margin = new Thickness(0, 12, 0, 5), Opacity = .7, TextWrapping = TextWrapping.Wrap };

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock
        {
            Text = "Paste an image link (or a file path) — it is downloaded automatically. Leave a box empty to keep the artwork the app found.",
            TextWrapping = TextWrapping.Wrap, Opacity = .8
        });
        root.Children.Add(Lbl("Cover (portrait, ~600×900)"));
        root.Children.Add(coverBox);
        root.Children.Add(Lbl("Banner (wide hero image)"));
        root.Children.Add(bannerBox);
        root.Children.Add(Lbl("Logo (transparent PNG) — shown as the title over the banner"));
        root.Children.Add(logoBox);
        root.Children.Add(buttons);
        w.Content = root;
        w.ShowDialog();
        return result;
    }
}
