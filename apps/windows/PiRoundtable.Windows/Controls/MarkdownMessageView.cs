using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PiRoundtable.Windows.Models;
using Windows.ApplicationModel.DataTransfer;

namespace PiRoundtable.Windows.Controls;

public sealed class ExternalLinkRequestedEventArgs(Uri uri) : EventArgs
{
    public Uri Uri { get; } = uri;
}

public sealed class MarkdownMessageView : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarkdownMessageView),
        new PropertyMetadata(string.Empty, OnTextChanged));

    private DispatcherQueueTimer? _renderTimer;
    private string? _renderedText;

    public event EventHandler<ExternalLinkRequestedEventArgs>? ExternalLinkRequested;

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
        var text = Text ?? string.Empty;
        if (string.Equals(_renderedText, text, StringComparison.Ordinal) && Content is not null)
        {
            return;
        }
        var blocks = SafeMarkdownParser.Parse(text);
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
        _renderedText = text;
    }

    private UIElement CreateBlock(MarkdownBlockPresentation block) => block.Kind switch
    {
        MarkdownBlockKind.Heading => CreateHeading(block),
        MarkdownBlockKind.ListItem => CreateListItem(block),
        MarkdownBlockKind.Quote => CreateQuote(block),
        MarkdownBlockKind.Code => CreateCodeBlock(block.Text ?? string.Empty, block.Language),
        MarkdownBlockKind.Math => CreateMathBlock(block.Text ?? string.Empty),
        MarkdownBlockKind.Table => CreateTable(block.Rows ?? []),
        MarkdownBlockKind.ThematicBreak => CreateDivider(),
        _ => CreateTextBlock(block.Inlines),
    };

    private TextBlock CreateHeading(MarkdownBlockPresentation block)
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

    private Grid CreateListItem(MarkdownBlockPresentation block)
    {
        var grid = new Grid { Margin = new Thickness(Math.Min(block.Level, 3) * 18, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new TextBlock
        {
            Text = block.Marker ?? "•",
            MinWidth = 22,
            FontSize = 14,
            Opacity = 0.72,
        };
        var body = CreateTextBlock(block.Inlines);
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private Border CreateQuote(MarkdownBlockPresentation block) => new()
    {
        Padding = new Thickness(12, 6, 8, 6),
        BorderThickness = new Thickness(3, 0, 0, 0),
        BorderBrush = ThemeBrush("AccentFillColorDefaultBrush"),
        Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
        CornerRadius = new CornerRadius(0, 6, 6, 0),
        Child = CreateTextBlock(block.Inlines),
    };

    private static Border CreateCodeBlock(string text, string? language)
    {
        var normalizedText = text.TrimEnd();
        var code = new TextBlock
        {
            Text = normalizedText,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(code, "代码块");
        var header = CreateBlockHeader(
            string.IsNullOrWhiteSpace(language) ? "代码" : $"代码 · {language}",
            normalizedText,
            "复制代码");
        var content = new StackPanel { Spacing = 7 };
        content.Children.Add(header);
        content.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = code,
        });
        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
        };
    }

    private static Border CreateMathBlock(string text)
    {
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
        content.Children.Add(CreateBlockHeader("公式 · LaTeX 源码", text, "复制公式源码"));
        content.Children.Add(formula);
        return new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
        };
    }

    private Border CreateTable(IReadOnlyList<MarkdownTableRowPresentation> rows)
    {
        var table = new Grid { RowSpacing = 1, ColumnSpacing = 1 };
        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Count);
        for (var column = 0; column < columnCount; column++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto,
                MinWidth = 96,
                MaxWidth = 360,
            });
        }
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                var cellText = CreateTextBlock(row.Cells[columnIndex]);
                if (row.IsHeader)
                {
                    cellText.FontWeight = FontWeights.SemiBold;
                }
                var cell = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                    Child = cellText,
                };
                Grid.SetRow(cell, rowIndex);
                Grid.SetColumn(cell, columnIndex);
                table.Children.Add(cell);
            }
        }
        AutomationProperties.SetName(table, "Markdown 表格");
        return new Border
        {
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = table,
            },
        };
    }

    private static Grid CreateBlockHeader(string labelText, string copyText, string automationName)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = labelText,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
        });
        var copyButton = new Button
        {
            Content = "复制",
            MinHeight = 28,
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
        };
        AutomationProperties.SetName(copyButton, automationName);
        copyButton.Click += (_, _) => CopyText(copyButton, copyText);
        Grid.SetColumn(copyButton, 1);
        header.Children.Add(copyButton);
        return header;
    }

    private static void CopyText(Button button, string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            button.Content = "已复制";
            AutomationProperties.SetHelpText(button, "内容已复制到剪贴板");
        }
        catch
        {
            button.Content = "复制失败";
            AutomationProperties.SetHelpText(button, "剪贴板当前不可用；仍可选择源码手动复制");
        }
    }

    private static Border CreateDivider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 4),
        Background = ThemeBrush("DividerStrokeColorDefaultBrush"),
    };

    private TextBlock CreateTextBlock(IReadOnlyList<MarkdownInlinePresentation> presentations)
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
            if (presentation.Strikethrough)
            {
                run.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
            }
            if (presentation.SafeUrl is not null && Uri.TryCreate(presentation.SafeUrl, UriKind.Absolute, out var uri))
            {
                var hyperlink = new Hyperlink();
                hyperlink.Click += (_, _) => ExternalLinkRequested?.Invoke(
                    this,
                    new ExternalLinkRequestedEventArgs(uri));
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
        if (!string.Equals(key, "TextFillColorPrimaryBrush", StringComparison.Ordinal) &&
            Application.Current?.Resources.TryGetValue("TextFillColorPrimaryBrush", out var fallback) == true &&
            fallback is Brush fallbackBrush)
        {
            return fallbackBrush;
        }
        var systemForeground = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(
            global::Windows.UI.ViewManagement.UIColorType.Foreground);
        return new SolidColorBrush(systemForeground);
    }
}
