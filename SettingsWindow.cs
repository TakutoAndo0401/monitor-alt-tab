using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MonitorAltTab;

internal sealed class SettingsWindow : Window
{
    internal SettingsWindow(Settings current, Action<Settings> save, Action preview, Action togglePause, Action exit, string status)
    {
        Title = "Monitor Alt Tab — 設定"; Width = 630; Height = 720; MinWidth = 520; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new FontFamily("Segoe UI Variable, Yu Gothic UI");
        bool light = Palette.Light(current);
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Palette.Brush(light ? "#F3F3F3" : "#202020");
        Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Palette.Brush(light ? "#202020" : "#F5F5F5");
        var root = new StackPanel { Margin = new Thickness(30, 26, 30, 24) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(Palette.Text("Monitor Alt Tab", 27, Foreground, FontWeights.SemiBold));
        var subtitle = Palette.Text(status, 13, Foreground); subtitle.Margin = new Thickness(0, 8, 0, 22); subtitle.TextWrapping = TextWrapping.Wrap; root.Children.Add(subtitle);
        void Heading(string text) { var t = Palette.Text(text, 16, Foreground, FontWeights.SemiBold); t.Margin = new Thickness(0, 16, 0, 10); root.Children.Add(t); }
        CheckBox Check(string text, bool check) { var box = new CheckBox { Content = text, IsChecked = check, Margin = new Thickness(0, 7, 0, 7), Foreground = Foreground, FontSize = 14 }; root.Children.Add(box); return box; }
        Heading("切り替え");
        var pointer = Check("マウスポインターがあるモニターを対象にする", current.UsePointerMonitor);
        root.Children.Add(Palette.Text("オフの場合は、操作中のウィンドウがあるモニターを使用", 12, Foreground));
        var fullscreen = Check("全画面アプリの操作中は標準のAlt＋Tabを使う", current.ExcludeFullscreen);
        Heading("外観");
        var previews = Check("ウィンドウのライブプレビューを表示する", current.ShowPreviews);
        var themes = new ComboBox { ItemsSource = new[] { "Windowsに合わせる", "ライト", "ダーク" }, SelectedIndex = current.Theme == "light" ? 1 : current.Theme == "dark" ? 2 : 0, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8), MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(themes);
        Heading("除外アプリ");
        root.Children.Add(Palette.Text("実行ファイル名を1行に1つ（例：mstsc.exe）", 12, Foreground));
        var exclusions = new TextBox { Text = string.Join(Environment.NewLine, current.ExcludedApps), AcceptsReturn = true, Height = 74, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) };
        root.Children.Add(exclusions);
        var privacy = Palette.Text("通信・入力履歴の保存なし。設定はアプリの隣に保存します。\n自動起動は設定していません。Ctrl＋Alt＋F12で一時停止できます。", 12, Foreground); privacy.TextWrapping = TextWrapping.Wrap; privacy.Margin = new Thickness(0, 22, 0, 16); root.Children.Add(privacy);
        var actions = new WrapPanel(); root.Children.Add(actions);
        void Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 8, 8) }; b.Click += (_, _) => action(); actions.Children.Add(b); }
        Button("保存", () =>
        {
            var next = new Settings { UsePointerMonitor = pointer.IsChecked == true, ShowPreviews = previews.IsChecked == true, ExcludeFullscreen = fullscreen.IsChecked == true, Theme = themes.SelectedIndex == 1 ? "light" : themes.SelectedIndex == 2 ? "dark" : "system", ExcludedApps = exclusions.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray() };
            try { next.Save(); save(next); Close(); }
            catch (Exception) { MessageBox.Show(this, "設定を保存できませんでした。アプリを置いたフォルダーへの書き込み権限を確認してください。", "設定", MessageBoxButton.OK, MessageBoxImage.Warning); }
        });
        Button("表示を試す", preview); Button("一時停止／再開", togglePause); Button("アプリを終了", exit);
    }
}
