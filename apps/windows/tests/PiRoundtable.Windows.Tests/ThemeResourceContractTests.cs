using System.Text.RegularExpressions;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed partial class ThemeResourceContractTests
{
    [TestMethod]
    public void Main_window_uses_theme_resources_instead_of_fixed_rgb_colors()
    {
        var xaml = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "MainWindow.xaml"));

        Assert.IsFalse(HexColorPattern().IsMatch(xaml), "MainWindow.xaml contains a fixed RGB color.");
        StringAssert.Contains(xaml, "SystemFillColorCautionBrush");
        StringAssert.Contains(xaml, "LayerFillColorDefaultBrush");
    }

    [TestMethod]
    public void Markdown_fallback_uses_the_current_system_foreground()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "Controls", "MarkdownMessageView.cs"));

        StringAssert.Contains(source, "UIColorType.Foreground");
        Assert.IsFalse(source.Contains("Microsoft.UI.Colors.Gray", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Markdown_surfaces_avoid_light_only_layer_backgrounds()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "Controls", "MarkdownMessageView.cs"));

        StringAssert.Contains(source, "SubtleFillColorSecondaryBrush");
        Assert.IsFalse(source.Contains("CardBackgroundFillColorDefaultBrush", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("LayerFillColorAltBrush", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains(
            "Foreground = ThemeBrush(\"TextFillColorSecondaryBrush\")",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Window_rechecks_high_contrast_when_it_is_reactivated()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "MainWindow.xaml.cs"));

        StringAssert.Contains(source, "Activated += MainWindow_Activated");
        StringAssert.Contains(source, "ThemePolicy.IsWindowsHighContrastEnabled()");
        StringAssert.Contains(source, "WindowActivationState.Deactivated");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("Unable to locate repository file.", Path.Combine(segments));
    }

    [GeneratedRegex("#[0-9A-Fa-f]{3,8}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorPattern();
}
