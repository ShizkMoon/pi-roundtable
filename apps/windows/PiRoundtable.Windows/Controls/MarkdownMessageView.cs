using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Controls;

public sealed class MarkdownMessageView : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownMessageView),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private DispatcherQueueTimer? _renderTimer;

    public MarkdownMessageView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) => QueueRender();
        Unloaded += (_, _) => _renderTimer?.Stop();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        ((MarkdownMessageView)dependencyObject).QueueRender();
    }

    private void QueueRender()
    {
        if (!IsLoaded || DispatcherQueue is null)
        {
            return;
        }
        _renderTimer ??= CreateRenderTimer();
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private DispatcherQueueTimer CreateRenderTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(80);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Render();
        return timer;
    }

    private void Render()
    {
        var blocks = SafeMarkdownParser.Parse(Text);
        var root = new StackPanel { Spacing = 8 };
        AutomationProperties.SetName(root, "消息正文");
        foreach (var block in blocks)
        {
            root.Children.Add(CreateBlock(block));
        }
        if (blocks.Count == 0)
        {
            root.Children.Add(CreateTextBlock([]));
        }
        Content = root;
    }

    private static UIElement CreateBlock(MarkdownBlockPresentation block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => CreateHeading(block),
        MarkdownBlockKind.ListItem => CreateListItem(block),
        MarkdownBlockKind.Quote => CreateQuote(block),
        MarkdownBlockKind.Code => CreateCodeBlock(block.Text ?? string.Empty),
        MarkdownBlockKind.Math => CreateMathBlock(block.Text ?? string.Empty),
        MarkdownBlockKind.ThematicBreak => CreateDivider(),
        _ => CreateTextBlock(block.Inlines),
    };

    private static TextBlock CreateHeading(MarkdownBlockPresentation block)
    {
        var heading = CreateTextBlock(block.Inlines);
        heading.FontSize = block.Level switch
        {
            <= 1 => 20,
            2 => 18,
            _ => 16,
        };
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 4, 0, 0);
        return heading;
    }

    private static Grid CreateListItem(MarkdownBlockPresentation block)
    {
        var grid = new Grid { Margin = new Thickness(Math.Min(block.Level, 3) * 18, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new TextBlock
        {
            Text = block.Marker ?? "•",
            MinWidth = 22,
            FontSize = 14,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var body = CreateTextBlock(block.Inlines);
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private static Border CreateQuote(MarkdownBlockPresentation block) => new()
    {
        Padding = new Thickness(12, 6, 8, 6),
        BorderThickness = new Thickness(3, 0, 0, 0),
        BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
        Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
        CornerRadius = new CornerRadius(0, 6, 6, 0),
        Child = CreateTextBlock(block.Inlines),
    };

    private static Border CreateCodeBlock(string text)
    {
        var code = new TextBlock
        {
            Text = text.TrimEnd(),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(code, "代码块");
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = code,
            },
        };
    }

    private static Border CreateMathBlock(string text)
    {
        var label = new TextBlock
        {
            Text = "公式 · LaTeX 源码",
            FontSize = 11,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var formula = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cambria Math, Cascadia Mono"),
            FontSize = 14,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(formula, "LaTeX 公式源码");
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(label);
        content.Children.Add(formula);
        return new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Background = ThemeBrush("LayerFillColorAltBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
        };
    }

    private static Border CreateDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 4),
        Background = ThemeBrush("DividerStrokeColorDefaultBrush"),
    };

    private static TextBlock CreateTextBlock(IReadOnlyList<MarkdownInlinePresentation> presentations)
    {
        var textBlock = new TextBlock
        {
            FontSize = 14,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        };
        foreach (var presentation in presentations)
        {
            if (presentation.IsLineBreak)
            {
                textBlock.Inlines.Add(new LineBreak());
                continue;
            }
            var run = new Run
            {
                Text = presentation.Text,
                FontWeight = presentation.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                FontStyle = presentation.Italic
                    ? global::Windows.UI.Text.FontStyle.Italic
                    : global::Windows.UI.Text.FontStyle.Normal,
            };
            if (presentation.Code)
            {
                run.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                run.Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush");
            }
            else if (presentation.Math)
            {
                run.FontFamily = new FontFamily("Cambria Math");
                run.Foreground = ThemeBrush("AccentTextFillColorPrimaryBrush");
            }
            if (presentation.SafeUrl is not null && Uri.TryCreate(presentation.SafeUrl, UriKind.Absolute, out var uri))
            {
                var hyperlink = new Hyperlink { NavigateUri = uri };
                hyperlink.Inlines.Add(run);
                textBlock.Inlines.Add(hyperlink);
            }
            else
            {
                textBlock.Inlines.Add(run);
            }
        }
        return textBlock;
    }

    private static Brush ThemeBrush(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var resource) == true && resource is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
