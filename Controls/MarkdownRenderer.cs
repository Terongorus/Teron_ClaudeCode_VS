using Markdig;
using System;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace TeronClaudeCodeVS.Controls
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
        private static readonly FontFamily s_inlineCodeFont = new("Consolas");

        // Diff line colors (same hues as GitHub's diff view).
        private static readonly SolidColorBrush s_diffAdd = Frozen(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush s_diffRem = Frozen(Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D));
        private static readonly SolidColorBrush s_diffHunk = Frozen(Color.FromArgb(0xFF, 0x79, 0xB8, 0xFF));

        private static SolidColorBrush Frozen(Color c) { SolidColorBrush b = new(c); b.Freeze(); return b; }

        public static FlowDocument Render(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return new FlowDocument();

            try
            {
                string xaml = Markdig.Wpf.Markdown.ToXaml(markdown, Pipeline);

                using StringReader reader = new(xaml);
                using XmlReader xml = System.Xml.XmlReader.Create(reader);

                FlowDocument doc = (FlowDocument)XamlReader.Load(xml);

                // FlowDocument defaults to a fixed ~768px column width meant for paginated
                // documents; without this, content gets clipped inside a narrow tool window.
                doc.PagePadding = new Thickness(0);
                doc.ColumnWidth = double.PositiveInfinity;

                PostProcess(doc);

                return doc;
            }
            catch
            {
                FlowDocument doc = new();
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
                AddCopyAffordance(para);
            }
            else
            {
                // Turns a sent "@path#Lstart-Lend" reference (written by
                // ClaudeCodeChatControl.InsertContextReference) into a clickable link back to that
                // file/line - never inside a fenced code block, where the same "@word" shape shows
                // up constantly as a real decorator (@property, @Override, @Injectable()) rather
                // than a file mention.
                LinkifyFileReferences(para);
            }

            // Inline `code` spans can come through as a bare Run with its own Background rather
            // than wrapped in a Span - normalize those directly (see FixupSpan for why).
            foreach (var run in para.Inlines.OfType<Run>())
            {
                if (IsLightBackground(run.Background))
                {
                    run.Background = s_codeBg;
                    run.FontFamily = s_inlineCodeFont;
                }
            }

            // Walk inline containers (Span, Hyperlink, etc.) for nested runs.
            foreach (var inline in para.Inlines.OfType<Span>())
                FixupSpan(inline);
        }

        /// <summary>
        /// UX-8: gives each fenced code block its own copy button, as baseline does. The only
        /// affordance we had was a single global "Copy Raw Output", which copies the entire
        /// transcript - useless when the user wants one command out of a long answer.
        /// <para>
        /// A <see cref="Floater"/> is the FlowDocument-native way to park a control at the right
        /// edge of a block; an InlineUIContainer would sit in the text flow and push the first
        /// line of code sideways. The block's text is snapshotted at build time because the
        /// document is rebuilt from scratch on every streaming update, so a stale closure is not
        /// possible.
        /// </para>
        /// </summary>
        private static void AddCopyAffordance(Paragraph para)
        {
            try
            {
                if (para.Inlines.FirstInline == null) return;

                // Snapshot before inserting the floater, so the button's own label is not copied.
                string code = new TextRange(para.ContentStart, para.ContentEnd).Text;
                if (string.IsNullOrWhiteSpace(code)) return;

                Button button = new()
                {
                    Content = "Copy",
                    FontSize = 10,
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Opacity = 0.55,
                    ToolTip = "Copy this code block",
                    Focusable = false,
                };
                button.SetResourceReference(Control.ForegroundProperty,
                    Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowTextKey);

                button.MouseEnter += (_, __) => button.Opacity = 1.0;
                button.MouseLeave += (_, __) => button.Opacity = 0.55;
                button.Click += (_, __) => CopyToClipboard(button, code);

                Floater floater = new(new BlockUIContainer(button)
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(0),
                })
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Width = 46,
                    Margin = new Thickness(0),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                };

                para.Inlines.InsertBefore(para.Inlines.FirstInline, floater);
            }
            catch
            {
                // A missing copy button must never cost the user the code block itself.
            }
        }

        private static void CopyToClipboard(Button button, string code)
        {
            try
            {
                Clipboard.SetText(code);
                button.Content = "Copied";

                // Revert the label so the button does not read "Copied" forever on a block the
                // user copied ten minutes ago.
                DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
                timer.Tick += (_, __) => { timer.Stop(); button.Content = "Copy"; };
                timer.Start();
            }
            catch
            {
                // Another process can hold the clipboard open; say so rather than failing silently.
                button.Content = "Failed";
            }
        }

        // Requires a real extension on the path segment (Class1.cs, src/Foo/Bar.tsx) so it never
        // matches a bare "@word" - the shape of a code decorator (@property, @Override,
        // @Injectable()) or an @-mention, neither of which is a file reference.
        private static readonly Regex s_fileRefPattern = new(
            @"@(?<path>(?:[\w.\-]+[/\\])*[\w.\-]+\.[A-Za-z0-9]{1,10})(?:#L(?<start>\d+)(?:-L(?<end>\d+))?)?",
            RegexOptions.Compiled);

        /// <summary>
        /// UX. Turns every "@path#Lstart-Lend" token in a plain-text run into a clickable link,
        /// so a reference the composer wrote (see ClaudeCodeChatControl.InsertContextReference)
        /// can be followed back to the file/line it named once the message has been sent, instead
        /// of only ever being read as inert text.
        /// </summary>
        private static void LinkifyFileReferences(Paragraph para)
        {
            foreach (Run run in para.Inlines.OfType<Run>().ToList())
            {
                // Inline `code` spans are still on their original Markdig light background at
                // this point (the fixup loop that neutralizes it runs after this one) - skip
                // them, since matching inside real code is exactly what the regex is guarding
                // against by requiring an extension.
                if (IsLightBackground(run.Background)) continue;
                LinkifyRun(para.Inlines, run);
            }
        }

        private static void LinkifyRun(InlineCollection inlines, Run run)
        {
            string text = run.Text;
            MatchCollection matches = s_fileRefPattern.Matches(text);
            if (matches.Count == 0) return;

            Inline anchor = run;
            int last = 0;

            foreach (Match m in matches)
            {
                if (m.Index > last)
                    inlines.InsertAfter(anchor, anchor = new Run(text.Substring(last, m.Index - last)));

                string path = m.Groups["path"].Value;
                int? start = m.Groups["start"].Success ? int.Parse(m.Groups["start"].Value) : null;
                int? end = m.Groups["end"].Success ? int.Parse(m.Groups["end"].Value) : start;

                Hyperlink link = new(new Run(m.Value))
                {
                    ToolTip = start.HasValue ? $"Open {path} at line {start}" : $"Open {path}",
                };
                // Fire-and-forget rather than an async lambda: OpenReferenceAsync already
                // try/catches its entire body, so nothing here can throw unobserved.
                link.Click += (_, __) => _ = OpenReferenceAsync(path, start, end);
                inlines.InsertAfter(anchor, anchor = link);

                last = m.Index + m.Length;
            }

            if (last < text.Length)
                inlines.InsertAfter(anchor, new Run(text.Substring(last)));

            inlines.Remove(run);
        }

        private static async System.Threading.Tasks.Task OpenReferenceAsync(string path, int? startLine, int? endLine)
        {
            try
            {
                string resolved = path;
                if (!Path.IsPathRooted(resolved))
                {
                    string root = await TeronClaudeCodeVS.Core.VsIdeToolHandlers.GetWorkingDirectoryAsync();
                    resolved = Path.Combine(root, resolved);
                }

                await TeronClaudeCodeVS.Core.VsIdeToolHandlers.OpenFileAtLineAsync(resolved, startLine, endLine);
            }
            catch
            {
                // The file may have moved or been deleted since the message was sent - a broken
                // reference is not worth interrupting the user over.
            }
        }

        private static void FixupSpan(Span span)
        {
            if (IsBlackForeground(span.Foreground))
                span.ClearValue(TextElement.ForegroundProperty);

            // Markdig.Wpf renders inline `code` spans with a light background sized for a white
            // page; left as-is it shows as a stark, undifferentiated light block in a dark theme
            // instead of a subtle inline-code chip. Same theme-neutral tint as fenced code blocks,
            // so it reads correctly in both themes automatically instead of needing its own
            // hardcoded color.
            if (IsLightBackground(span.Background))
            {
                span.Background = s_codeBg;
                span.FontFamily = s_inlineCodeFont;
            }

            foreach (var run in span.Inlines.OfType<Run>())
            {
                if (IsLightBackground(run.Background))
                {
                    run.Background = s_codeBg;
                    run.FontFamily = s_inlineCodeFont;
                }
            }

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
