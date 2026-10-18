using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Vigil.UI;

/// <summary>Just enough Markdown for an assistant's answer: paragraphs, headings,
/// lists, quotes, code, bold, italics, inline code and links. Drawn as a
/// FlowDocument so the text stays selectable.</summary>
public static class Markdown
{
    static readonly Regex Inline = new(@"(\*\*(?<b>.+?)\*\*|__(?<b2>.+?)__|`(?<code>[^`]+)`|\[(?<lt>[^\]]+)\]\((?<lu>[^)\s]+)\)|(?<![\w*])\*(?<i>[^*\n]+)\*(?![\w*])|(?<![\w_])_(?<i2>[^_\n]+)_(?![\w_]))",
                                        RegexOptions.Compiled);

    public static FlowDocument Render(string text, Brush foreground, Brush dim, Brush codeBack, FontFamily sans, FontFamily mono)
    {
        var doc = new FlowDocument
        {
            FontFamily = sans,
            FontSize = 14.5,
            Foreground = foreground,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = 22.5,
        };

        var lines = text.Replace("\r", "").Split('\n');
        var paragraph = new List<string>();
        List? list = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
            AddInlines(p.Inlines, string.Join(" ", paragraph), foreground, dim, codeBack, mono);
            doc.Blocks.Add(p);
            paragraph.Clear();
        }

        void FlushList()
        {
            if (list is null) return;
            doc.Blocks.Add(list);
            list = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```"))
            {
                FlushParagraph(); FlushList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++)
                    code.Add(lines[i]);
                var block = new Paragraph(new Run(string.Join("\n", code)))
                {
                    FontFamily = mono,
                    FontSize = 12.5,
                    LineHeight = 18,
                    Background = codeBack,
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 2, 0, 11),
                    Foreground = foreground,
                };
                doc.Blocks.Add(block);
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph(); FlushList();
                continue;
            }

            var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
            if (heading.Success)
            {
                FlushParagraph(); FlushList();
                var h = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = heading.Groups[1].Length <= 2 ? 16 : 14.5,
                    Margin = new Thickness(0, 6, 0, 6),
                };
                AddInlines(h.Inlines, heading.Groups[2].Value, foreground, dim, codeBack, mono);
                doc.Blocks.Add(h);
                continue;
            }

            var bullet = Regex.Match(line, @"^\s*(?:[-*+•]|(?<n>\d+)[.)])\s+(.*)$");
            if (bullet.Success)
            {
                FlushParagraph();
                var ordered = bullet.Groups["n"].Success;
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != ordered)
                {
                    FlushList();
                    list = new List
                    {
                        MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 9),
                        Padding = new Thickness(20, 0, 0, 0),
                        Foreground = foreground,
                    };
                }
                var item = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                AddInlines(item.Inlines, bullet.Groups[1].Value, foreground, dim, codeBack, mono);
                list.ListItems.Add(new ListItem(item));
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                FlushParagraph(); FlushList();
                var quote = new Paragraph
                {
                    Foreground = dim,
                    BorderBrush = dim,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(10, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 9),
                };
                AddInlines(quote.Inlines, trimmed.TrimStart('>').Trim(), foreground, dim, codeBack, mono);
                doc.Blocks.Add(quote);
                continue;
            }

            FlushList();
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        FlushList();

        if (doc.Blocks.LastBlock is Block last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        return doc;
    }

    static void AddInlines(InlineCollection target, string text, Brush foreground, Brush dim, Brush codeBack, FontFamily mono)
    {
        var at = 0;
        foreach (Match match in Inline.Matches(text))
        {
            if (match.Index > at) target.Add(new Run(text[at..match.Index]));
            if (match.Groups["b"].Success || match.Groups["b2"].Success)
                target.Add(new Bold(new Run(match.Groups["b"].Success ? match.Groups["b"].Value : match.Groups["b2"].Value)));
            else if (match.Groups["code"].Success)
                target.Add(new Run(" " + match.Groups["code"].Value + " ") { FontFamily = mono, FontSize = 12.5, Background = codeBack });
            else if (match.Groups["lt"].Success)
            {
                var url = match.Groups["lu"].Value;
                var link = new Hyperlink(new Run(match.Groups["lt"].Value)) { Foreground = foreground, Cursor = System.Windows.Input.Cursors.Hand };
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    link.NavigateUri = uri;
                    link.ToolTip = url;
                    link.RequestNavigate += (_, e) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception) { }
                    };
                }
                target.Add(link);
            }
            else
                target.Add(new Italic(new Run(match.Groups["i"].Success ? match.Groups["i"].Value : match.Groups["i2"].Value)));
            at = match.Index + match.Length;
        }
        if (at < text.Length) target.Add(new Run(text[at..]));
    }
}
