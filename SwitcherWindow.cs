using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    internal static bool Light(Settings s)
    {
        if (SystemParameters.HighContrast) return SystemColors.WindowColor.R > 128;
        if (s.Theme == "light") return true;
        if (s.Theme == "dark") return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return (int?)key?.GetValue("AppsUseLightTheme") != 0;
    }
    internal static Brush Brush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    internal static TextBlock Text(string value, double size, Brush foreground, FontWeight? weight = null) => new() { Text = value, FontSize = size, Foreground = foreground, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
}

internal sealed class SwitcherWindow : Window
{
    private readonly List<nint> thumbnails = new();
    private readonly List<(WindowEntry Entry, Border Preview)> previewHosts = new();
    private SwitchSession? session;
    private Settings settings;
    private ScrollViewer? scroller;
    private readonly List<Border> cards = new();
    private nint hwnd;
    private Brush foreground = Brushes.White, muted = Brushes.Gray, cardBackground = Brushes.Black, accent = Brushes.DodgerBlue;
    internal Action<WindowEntry>? Clicked;
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
            Native.SetWindowLongPtr(hwnd, -20, (nint)(ex | 0x08000080));
            int corner = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref corner, 4);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        LayoutUpdated += (_, _) => UpdateThumbnails();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClearThumbnails(); };
        Closed += (_, _) => ClearThumbnails();
    }
    private nint WndProc(nint h, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        return 0;
    }
    internal void Present(SwitchSession value, Settings config, bool sample = false)
    {
        session = value; settings = config; ClearThumbnails(); cards.Clear(); previewHosts.Clear();
        bool light = Palette.Light(config);
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
        Select(value.Index);
        // Position with native physical pixels so negative monitor coordinates and mixed DPI work.
        if (!IsVisible) Show();
        scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        Width = Math.Min(1000, Math.Max(250, monitor.Work.Width / scale - 64));
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        Native.SetWindowPos(hwnd, -1, monitor.Work.Left + (monitor.Work.Width - w) / 2, monitor.Work.Top + (monitor.Work.Height - h) / 2, w, h, 0x10 | 0x40);
        UpdateLayout();
        if (config.ShowPreviews && !sample)
            foreach (var host in previewHosts)
                if (Native.DwmRegisterThumbnail(hwnd, host.Entry.Handle, out nint thumb) == 0) thumbnails.Add(thumb); else thumbnails.Add(0);
        UpdateThumbnails();
    }
    private Border BuildCard(WindowEntry entry, bool sample)
    {
        var column = new StackPanel();
        if (settings.ShowPreviews)
        {
            var preview = new Border { Height = 112, Background = Palette.Brush(Palette.Light(settings) ? "#E8E8E8" : "#181818"), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 12), ClipToBounds = true };
            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = Palette.Text(sample ? (entry.AppName == "Edge" ? "◎" : entry.AppName == "Explorer" ? "▣" : "⌘") : entry.Minimized ? "▱" : "▣", 30, muted);
            glyph.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(glyph);
            center.Children.Add(Palette.Text(entry.Minimized ? "最小化中" : sample ? entry.AppName : "プレビュー", 11, muted)); preview.Child = center;
            column.Children.Add(preview); previewHosts.Add((entry, preview));
        }
        var label = new DockPanel();
        var icon = GetIcon(entry);
        if (icon != null) { var image = new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) }; DockPanel.SetDock(image, Dock.Left); label.Children.Add(image); }
        label.Children.Add(Palette.Text(entry.AppName, 12, muted)); column.Children.Add(label);
        var title = Palette.Text(entry.Title, 13, foreground, FontWeights.SemiBold); title.Margin = new Thickness(0, 5, 0, 0); column.Children.Add(title);
        var card = new Border { Width = 208, Padding = new Thickness(12), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = cardBackground, CornerRadius = new CornerRadius(10), Child = column, ToolTip = entry.Title, Cursor = System.Windows.Input.Cursors.Hand };
        AutomationProperties.SetName(card, entry.AppName + " " + entry.Title);
        card.MouseLeftButtonUp += (_, _) => Clicked?.Invoke(entry);
        return card;
    }
    private static ImageSource? GetIcon(WindowEntry entry)
    {
        if (entry.Executable == null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(entry.Executable);
            if (icon == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24)); bitmap.Freeze(); return bitmap;
        }
        catch { return null; }
    }
    internal void Select(int index)
    {
        for (int i = 0; i < cards.Count; i++) cards[i].BorderBrush = i == index ? accent : Brushes.Transparent;
        if (index >= 0 && index < cards.Count)
        {
            AutomationProperties.SetHelpText(cards[index], "選択中。Altを離すと切り替えます。");
            scroller?.ScrollToHorizontalOffset(Math.Max(0, index * 218 - (Width - 48 - 208) / 2));
        }
    }
    private void UpdateThumbnails()
    {
        if (!IsVisible || hwnd == 0 || scroller == null) return;
        double scale = Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0;
        for (int i = 0; i < thumbnails.Count && i < previewHosts.Count; i++)
        {
            nint thumbnail = thumbnails[i]; if (thumbnail == 0) continue;
            var host = previewHosts[i].Preview;
            var pos = host.TranslatePoint(new System.Windows.Point(0, 0), this);
            var clip = scroller.TranslatePoint(new System.Windows.Point(0, 0), this);
            double x = pos.X, y = pos.Y, w = host.ActualWidth, h = host.ActualHeight;
            if (Native.DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0 && size.X > 0 && size.Y > 0)
            {
                double factor = Math.Min(w / size.X, h / size.Y);
                double iw = size.X * factor, ih = size.Y * factor; x += (w - iw) / 2; y += (h - ih) / 2; w = iw; h = ih;
            }
            // Hide partially clipped thumbnails; native DWM content is not clipped by WPF.
            bool visible = pos.X >= clip.X - 1 && pos.X + host.ActualWidth <= clip.X + scroller.ViewportWidth + 1;
            var props = new Native.ThumbnailProperties { Flags = 1 | 4 | 8 | 16, Destination = new Native.Rect { Left = (int)(x * scale), Top = (int)(y * scale), Right = (int)((x + w) * scale), Bottom = (int)((y + h) * scale) }, Opacity = 255, Visible = visible, ClientOnly = true };
            Native.DwmUpdateThumbnailProperties(thumbnail, ref props);
        }
    }
    private void ClearThumbnails() { foreach (nint thumb in thumbnails) if (thumb != 0) Native.DwmUnregisterThumbnail(thumb); thumbnails.Clear(); }
    internal void SavePreview(string path)
    {
        UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
