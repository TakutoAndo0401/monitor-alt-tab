using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MonitorAltTab;

internal static class Palette
{
    private static readonly ConcurrentDictionary<string, Brush> brushes = new();
    internal static bool Light(Settings s)
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowColor.R > 128;
        if (s.Theme == "light") return true;
        if (s.Theme == "dark") return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (int?)key?.GetValue("AppsUseLightTheme") != 0;
    }
    internal static Brush Brush(string hex) => brushes.GetOrAdd(hex, value =>
    { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; });
    internal static TextBlock Text(string value, double size, Brush foreground, FontWeight? weight = null) => new() { Text = value, FontSize = size, Foreground = foreground, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
}

internal sealed class SwitcherWindow : Window
{
    private readonly List<nint> thumbnails = new();
    private readonly List<Native.Point> thumbnailSizes = new();
    private readonly List<Native.ThumbnailProperties?> thumbnailLayouts = new();
    private readonly List<bool> thumbnailAttempted = new();
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(WindowEntry Entry, Border Preview)> previewHosts = new();
    private SwitchSession? session;
    private Settings settings;
    private ScrollViewer? scroller;
    private readonly List<Border> cards = new();
    private nint hwnd;
    private WindowEntry[] previousWindows = Array.Empty<WindowEntry>();
    private bool viewBuilt, previousLight, previousPreviews, previousHighContrast, previousSample;
    private bool lightTheme;
    private nint previousMonitor;
    private bool renderPending;
    private int selectedIndex = -1;
    internal Action<SwitchDiagnosticEvent>? Trace;
    private Brush foreground = Brushes.White, muted = Brushes.Gray, cardBackground = Brushes.Black, accent = Brushes.DodgerBlue;
    internal Action<WindowEntry>? Clicked;
    internal nint Handle => hwnd;
    internal long SessionId => session?.Id ?? -1;
    internal SwitcherWindow(Settings settings)
    {
        this.settings = settings;
        Title = "Monitor Alt Tab — 切り替え"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; FontFamily = new FontFamily("Segoe UI Variable, Yu Gothic UI");
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)Native.GetWindowLongPtr(hwnd, -20);
            Native.SetWindowLongPtr(hwnd, -20, (nint)((ex & ~0x08000000L) | 0x80));
            int corner = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        LayoutUpdated += (_, _) => UpdateThumbnails();
        IsVisibleChanged += (_, _) =>
        {
            CompositionTarget.Rendering -= Rendering;
            if (IsVisible) CompositionTarget.Rendering += Rendering;
            else ClearThumbnails();
        };
        Closed += (_, _) => { CompositionTarget.Rendering -= Rendering; ClearThumbnails(); };
        _ = new WindowInteropHelper(this).EnsureHandle();
    }
    private nint WndProc(nint h, int message, nint wp, nint lp, ref bool handled)
    {
        // Alt release belongs to the active switcher, rather than opening an app menu.
        if (message == 0x112 && ((long)wp & 0xFFF0) == 0xF100) { handled = true; return 0; }
        return 0;
    }
    private void Rendering(object? sender, EventArgs e)
    {
        if (!renderPending || !IsVisible) return;
        renderPending = false;
        CompositionTarget.Rendering -= Rendering;
        Trace?.Invoke(SwitchDiagnosticEvent.UiRenderPass);
    }
    internal bool Present(SwitchSession value, Settings config, bool sample = false)
    {
        renderPending = true;
        CompositionTarget.Rendering -= Rendering;
        CompositionTarget.Rendering += Rendering;
        session = value; settings = config; ClearThumbnails();
        bool light = Palette.Light(config);
        lightTheme = light;
        bool reuse = viewBuilt && previousMonitor == value.Monitor && previousLight == light && previousPreviews == config.ShowPreviews &&
            previousHighContrast == SystemParameters.HighContrast && previousSample == sample &&
            Rules.SameWindowCards(previousWindows, value.Windows);
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Palette.Brush(light ? "#F3F3F3" : "#202020");
        foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Palette.Brush(light ? "#1A1A1A" : "#F5F5F5");
        muted = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Palette.Brush(light ? "#606060" : "#B6B6B6");
        cardBackground = Palette.Brush(light ? "#FFFFFF" : "#2B2B2B");
        accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : SystemParameters.WindowGlassBrush;
        var monitor = Native.Monitor(value.Monitor);
        double scale = 1;
        if (hwnd != 0) scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        Width = Math.Min(1000, Math.Max(250, monitor.Work.Width / scale - 64));
        Height = config.ShowPreviews ? 304 : 212;
        if (!reuse)
        {
        cards.Clear(); previewHosts.Clear(); selectedIndex = -1;
        var layout = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var count = Palette.Text($"{value.Windows.Length} ウィンドウ", 12, muted);
        DockPanel.SetDock(count, Dock.Right); header.Children.Add(count);
        header.Children.Add(Palette.Text(sample ? "ディスプレイ 2  /  デザインプレビュー" : $"{monitor.Device.Replace(@"\\.\", "")}  /  この画面のウィンドウ", 13, foreground, FontWeights.SemiBold));
        layout.Children.Add(header);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        scroller = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        scroller.ScrollChanged += (_, _) => UpdateThumbnails();
        foreach (var entry in value.Windows)
        {
            var card = BuildCard(entry, sample); row.Children.Add(card); cards.Add(card);
        }
        if (cards.Count == 0)
            row.Children.Add(new Border { Height = config.ShowPreviews ? 178 : 86, Child = Palette.Text("この画面に切り替えられるウィンドウはありません", 16, muted), Padding = new Thickness(12, 28, 12, 12) });
        layout.Children.Add(scroller);
        var footer = Palette.Text("Tab  次へ      Shift + Tab  前へ      Altを離して確定      Esc  キャンセル", 11, muted);
        footer.Margin = new Thickness(0, 18, 0, 0); layout.Children.Add(footer);
        Content = layout;
        previousWindows = value.Windows; previousLight = light; previousPreviews = config.ShowPreviews;
        previousHighContrast = SystemParameters.HighContrast; previousSample = sample; previousMonitor = value.Monitor; viewBuilt = true;
        }
        Select(value.Index);
        // Position with native physical pixels so negative monitor coordinates and mixed DPI work.
        if (!IsVisible) Show();
        scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        Width = Math.Min(1000, Math.Max(250, monitor.Work.Width / scale - 64));
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        Native.SetWindowPos(hwnd, -1, monitor.Work.Left + (monitor.Work.Width - w) / 2, monitor.Work.Top + (monitor.Work.Height - h) / 2, w, h, 0x10 | 0x40);
        // Acquire foreground before expensive layout/thumbnail work. The game may change
        // foreground windows while leaving fullscreen; delaying this transfer can cancel display.
        bool activatedEarly = true;
        if (!sample)
        {
            Trace?.Invoke(SwitchDiagnosticEvent.UiActivationStart);
            activatedEarly = ForegroundActivation.Request(hwnd);
            Trace?.Invoke(activatedEarly ? SwitchDiagnosticEvent.UiActivationComplete : SwitchDiagnosticEvent.UiActivationRejected);
        }
        UpdateLayout();
        Trace?.Invoke(SwitchDiagnosticEvent.UiLayoutReady);
        if (config.ShowPreviews && !sample)
            foreach (var host in previewHosts)
            {
                thumbnails.Add(0); thumbnailSizes.Add(default); thumbnailLayouts.Add(null); thumbnailAttempted.Add(false);
            }
        UpdateThumbnails();
        Trace?.Invoke(SwitchDiagnosticEvent.UiThumbnailsReady);
        // Own the foreground during a real switch session so the final Alt release
        // reaches this process. Transfer to the target before hiding this window.
        return activatedEarly;
    }
    private Border BuildCard(WindowEntry entry, bool sample)
    {
        var column = new StackPanel();
        if (settings.ShowPreviews)
        {
            var preview = new Border { Height = 112, Background = Palette.Brush(lightTheme ? "#E8E8E8" : "#181818"), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12), ClipToBounds = true };
            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = Palette.Text(sample ? (entry.AppName == "Edge" ? "◎" : entry.AppName == "Explorer" ? "▣" : "⌘") : entry.Minimized ? "▱" : "▣", 30, muted);
            glyph.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(glyph);
            center.Children.Add(Palette.Text(entry.Minimized ? "最小化中" : sample ? entry.AppName : "プレビュー", 11, muted)); preview.Child = center;
            column.Children.Add(preview); previewHosts.Add((entry, preview));
        }
        var label = new DockPanel();
        if (entry.Executable != null)
        {
            var image = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(image, Dock.Left); label.Children.Add(image);
            LoadIcon(image, entry.Executable);
        }
        label.Children.Add(Palette.Text(entry.AppName, 12, muted)); column.Children.Add(label);
        var title = Palette.Text(entry.Title, 13, foreground, FontWeights.SemiBold); title.Margin = new Thickness(0, 5, 0, 0); column.Children.Add(title);
        var card = new Border { Width = 208, Padding = new Thickness(12), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = cardBackground, CornerRadius = new CornerRadius(10), Child = column, ToolTip = entry.Title, Cursor = System.Windows.Input.Cursors.Hand };
        AutomationProperties.SetName(card, entry.AppName + " " + entry.Title);
        card.MouseLeftButtonUp += (_, _) => Clicked?.Invoke(entry);
        return card;
    }
    private static async void LoadIcon(Image image, string executable)
    {
        // Frozen images are safe to transfer to WPF; shell/file access never blocks display.
        try
        {
            if (iconCache.Count >= 128 && !iconCache.ContainsKey(executable)) iconCache.Clear();
            var task = iconCache.GetOrAdd(executable, path => Task.Run(() => ReadIcon(path)));
            image.Source = await task;
        }
        catch { }
    }
    private static ImageSource? ReadIcon(string executable)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
            if (icon == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24)); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
    internal void Select(int index)
    {
        if (selectedIndex != index && selectedIndex >= 0 && selectedIndex < cards.Count)
        {
            cards[selectedIndex].BorderBrush = Brushes.Transparent;
            AutomationProperties.SetHelpText(cards[selectedIndex], "");
        }
        selectedIndex = index;
        if (index >= 0 && index < cards.Count)
        {
            cards[index].BorderBrush = accent;
            AutomationProperties.SetHelpText(cards[index], "選択中。Altを離すと切り替えます。");
            scroller?.ScrollToHorizontalOffset(Math.Max(0, index * 218 - (Width - 48 - 208) / 2));
        }
    }
    private void UpdateThumbnails()
    {
        if (!IsVisible || hwnd == 0 || scroller == null) return;
        double scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        var clip = scroller.TranslatePoint(new System.Windows.Point(0, 0), this);
        for (int i = 0; i < thumbnails.Count && i < previewHosts.Count; i++)
        {
            var host = previewHosts[i].Preview;
            var pos = host.TranslatePoint(new System.Windows.Point(0, 0), this);
            bool visible = pos.X >= clip.X - 1 && pos.X + host.ActualWidth <= clip.X + scroller.ViewportWidth + 1;
            nint thumbnail = thumbnails[i];
            if (thumbnail == 0 && visible && !thumbnailAttempted[i])
            {
                thumbnailAttempted[i] = true;
                if (Native.DwmRegisterThumbnail(hwnd, previewHosts[i].Entry.Handle, out thumbnail) == 0)
                {
                    thumbnails[i] = thumbnail;
                    Native.DwmQueryThumbnailSourceSize(thumbnail, out var sourceSize); thumbnailSizes[i] = sourceSize;
                }
                else thumbnail = 0;
            }
            if (thumbnail == 0) continue;
            double x = pos.X, y = pos.Y, w = host.ActualWidth, h = host.ActualHeight;
            var size = thumbnailSizes[i];
            if (size.X > 0 && size.Y > 0)
            {
                double factor = Math.Min(w / size.X, h / size.Y);
                double iw = size.X * factor, ih = size.Y * factor; x += (w - iw) / 2; y += (h - ih) / 2; w = iw; h = ih;
            }
            // Hide partially clipped thumbnails; native DWM content is not clipped by WPF.
            var props = new Native.ThumbnailProperties { Flags = 1 | 4 | 8 | 16, Destination = new Native.Rect { Left = (int)(x * scale), Top = (int)(y * scale), Right = (int)((x + w) * scale), Bottom = (int)((y + h) * scale) }, Opacity = 255, Visible = visible, ClientOnly = true };
            var previous = thumbnailLayouts[i];
            if (previous is { } old && old.Visible == props.Visible && old.Destination.Left == props.Destination.Left &&
                old.Destination.Top == props.Destination.Top && old.Destination.Right == props.Destination.Right &&
                old.Destination.Bottom == props.Destination.Bottom) continue;
            if (Native.DwmUpdateThumbnailProperties(thumbnail, ref props) == 0) thumbnailLayouts[i] = props;
        }
    }
    private void ClearThumbnails() { foreach (nint thumb in thumbnails) if (thumb != 0) Native.DwmUnregisterThumbnail(thumb); thumbnails.Clear(); thumbnailSizes.Clear(); thumbnailLayouts.Clear(); thumbnailAttempted.Clear(); }
    internal void SavePreview(string path)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
