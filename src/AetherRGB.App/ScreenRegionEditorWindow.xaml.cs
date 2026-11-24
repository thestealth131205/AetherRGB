using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AetherRGB.Core.Config;
using AetherRGB.Core.Devices;
using AetherRGB.Core.Diagnostics;
using AetherRGB.Core.Screen;
using Forms = System.Windows.Forms;

namespace AetherRGB.App;

/// <summary>Bereichs-Editor für den Bildschirm-Sync: Standbild eines Monitors (oder Screenshot-Datei, oder live), darauf
/// Rechtecke aufziehen, Farben per Pipette aufnehmen, Regel und Wirkung je Bereich einstellen. Die Messwerte werden sofort
/// am Bild ausgewertet, die Vorschau zeigt, was auf der Effekt-Fläche ankommt.</summary>
public partial class ScreenRegionEditorWindow : Window
{
    private readonly ScreenProfileStore _store;
    private readonly ScreenCaptureService _capture;
    private readonly ScreenSyncConfig _config;
    private readonly ScreenHub _hub = ScreenHub.Shared;

    public event Action<string, LogLevel>? LogMessage;

    private ScreenProfile _profile = new();
    private bool _dirty, _suppress;

    /// <summary>Angezeigtes Bild (Standbild in voller Auflösung oder Live-Bild) und das verkleinerte Analysebild dazu.</summary>
    private ScreenImage? _image, _analysisImage;
    private ScreenAnalysis? _analysis;
    private ScreenAnalyzer? _liveAnalyzer;

    private ScreenRegion? _selected;
    private int _selectedColor = -1;

    private enum Drag { None, New, Move, Resize }
    private Drag _drag;
    private Point _dragStart;
    private RelRect _dragOrigin;
    private Rectangle? _newRect;

    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly WriteableBitmap _preview = new(160, 80, 96, 96, PixelFormats.Bgr32, null);

    private const string AutoMonitorLabel = "Automatisch (Monitor des Spiels)";

    public ScreenRegionEditorWindow(ScreenProfileStore store, ScreenCaptureService capture, ScreenSyncConfig config, string? profileId)
    {
        _store = store;
        _capture = capture;
        _config = config;
        InitializeComponent();
        CanvasPreview.Source = _preview;

        MonitorCombo.Items.Add(AutoMonitorLabel);
        foreach (var m in ScreenCaptureService.GetMonitors()) MonitorCombo.Items.Add(m);
        MonitorCombo.SelectedItem = MonitorCombo.Items.OfType<MonitorInfo>().FirstOrDefault(m => m.DeviceName == config.Monitor) ?? (object)AutoMonitorLabel;

        _liveTimer.Tick += (_, _) => LiveTick();
        _previewTimer.Tick += (_, _) => RenderPreview();
        _previewTimer.Start();

        ReloadProfileCombo(profileId ?? _hub.CurrentProfile?.Id ?? config.DefaultProfileId);
        ShowImage(null);
        Closing += OnWindowClosing;
        Closed += (_, _) => { _liveTimer.Stop(); _previewTimer.Stop(); };
    }

    // ===================== Profile =====================

    private void ReloadProfileCombo(string? selectId)
    {
        _suppress = true;
        var profiles = _store.Profiles;
        ProfileCombo.ItemsSource = profiles;
        var pick = profiles.FirstOrDefault(p => p.Id == selectId) ?? profiles.FirstOrDefault();
        ProfileCombo.SelectedItem = pick;
        _suppress = false;
        LoadProfile(pick?.Clone() ?? new ScreenProfile { Name = "Neues Profil" });
    }

    private void LoadProfile(ScreenProfile profile)
    {
        _profile = profile;
        _dirty = false;
        Title = "AetherRGB — Bildschirm-Sync: Bereiche";
        _selected = _profile.Regions.FirstOrDefault();
        _selectedColor = -1;
        _liveAnalyzer = null;
        _suppress = true;
        ProfileNameBox.Text = _profile.Name;
        ProcessNameBox.Text = _profile.ProcessName;
        HudHiddenCombo.SelectedIndex = (int)_profile.WhenHudHidden;
        _suppress = false;
        BuildBaseColor();
        ResetTemplateButton.IsEnabled = ScreenProfileTemplates.Get(_profile.Id) is not null;
        RefreshAll();
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var answer = MessageBox.Show(this, $"Änderungen an „{_profile.Name}“ speichern?", "Bildschirm-Sync",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) Save();
        return true;
    }

