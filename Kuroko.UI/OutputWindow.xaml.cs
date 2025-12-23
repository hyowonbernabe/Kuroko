using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Kuroko.UI;

public partial class OutputWindow : Window
{
    public OutputWindow()
    {
        InitializeComponent();
    }

    public void SetLoading(bool isLoading)
    {
        MarkdownContainer.Children.Clear();
        if (isLoading)
        {
            MarkdownContainer.Children.Add(new TextBlock
            {
                Text = "Thinking...",
                Foreground = Brushes.Gray,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14
            });
        }
    }

    public void RenderResponse(string markdown)
    {
        MarkdownContainer.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8)
            };

            string cleanLine = line.Trim();
            if (cleanLine.StartsWith("- ") || cleanLine.StartsWith("* "))
            {
                cleanLine = cleanLine.Substring(2);
                textBlock.Text = "• ";
                textBlock.Foreground = Brushes.White;
            }

            var parts = Regex.Split(cleanLine, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                {
                    string content = part.Substring(2, part.Length - 4);
                    textBlock.Inlines.Add(new Run(content) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65)) });
                }
                else
                {
                    textBlock.Inlines.Add(new Run(part));
                }
            }
            MarkdownContainer.Children.Add(textBlock);
        }
    }

    private void BtnHide_Click(object sender, RoutedEventArgs e) => this.Hide();
    private void Header_MouseDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}