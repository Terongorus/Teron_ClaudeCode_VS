using Markdig;
using System;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace ClaudeCodeGUI.Controls
{
    public static class MarkdownRenderer
    {
        private static readonly MarkdownPipeline Pipeline =
            new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UsePipeTables()
                .UseTaskLists()
                .UseEmojiAndSmiley()
                .Build();

        // Semi-transparent neutral background for code blocks — reads correctly on both VS light and dark themes.
        private static readonly SolidColorBrush s_codeBg = Frozen(Color.FromArgb(0x18, 0x80, 0x80, 0x80));

        // Diff line colors (same hues as GitHub's diff view).
        private static readonly SolidColorBrush s_diffAdd = Frozen(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush s_diffRem = Frozen(Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D));
        private static readonly SolidColorBrush s_diffHunk = Frozen(Color.FromArgb(0xFF, 0x79, 0xB8, 0xFF));

        private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static FlowDocument Render(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return new FlowDocument();

            try
            {
                string xaml = Markdig.Wpf.Markdown.ToXaml(markdown, Pipeline);

                using var reader = new StringReader(xaml);
                using var xml = System.Xml.XmlReader.Create(reader);

                var doc = (FlowDocument)XamlReader.Load(xml);

                // FlowDocument defaults to a fixed ~768px column width meant for paginated
                // documents; without this, content gets clipped inside a narrow tool window.
                doc.PagePadding = new Thickness(0);
                doc.ColumnWidth = double.PositiveInfinity;

                PostProcess(doc);

                return doc;
            }
            catch
            {
                var doc = new FlowDocument();
                doc.Blocks.Add(new Paragraph(new Run(markdown)));
                return doc;
            }
        }

        // ─── Post-processing ──────────────────────────────────────────────────────

        private static void PostProcess(FlowDocument doc)
        {
            try
            {
                // Markdig.Wpf sets FlowDocument.Foreground="Black" and sometimes a light Background.
                // Clear both at the document root so the FlowDocumentScrollViewer.Foreground binding
                // (which carries the VS theme text color) wins for all text in the document.
                if (IsBlackForeground(doc.Foreground))
                    doc.ClearValue(TextElement.ForegroundProperty);
                if (IsLightBackground(doc.Background))
                    doc.ClearValue(TextElement.BackgroundProperty);

                WalkBlocks(doc.Blocks);
            }
            catch
            {
                // Never break rendering on a post-processing error.
            }
        }

        private static void WalkBlocks(BlockCollection blocks)
        {
            foreach (var block in blocks)
                WalkBlock(block);
        }

        private static void WalkBlock(Block block)
        {
            switch (block)
            {
                case Paragraph para:
                    FixupParagraph(para);
                    break;

                case Section section:
                    if (IsLightBackground(section.Background))
                        section.Background = s_codeBg;
                    if (IsBlackForeground(section.Foreground))
                        section.ClearValue(TextElement.ForegroundProperty);
                    WalkBlocks(section.Blocks);
                    break;

                case List list:
                    foreach (ListItem li in list.ListItems)
                        WalkBlocks(li.Blocks);
                    break;

                case Table table:
                    foreach (var rg in table.RowGroups)
                        foreach (TableRow row in rg.Rows)
                            foreach (TableCell cell in row.Cells)
                                WalkBlocks(cell.Blocks);
                    break;
            }
        }

        private static void FixupParagraph(Paragraph para)
        {
            // Remove any explicitly-set black foreground so VS theme text color is inherited.
            if (IsBlackForeground(para.Foreground))
                para.ClearValue(TextElement.ForegroundProperty);

            // Code blocks from Markdig.Wpf have a light background; make it theme-neutral.
            bool isCodeBlock = IsLightBackground(para.Background);
            if (isCodeBlock)
            {
                para.Background = s_codeBg;
                ApplyDiffColors(para.Inlines);
            }

            // Walk inline containers (Span, Hyperlink, etc.) for nested runs.
            foreach (var inline in para.Inlines.OfType<Span>())
                FixupSpan(inline);
        }

        private static void FixupSpan(Span span)
        {
            if (IsBlackForeground(span.Foreground))
                span.ClearValue(TextElement.ForegroundProperty);

            foreach (var child in span.Inlines.OfType<Span>())
                FixupSpan(child);
        }

        private static void ApplyDiffColors(InlineCollection inlines)
        {
            // Only activate for blocks that actually look like a unified diff.
            bool hasDiff = inlines.OfType<Run>().Any(r =>
                r.Text.StartsWith("+", StringComparison.Ordinal) ||
                r.Text.StartsWith("-", StringComparison.Ordinal));

            if (!hasDiff) return;

            foreach (var run in inlines.OfType<Run>())
            {
                string t = run.Text;
                if (t.StartsWith("+++", StringComparison.Ordinal) || t.StartsWith("---", StringComparison.Ordinal))
                    run.Foreground = s_diffHunk;
                else if (t.StartsWith("+", StringComparison.Ordinal))
                    run.Foreground = s_diffAdd;
                else if (t.StartsWith("-", StringComparison.Ordinal))
                    run.Foreground = s_diffRem;
                else if (t.StartsWith("@@", StringComparison.Ordinal))
                    run.Foreground = s_diffHunk;
            }
        }

        private static bool IsLightBackground(Brush? brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                // Light neutral colors commonly used by Markdig.Wpf for code blocks.
                return c.A > 0x80 && c.R > 0xCC && c.G > 0xCC && c.B > 0xCC;
            }
            return false;
        }

        private static bool IsBlackForeground(Brush? brush)
        {
            // Only clear truly-black (or near-black) explicit foreground values —
            // not coloured runs set by syntax highlighting.
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                return c.A > 0x80 && c.R < 0x30 && c.G < 0x30 && c.B < 0x30;
            }
            return false;
        }
    }
}