    private void OnProfileComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || ProfileCombo.SelectedItem is not ScreenProfile picked) return;
        if (picked.Id == _profile.Id) return;
        if (!ConfirmDiscard())
        {
            _suppress = true;
            ProfileCombo.SelectedItem = _store.Profiles.FirstOrDefault(p => p.Id == _profile.Id);
            _suppress = false;
            return;
        }
        LoadProfile(picked.Clone());
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var profile = new ScreenProfile { Name = "Neues Profil" };
        _store.Save(profile);
        ReloadProfileCombo(profile.Id);
    }

    private void OnCopyProfileClick(object sender, RoutedEventArgs e)
    {
        var copy = _profile.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..12];
        copy.Name = _profile.Name.Replace(" (Vorlage)", "") + " (Kopie)";
        _store.Save(copy);
        _dirty = false;
        ReloadProfileCombo(copy.Id);
        Log($"Bildschirm-Profil „{copy.Name}“ angelegt.");
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Profil „{_profile.Name}“ löschen?", "Bildschirm-Sync", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _store.Delete(_profile.Id); }
        catch (Exception ex) { MessageBox.Show(this, "Löschen fehlgeschlagen: " + ex.Message, "Bildschirm-Sync"); return; }
        Log($"Bildschirm-Profil „{_profile.Name}“ gelöscht.");
        _dirty = false;
        ReloadProfileCombo(null);
    }

    private void OnResetTemplateClick(object sender, RoutedEventArgs e)
    {
        if (ScreenProfileTemplates.Get(_profile.Id) is not { } template) return;
        if (MessageBox.Show(this, "Alle Bereiche dieses Profils durch die mitgelieferte Vorlage ersetzen?", "Bildschirm-Sync",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LoadProfile(template);
        MarkDirty();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        try
        {
            _store.Save(_profile);
            _dirty = false;
            Title = "AetherRGB — Bildschirm-Sync: Bereiche";
            var id = _profile.Id;
            _suppress = true;
            ProfileCombo.ItemsSource = _store.Profiles;
            ProfileCombo.SelectedItem = _store.Profiles.FirstOrDefault(p => p.Id == id);
            _suppress = false;
            Log($"Bildschirm-Profil „{_profile.Name}“ gespeichert ({_store.PathFor(id)}).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Speichern fehlgeschlagen: " + ExceptionText.Describe(ex), "Bildschirm-Sync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnProfileFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _profile.Name = ProfileNameBox.Text;
        _profile.ProcessName = ProcessNameBox.Text.Trim();
        if (HudHiddenCombo.SelectedIndex >= 0) _profile.WhenHudHidden = (HudHiddenMode)HudHiddenCombo.SelectedIndex;
        MarkDirty();
    }

    private void BuildBaseColor()
    {
        BaseColorPanel.Children.Clear();
        BaseColorPanel.Children.Add(new TextBlock { Text = "Grundfarbe unter allen Ebenen", Style = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        BaseColorPanel.Children.Add(ColorSwatch(() => _profile.BaseColor, c => { _profile.BaseColor = c; MarkDirty(); }));
    }

    private void MarkDirty()
    {
        _dirty = true;
        _liveAnalyzer = null;
        Title = "AetherRGB — Bildschirm-Sync: Bereiche *";
        Reanalyze();
        RedrawOverlay();
        RefreshRegionList();
    }

    // ===================== Bildquellen =====================

    private string? SelectedMonitor => MonitorCombo.SelectedItem is MonitorInfo m ? m.DeviceName : null;

    private async void OnStillClick(object sender, RoutedEventArgs e) => await TakeStill();

    private async void OnStillDelayedClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var text = button.Content;
        button.IsEnabled = false;
        for (int i = 5; i > 0; i--)
        {
            button.Content = $"📷  {i} …";
            await Task.Delay(1000);
        }
        button.Content = text;
        button.IsEnabled = true;
        await TakeStill();
    }

    private async Task TakeStill()
    {
        LiveToggle.IsChecked = false;
        var image = await _capture.CaptureStillAsync(SelectedMonitor);
        if (image is null)
        {
            MessageBox.Show(this, "Kein Bild erhalten. Details stehen im Log. Notfalls einen Screenshot über „🖼 Bild laden …“ öffnen.",
                "Bildschirm-Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShowImage(image);
        if (IsMostlyBlack(image))
            MessageBox.Show(this, "Das Standbild ist fast komplett schwarz. Das passiert bei exklusivem Vollbild mit der GDI-Aufnahme oder " +
                                  "bei geschützten Inhalten. Im Spiel „Fenstermodus-Vollbild“ (randlos) wählen hilft meistens.",
                "Bildschirm-Sync", MessageBoxButton.OK, MessageBoxImage.Information);
        Activate();
    }

    private static bool IsMostlyBlack(ScreenImage img)
    {
        var avg = img.Average(RelRect.Full);
        return avg.R + avg.G + avg.B < 6;
    }

    private void OnLoadImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Bilder (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(dialog.FileName);
            bitmap.EndInit();
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            var image = new ScreenImage(w, h, pixels);

            // Screenshot über zwei Monitore nebeneinander (z. B. 3840 × 1080): einen davon nehmen.
            if (w >= h * 3.2)
            {
                var answer = MessageBox.Show(this,
                    $"Das Bild ist {w} × {h} Pixel groß — vermutlich zwei Monitore nebeneinander.\n\nJa = linke Hälfte, Nein = rechte Hälfte, Abbrechen = ganzes Bild",
                    "Bildschirm-Sync", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Yes) image = image.Crop(0, 0, w / 2, h);
                else if (answer == MessageBoxResult.No) image = image.Crop(w / 2, 0, w - w / 2, h);
            }
            LiveToggle.IsChecked = false;
            ShowImage(image);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Bild konnte nicht geladen werden: " + ex.Message, "Bildschirm-Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLiveChanged(object sender, RoutedEventArgs e)
    {
        if (LiveToggle.IsChecked == true)
        {
            _liveAnalyzer = null;
            _liveTimer.Start();
            Log("Bereichs-Editor: Live-Ansicht (Monitor laut Einstellungen).", LogLevel.Debug);
        }
        else _liveTimer.Stop();
    }

    private void LiveTick()
    {
        _hub.Demand(); // hält die Aufnahme am Laufen, auch ohne Bildschirm-Effekt im Effekt-Fenster
        var image = _hub.LatestImage;
        if (image is null || ReferenceEquals(image, _image)) return;
        if (_image is null || _image.Width != image.Width || _image.Height != image.Height) ShowImage(image, analyze: false);
        else
        {
            _image = image;
            _analysisImage = image;
            ShotImage.Source = ToBitmap(image);
        }
        _liveAnalyzer ??= new ScreenAnalyzer(_profile);
        _analysis = _liveAnalyzer.Analyze(image);
        UpdateAnalysisText();
        RefreshRegionList();
    }

    private void ShowImage(ScreenImage? image, bool analyze = true)
    {
        _image = image;
        EmptyHint.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        if (image is null)
        {
            ShotImage.Source = null;
            _analysisImage = null;
            _analysis = null;
            ImageRoot.Width = 1920;
            ImageRoot.Height = 1080;
        }
        else
        {
            ShotImage.Source = ToBitmap(image);
            ImageRoot.Width = image.Width;
            ImageRoot.Height = image.Height;
            int factor = ScreenImage.FactorFor(image.Width);
            _analysisImage = factor == 1 ? image : ScreenImage.Downsample(image.Bgra, image.Width, image.Height, image.Width * 4, factor);
        }
        if (analyze) Reanalyze();
        RedrawOverlay();
        RefreshRegionList();
    }

    private static BitmapSource ToBitmap(ScreenImage image)
    {
        // Bgr32: das Alpha-Byte der Aufnahme ist nicht immer gesetzt (GDI liefert 0).
        var bmp = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr32, null, image.Bgra, image.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ===================== Auswertung + Vorschau =====================

    /// <summary>Profil am Standbild auswerten. Drei Durchläufe, damit „HUD sichtbar“ (wechselt erst nach 3 Bildern) stimmt.</summary>
    private void Reanalyze()
    {
        if (LiveToggle.IsChecked == true) return; // live wertet der Timer aus
        if (_analysisImage is null) { _analysis = null; UpdateAnalysisText(); return; }
        var analyzer = new ScreenAnalyzer(_profile);
        long t = Stopwatch.GetTimestamp();
        for (int i = 0; i < 3; i++) _analysis = analyzer.Analyze(_analysisImage, t + i * Stopwatch.Frequency / 30);
        UpdateAnalysisText();
    }

    private void UpdateAnalysisText()
    {
        if (_analysis is null)
        {
            AnalysisText.Text = "Ohne Bild keine Messwerte.";
            return;
        }
        var lines = new List<string> { $"HUD sichtbar: {(_analysis.HudVisible ? "ja" : "NEIN — es gilt „Ohne sichtbares HUD“")}     Mischfarbe Monitor: {_analysis.Average.ToHex()}" };
        foreach (var r in _profile.Regions)
            lines.Add($"{(r.Enabled ? "•" : "○")} {r.Name,-34} {RuleName(r.Rule),-16} {ValueText(r)}");
        AnalysisText.Text = string.Join("\n", lines);
    }

    private string ValueText(ScreenRegion region)
    {
        if (!region.Enabled) return "aus";
        if (_analysis is null || !_analysis.Results.TryGetValue(region.Id, out var r)) return "–";
        return region.Rule switch
        {
            RegionRule.Average => r.Average.ToHex(),
            RegionRule.ColorShare => $"{r.Value:P0} (Anteil {r.Raw * region.FullAt:P0})",
            RegionRule.BarFill => $"{r.Raw:P0}",
            RegionRule.HudCheck => $"{r.Raw:P0} passend → {(r.Passed ? "sichtbar" : "nicht sichtbar")}",
            RegionRule.ColorFlash => $"Anteil {r.Raw:P0}" + (RecentlyTriggered(region, r) ? "  ⚡" : ""),
            RegionRule.ValueDrop => $"Summe {r.Raw:0.00}" + (RecentlyTriggered(region, r) ? "  ⚡" : ""),
            _ => "",
        };
    }

    private static bool RecentlyTriggered(ScreenRegion region, RegionResult r) =>
        r.LastTrigger != 0 && (Stopwatch.GetTimestamp() - r.LastTrigger) * 1000.0 / Stopwatch.Frequency < Math.Max(600, region.FlashMs);

    private void RenderPreview()
    {
        var analysis = _analysis;
        int w = _preview.PixelWidth, h = _preview.PixelHeight;
        var pixels = new int[w * h];
        if (analysis is not null)
        {
            // Profil des Editors (auch ungespeichert) anzeigen, nicht das des Hubs.
            var a = analysis.Profile == _profile ? analysis : analysis with { Profile = _profile };
            var elapsed = _clock.Elapsed;
            long now = Stopwatch.GetTimestamp();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = CanvasScreenEffect.SampleProfile(a, (x + 0.5) / w, (y + 0.5) / h, elapsed, now);
                    pixels[y * w + x] = (c.R << 16) | (c.G << 8) | c.B;
                }
        }
        _preview.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
    }

    // ===================== Bereiche auf dem Bild =====================

    private static Color RuleColor(RegionRule rule) => rule switch
    {
        RegionRule.Average => Color.FromRgb(200, 200, 200),
        RegionRule.ColorShare => Color.FromRgb(176, 38, 255),
        RegionRule.BarFill => Color.FromRgb(34, 197, 94),
        RegionRule.ColorFlash => Color.FromRgb(239, 68, 68),
        RegionRule.ValueDrop => Color.FromRgb(249, 115, 22),
        _ => Color.FromRgb(56, 189, 248),
    };

    private void RedrawOverlay()
    {
        Overlay.Children.Clear();
        double w = ImageRoot.Width, h = ImageRoot.Height, unit = w / 640.0;
        foreach (var region in _profile.Regions)
        {
            if (region.Rule == RegionRule.ValueDrop) continue; // misst kein Bild, Bereich ohne Bedeutung
            bool selected = region == _selected;
            var color = RuleColor(region.Rule);
            var a = region.Area;
            var rect = new Rectangle
            {
                Width = Math.Max(1, a.W * w), Height = Math.Max(1, a.H * h),
                Stroke = new SolidColorBrush(color), StrokeThickness = unit * (selected ? 1.6 : 0.8),
                Fill = new SolidColorBrush(Color.FromArgb((byte)(selected ? 50 : 22), color.R, color.G, color.B)),
                StrokeDashArray = region.Enabled ? null : new DoubleCollection { 3, 2 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, a.X * w);
            Canvas.SetTop(rect, a.Y * h);
            Overlay.Children.Add(rect);

            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 10, 11, 15)),
                Padding = new Thickness(unit * 1.5, unit * 0.5, unit * 1.5, unit * 0.5),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = $"{region.Name}  {ValueText(region)}", Foreground = new SolidColorBrush(color), FontSize = unit * 5 },
            };
            Canvas.SetLeft(label, a.X * w);
            double top = a.Y * h - unit * 8;
            Canvas.SetTop(label, top < 0 ? (a.Y + a.H) * h + unit : top);
            Overlay.Children.Add(label);

            if (selected)
            {
                var handle = new Rectangle { Width = unit * 5, Height = unit * 5, Fill = new SolidColorBrush(color), IsHitTestVisible = false };
                Canvas.SetLeft(handle, (a.X + a.W) * w - unit * 2.5);
                Canvas.SetTop(handle, (a.Y + a.H) * h - unit * 2.5);
                Overlay.Children.Add(handle);
            }
        }
        if (_newRect is not null) Overlay.Children.Add(_newRect);
    }

    private ScreenRegion? HitTest(Point p, out bool onHandle)
    {
        double w = ImageRoot.Width, h = ImageRoot.Height, grab = w / 640.0 * 5;
        onHandle = false;
        if (_selected is { } sel && sel.Rule != RegionRule.ValueDrop)
        {
            var a = sel.Area;
            if (Math.Abs(p.X - (a.X + a.W) * w) <= grab && Math.Abs(p.Y - (a.Y + a.H) * h) <= grab)
            {
                onHandle = true;
                return sel;
            }
        }
        // Oberste (zuletzt gezeichnete) zuerst; kleine Bereiche in großen bleiben so erreichbar, wenn sie später kommen.
        for (int i = _profile.Regions.Count - 1; i >= 0; i--)
        {
            var r = _profile.Regions[i];
            if (r.Rule != RegionRule.ValueDrop && r.Area.Contains(p.X / w, p.Y / h)) return r;
        }
        return null;
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        if (_image is null) return;
        if (ToolPipette.IsChecked == true || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            PickColor(p);
            return;
        }
        if (ToolNew.IsChecked == true)
        {
            _drag = Drag.New;
            _dragStart = p;
            _newRect = new Rectangle { Stroke = Brushes.White, StrokeThickness = ImageRoot.Width / 640.0, StrokeDashArray = new DoubleCollection { 2, 2 }, IsHitTestVisible = false };
            Overlay.CaptureMouse();
            return;
        }

        var hit = HitTest(p, out bool onHandle);
        if (hit != _selected) Select(hit);
        if (hit is null) return;
        _drag = onHandle ? Drag.Resize : Drag.Move;
        _dragStart = p;
        _dragOrigin = hit.Area;
        Overlay.CaptureMouse();
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Overlay);
        double w = ImageRoot.Width, h = ImageRoot.Height;
        if (_image is not null && p.X >= 0 && p.Y >= 0 && p.X < _image.Width && p.Y < _image.Height)
        {
            var c = _image.GetPixel((int)p.X, (int)p.Y);
            var (hue, s, v) = ColorMatcher.ToHsv(c.R, c.G, c.B);
            double sx = _image.SourceWidth / (double)_image.Width;
            CursorText.Text = $"x {p.X * sx:0}  y {p.Y * sx:0}   {c.ToHex()}  Farbton {hue:0}°  Sätt. {s:0.00}  Hell. {v:0.00}";
        }

        switch (_drag)
        {
            case Drag.New when _newRect is not null:
                Canvas.SetLeft(_newRect, Math.Min(p.X, _dragStart.X));
                Canvas.SetTop(_newRect, Math.Min(p.Y, _dragStart.Y));
                _newRect.Width = Math.Abs(p.X - _dragStart.X);
                _newRect.Height = Math.Abs(p.Y - _dragStart.Y);
                RedrawOverlay();
                break;
            case Drag.Move when _selected is not null:
            {
                double dx = (p.X - _dragStart.X) / w, dy = (p.Y - _dragStart.Y) / h;
                _selected.Area = new RelRect(Math.Clamp(_dragOrigin.X + dx, 0, 1 - _dragOrigin.W), Math.Clamp(_dragOrigin.Y + dy, 0, 1 - _dragOrigin.H), _dragOrigin.W, _dragOrigin.H);
                RedrawOverlay();
                break;
            }
            case Drag.Resize when _selected is not null:
            {
                double right = Math.Clamp(p.X / w, _dragOrigin.X + 0.002, 1), bottom = Math.Clamp(p.Y / h, _dragOrigin.Y + 0.002, 1);
                _selected.Area = new RelRect(_dragOrigin.X, _dragOrigin.Y, right - _dragOrigin.X, bottom - _dragOrigin.Y);
                RedrawOverlay();
                break;
            }
        }
    }

    private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        Overlay.ReleaseMouseCapture();
        var p = e.GetPosition(Overlay);
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.New)
        {
            _newRect = null;
            if (Math.Abs(p.X - _dragStart.X) < 3 || Math.Abs(p.Y - _dragStart.Y) < 3) { RedrawOverlay(); return; }
            var region = new ScreenRegion
            {
                Name = $"Bereich {_profile.Regions.Count + 1}",
                Rule = RegionRule.Average,
                Area = RelRect.FromPixels(_dragStart.X, _dragStart.Y, p.X, p.Y, ImageRoot.Width, ImageRoot.Height),
            };
            _profile.Regions.Add(region);
            ToolSelect.IsChecked = true;
            Select(region);
            MarkDirty();
            return;
        }
        if (_selected is not null && _selected.Area != _dragOrigin)
        {
            MarkDirty();
            BuildRegionPanel(); // Zahlenfelder der Position aktualisieren
        }
    }

    private void OnOverlayRightDown(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.None) return;
        Select(HitTest(e.GetPosition(Overlay), out _));
    }

    private void PickColor(Point p)
    {
        if (_image is null) return;
        if (_selected is null || !UsesColors(_selected.Rule))
        {
            MessageBox.Show(this, "Zuerst einen Bereich mit der Regel „Farbanteil“, „Balken-Füllstand“, „Farb-Blitz“ oder „HUD-Prüfung“ auswählen, dann mit der Pipette ins Bild klicken.",
                "Bildschirm-Sync", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Mittelwert über 3 × 3 Pixel gegen Rauschen und Kanten.
        int x = Math.Clamp((int)p.X, 1, _image.Width - 2), y = Math.Clamp((int)p.Y, 1, _image.Height - 2);
        var color = _image.Average(new RelRect((x - 1.0) / _image.Width, (y - 1.0) / _image.Height, 3.0 / _image.Width, 3.0 / _image.Height));
        _selected.Colors.Add(ColorMatcher.FromSample(color));
        _selectedColor = _selected.Colors.Count - 1;
        Log($"Bereich „{_selected.Name}“: Farbe {color.ToHex()} aufgenommen.", LogLevel.Debug);
        BuildRegionPanel();
        MarkDirty();
    }

    private static bool UsesColors(RegionRule rule) => rule is RegionRule.ColorShare or RegionRule.BarFill or RegionRule.ColorFlash or RegionRule.HudCheck;

    // ===================== Bereichsliste =====================

    private void Select(ScreenRegion? region)
    {
        _selected = region;
        _selectedColor = region is null || region.Colors.Count == 0 ? -1 : 0;
        RefreshRegionList();
        BuildRegionPanel();
        RedrawOverlay();
    }

    private void RefreshAll()
    {
        Reanalyze();
        RefreshRegionList();
        BuildRegionPanel();
        RedrawOverlay();
    }

    private void RefreshRegionList()
    {
        _suppress = true;
        RegionList.Items.Clear();
        foreach (var r in _profile.Regions)
        {
            var item = new ListBoxItem
            {
                Tag = r,
                Content = new TextBlock
                {
                    Text = $"{(r.Enabled ? "" : "(aus) ")}{r.Name} — {RuleName(r.Rule)}: {ValueText(r)}",
                    Foreground = new SolidColorBrush(RuleColor(r.Rule)),
                },
            };
            RegionList.Items.Add(item);
            if (r == _selected) item.IsSelected = true;
        }
        _suppress = false;
    }

    private void OnRegionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        Select((RegionList.SelectedItem as ListBoxItem)?.Tag as ScreenRegion);
    }

    private void MoveSelected(int delta)
    {
        if (_selected is null) return;
        int i = _profile.Regions.IndexOf(_selected), j = i + delta;
        if (j < 0 || j >= _profile.Regions.Count) return;
        _profile.Regions.RemoveAt(i);
        _profile.Regions.Insert(j, _selected);
        MarkDirty();
    }

    private void OnRegionUpClick(object sender, RoutedEventArgs e) => MoveSelected(+1);
    private void OnRegionDownClick(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnRegionDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var copy = _selected.Clone();
        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.Name += " (Kopie)";
        copy.Area = new RelRect(Math.Min(copy.Area.X + 0.01, 1 - copy.Area.W), Math.Min(copy.Area.Y + 0.01, 1 - copy.Area.H), copy.Area.W, copy.Area.H);
        _profile.Regions.Insert(_profile.Regions.IndexOf(_selected) + 1, copy);
        Select(copy);
        MarkDirty();
    }

    private void OnRegionDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_selected is null) return;
        var removed = _selected;
        _profile.Regions.Remove(removed);
        foreach (var r in _profile.Regions) r.Sources.Remove(removed.Id);
        Select(_profile.Regions.LastOrDefault());
        MarkDirty();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
        else if (e.Key == Key.Escape && _drag != Drag.None)
        {
            if (_drag is Drag.Move or Drag.Resize && _selected is not null) _selected.Area = _dragOrigin;
            _drag = Drag.None;
            _newRect = null;
            Overlay.ReleaseMouseCapture();
            RedrawOverlay();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    // ===================== Eigenschaften des gewählten Bereichs =====================

    private static readonly (RegionRule Rule, string Name, string Help)[] Rules =
    {
        (RegionRule.Average, "Mischfarbe", "Die LEDs zeigen die Durchschnittsfarbe des Bereichs."),
        (RegionRule.ColorShare, "Farbanteil", "Wie viel des Bereichs hat eine der Farben (z. B. Sturm in der Minimap)? Mehr Anteil = kräftigere Farbe."),
        (RegionRule.BarFill, "Balken-Füllstand", "Wie weit reicht die Füllfarbe eines Balkens (Leben, Schild, Munition)? Der Bereich sollte genau den Balken umfassen."),
        (RegionRule.ColorFlash, "Farb-Blitz", "Blitz, wenn der Anteil einer Farbe plötzlich steigt (z. B. roter Rand bei Treffern)."),
        (RegionRule.ValueDrop, "Wert fällt → Blitz", "Blitz, wenn die Werte anderer Bereiche fallen — z. B. Leben + Schild = Schaden. Misst selbst kein Bild."),
        (RegionRule.HudCheck, "HUD-Prüfung", "Kein Licht. Fehlen die Farben hier (Menü, Lobby, tot), pausieren die übrigen Regeln."),
    };

    private static string RuleName(RegionRule rule) => Rules.First(r => r.Rule == rule).Name;

    private Style Muted => (Style)FindResource("MutedText");

    private void BuildRegionPanel()
    {
        RegionPanel.Children.Clear();
        var region = _selected;
        if (region is null)
        {
            RegionPanel.Children.Add(new TextBlock
            {
                Text = "Kein Bereich gewählt. Mit „▭ Neuer Bereich“ ein Rechteck auf dem Bild aufziehen oder einen Bereich in der Liste anklicken.",
                Style = Muted, TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        Header("Bereich");
        TextField("Name", region.Name, v => { region.Name = v; MarkDirty(); });
        Check("Aktiv", region.Enabled, v => { region.Enabled = v; MarkDirty(); });

        var ruleCombo = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
        foreach (var r in Rules) ruleCombo.Items.Add(r.Name);
        ruleCombo.SelectedIndex = Array.FindIndex(Rules, r => r.Rule == region.Rule);
        ruleCombo.SelectionChanged += (_, _) =>
        {
            region.Rule = Rules[ruleCombo.SelectedIndex].Rule;
            if (region.Rule == RegionRule.HudCheck && region.Threshold < 0.1) region.Threshold = 0.3;
            BuildRegionPanel();
            MarkDirty();
        };
        Label("Regel");
        RegionPanel.Children.Add(ruleCombo);
        RegionPanel.Children.Add(new TextBlock { Text = Rules.First(r => r.Rule == region.Rule).Help, Style = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        if (region.Rule != RegionRule.ValueDrop)
            RectFields("Position auf dem Monitor (%)", () => region.Area, v => { region.Area = v; MarkDirty(); });

        if (UsesColors(region.Rule)) BuildColorList(region);

        Header("Messung");
        switch (region.Rule)
        {
            case RegionRule.ColorShare:
                SliderField("Voll bei Anteil", 0.05, 1, region.FullAt, "P0", v => { region.FullAt = v; MarkDirty(); });
                SliderField("Mindestanteil (darunter 0)", 0, 0.3, region.MinShare, "P0", v => { region.MinShare = v; MarkDirty(); });
                break;
            case RegionRule.BarFill:
            {
                var dir = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
                foreach (var d in new[] { "von links nach rechts", "von rechts nach links", "von unten nach oben", "von oben nach unten" }) dir.Items.Add(d);
                dir.SelectedIndex = (int)region.Direction;
                dir.SelectionChanged += (_, _) => { region.Direction = (BarDirection)dir.SelectedIndex; MarkDirty(); };
                Label("Balken füllt sich");
                RegionPanel.Children.Add(dir);
                break;
            }
            case RegionRule.ColorFlash:
                SliderField("Blitz ab Anstieg um", 0.01, 0.8, region.Threshold, "P0", v => { region.Threshold = v; MarkDirty(); });
                break;
            case RegionRule.HudCheck:
                SliderField("HUD sichtbar ab Anteil", 0.02, 1, region.Threshold, "P0", v => { region.Threshold = v; MarkDirty(); });
                break;
            case RegionRule.ValueDrop:
            {
                SliderField("Blitz ab Abfall um", 0.005, 0.5, region.Threshold, "P1", v => { region.Threshold = v; MarkDirty(); });
                Label("Werte dieser Bereiche zusammenzählen");
                var candidates = _profile.Regions.Where(r => r != region && r.Rule is RegionRule.BarFill or RegionRule.ColorShare).ToList();
                if (candidates.Count == 0)
                    RegionPanel.Children.Add(new TextBlock { Text = "Noch kein Bereich mit „Balken-Füllstand“ oder „Farbanteil“.", Style = Muted, TextWrapping = TextWrapping.Wrap });
                foreach (var c in candidates)
                {
                    var box = new CheckBox { Content = c.Name, IsChecked = region.Sources.Contains(c.Id), Foreground = (Brush)FindResource("BrushText"), Margin = new Thickness(0, 2, 0, 2) };
                    box.Checked += (_, _) => { if (!region.Sources.Contains(c.Id)) region.Sources.Add(c.Id); MarkDirty(); };
                    box.Unchecked += (_, _) => { region.Sources.Remove(c.Id); MarkDirty(); };
                    RegionPanel.Children.Add(box);
                }
                break;
            }
        }
        if (region.Rule is RegionRule.ColorShare or RegionRule.BarFill)
            SliderField("Glättung (ms)", 0, 1500, region.SmoothingMs, "0", v => { region.SmoothingMs = Math.Round(v / 10) * 10; MarkDirty(); });

        if (!region.HasLayer) return;

        Header("Wirkung auf die LEDs");
        switch (region.Rule)
        {
            case RegionRule.BarFill:
                ColorField("Farbe bei 0 %", () => region.ColorLow, c => { region.ColorLow = c; MarkDirty(); });
                Check("Zwischenfarbe bei 50 %", region.ColorMid is not null, v => { region.ColorMid = v ? (region.ColorMid ?? RgbColor.Lerp(region.ColorLow, region.ColorHigh, 0.5)) : null; BuildRegionPanel(); MarkDirty(); });
                if (region.ColorMid is not null) ColorField("Farbe bei 50 %", () => region.ColorMid ?? RgbColor.Black, c => { region.ColorMid = c; MarkDirty(); });
                ColorField("Farbe bei 100 %", () => region.ColorHigh, c => { region.ColorHigh = c; MarkDirty(); });
                Check("Deckkraft folgt dem Wert (leer = unsichtbar)", region.ValueAsOpacity, v => { region.ValueAsOpacity = v; MarkDirty(); });
                SliderField("Pulsieren unter", 0, 0.6, region.PulseBelow, "P0", v => { region.PulseBelow = v; MarkDirty(); });
                break;
            case RegionRule.ColorShare:
                ColorField("Farbe", () => region.ColorHigh, c => { region.ColorHigh = c; MarkDirty(); });
                break;
            case RegionRule.ColorFlash:
            case RegionRule.ValueDrop:
                ColorField("Blitzfarbe", () => region.ColorHigh, c => { region.ColorHigh = c; MarkDirty(); });
                SliderField("Blitzdauer (ms)", 100, 2000, region.FlashMs, "0", v => { region.FlashMs = (int)Math.Round(v / 10) * 10; MarkDirty(); });
                break;
        }
        SliderField("Deckkraft", 0, 1, region.Opacity, "P0", v => { region.Opacity = v; MarkDirty(); });
        if (region.Rule is RegionRule.BarFill or RegionRule.ColorShare)
        {
            var display = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            display.Items.Add("Ganze Zielfläche einfärben");
            display.Items.Add("Als Balken über die Zielfläche (links → rechts)");
            display.SelectedIndex = (int)region.Display;
            display.SelectionChanged += (_, _) => { region.Display = (LayerDisplay)display.SelectedIndex; MarkDirty(); };
            Label("Darstellung");
            RegionPanel.Children.Add(display);
        }
        RectFields("Zielfläche auf der Effekt-Fläche (%)", () => region.Target, v => { region.Target = v; MarkDirty(); });
    }

    private void BuildColorList(ScreenRegion region)
    {
        Header("Farben");
        if (region.Colors.Count == 0)
            RegionPanel.Children.Add(new TextBlock { Text = "Noch keine Farbe. Pipette wählen (oder Alt gedrückt halten) und ins Bild klicken.", Style = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        for (int i = 0; i < region.Colors.Count; i++)
        {
            int index = i;
            var m = region.Colors[i];
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = index == _selectedColor ? (Brush)FindResource("BrushPanelAlt") : Brushes.Transparent, Cursor = Cursors.Hand };
            var remove = new Button { Content = "✕", Width = 26, Padding = new Thickness(0), ToolTip = "Farbe entfernen" };
            remove.Click += (_, _) => { region.Colors.RemoveAt(index); _selectedColor = Math.Min(_selectedColor, region.Colors.Count - 1); BuildRegionPanel(); MarkDirty(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(ToWpf(m.Example())) };
            row.Children.Add(sw);
            row.Children.Add(new TextBlock
            {
                Text = $"Farbton {m.Hue:0}° ± {m.HueTolerance:0}°, Sätt. ≥ {m.MinSaturation:0.00}, Hell. {m.MinValue:0.00}–{m.MaxValue:0.00}",
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            });
            row.MouseLeftButtonUp += (_, _) => { _selectedColor = index; BuildRegionPanel(); };
            RegionPanel.Children.Add(row);
        }
        if (_selectedColor >= 0 && _selectedColor < region.Colors.Count)
        {
            int idx = _selectedColor;
            void Update(Func<ColorMatcher, ColorMatcher> change) { region.Colors[idx] = change(region.Colors[idx]); MarkDirty(); }
            var c = region.Colors[idx];
            SliderField("Farbton (°)", 0, 360, c.Hue, "0", v => Update(m => m with { Hue = Math.Round(v) }));
            SliderField("Toleranz Farbton (°)", 2, 90, c.HueTolerance, "0", v => Update(m => m with { HueTolerance = Math.Round(v) }));
            SliderField("Mindest-Sättigung", 0, 1, c.MinSaturation, "0.00", v => Update(m => m with { MinSaturation = Math.Round(v, 2) }));
            SliderField("Mindest-Helligkeit", 0, 1, c.MinValue, "0.00", v => Update(m => m with { MinValue = Math.Round(v, 2) }));
            SliderField("Höchst-Helligkeit", 0, 1, c.MaxValue, "0.00", v => Update(m => m with { MaxValue = Math.Round(v, 2) }));
        }
    }

    // ===================== kleine Bausteine =====================

    private void Header(string text) => RegionPanel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
    private void Label(string text) => RegionPanel.Children.Add(new TextBlock { Text = text, Style = Muted, TextWrapping = TextWrapping.Wrap });

    private void TextField(string label, string value, Action<string> apply)
    {
        Label(label);
        var box = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 6) };
        box.LostFocus += (_, _) => { if (box.Text != value) { value = box.Text; apply(box.Text); } };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && box.Text != value) { value = box.Text; apply(box.Text); } };
        RegionPanel.Children.Add(box);
    }

    private void Check(string label, bool value, Action<bool> apply)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = (Brush)FindResource("BrushText"), Margin = new Thickness(0, 2, 0, 6) };
        box.Checked += (_, _) => apply(true);
        box.Unchecked += (_, _) => apply(false);
        RegionPanel.Children.Add(box);
    }

    private void SliderField(string label, double min, double max, double value, string format, Action<double> apply)
    {
        var text = new TextBlock { Text = $"{label}: {Format(value, format)}", Style = Muted };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Margin = new Thickness(0, 0, 0, 6) };
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        debounce.Tick += (_, _) => { debounce.Stop(); apply(slider.Value); };
        slider.ValueChanged += (_, e) =>
        {
            text.Text = $"{label}: {Format(e.NewValue, format)}";
            debounce.Stop();
            debounce.Start();
        };
        RegionPanel.Children.Add(text);
        RegionPanel.Children.Add(slider);
    }

    private static string Format(double v, string format) => v.ToString(format, CultureInfo.CurrentCulture);

    private void ColorField(string label, Func<RgbColor> get, Action<RgbColor> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Style = Muted, Width = 150, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(ColorSwatch(get, apply));
        RegionPanel.Children.Add(row);
    }

    private FrameworkElement ColorSwatch(Func<RgbColor> get, Action<RgbColor> apply)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Border
        {
            Width = 28, Height = 24, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("BrushBorder"), Background = new SolidColorBrush(ToWpf(get())), Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 0), ToolTip = "Farbe wählen",
        };
        var hex = new TextBox { Text = get().ToHex(), Width = 80, VerticalAlignment = VerticalAlignment.Center };
        void Set(RgbColor c)
        {
            hex.Text = c.ToHex();
            swatch.Background = new SolidColorBrush(ToWpf(c));
            if (c != get()) apply(c);
        }
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            var current = get();
            using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
            if (dialog.ShowDialog() == Forms.DialogResult.OK) Set(new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        };
        void FromText()
        {
            var t = hex.Text.Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            try { Set(RgbColor.FromHex(t)); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { hex.Text = get().ToHex(); }
        }
        hex.LostFocus += (_, _) => FromText();
        hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) FromText(); };
        row.Children.Add(swatch);
        row.Children.Add(hex);
        return row;
    }

    private void RectFields(string label, Func<RelRect> get, Action<RelRect> apply)
    {
        Label(label);
        var r = get();
        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 2, 0, 8) };
        var boxes = new[] { r.X, r.Y, r.W, r.H }.Select((v, i) => new TextBox
        {
            Text = (v * 100).ToString("0.0", CultureInfo.CurrentCulture), Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0),
            ToolTip = new[] { "X (links)", "Y (oben)", "Breite", "Höhe" }[i],
        }).ToArray();
        void Commit()
        {
            var vals = boxes.Select(b => double.TryParse(b.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ? d / 100 : double.NaN).ToArray();
            if (vals.Any(double.IsNaN)) return;
            var next = new RelRect(vals[0], vals[1], vals[2], vals[3]).Normalized();
            if (next != get()) apply(next);
        }
        foreach (var b in boxes)
        {
            b.LostFocus += (_, _) => Commit();
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            grid.Children.Add(b);
        }
        RegionPanel.Children.Add(new TextBlock { Text = "X       Y       Breite   Höhe", Style = Muted, FontSize = 11 });
        RegionPanel.Children.Add(grid);
    }

    private static Color ToWpf(RgbColor c) => Color.FromRgb(c.R, c.G, c.B);

    private void Log(string message, LogLevel level = LogLevel.Info) => LogMessage?.Invoke(message, level);
}
