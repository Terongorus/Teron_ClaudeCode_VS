using Markdig;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;

namespace ClaudeCodeVS.Controls
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
                // documents; without this, content gets clipped/horizontally scrollable
                // inside a narrow tool window instead of wrapping to fit it.
                doc.PagePadding = new Thickness(0);
                doc.ColumnWidth = double.PositiveInfinity;

                return doc;
            }
            catch
            {
                // Malformed markdown (e.g. raw HTML Markdig can't turn into valid XAML) falls
                // back to plain text so one bad chunk can't break the whole chat.
                var doc = new FlowDocument();
                doc.Blocks.Add(new Paragraph(new Run(markdown)));
                return doc;
            }
        }
    }
}
