using Markdig;
using System.Windows.Documents;
using System.Windows.Markup;
using System.IO;

namespace Antigravity_CLI_GUI.Core
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
            string xaml = Markdig.Wpf.Markdown.ToXaml(markdown, Pipeline);

            using var reader = new StringReader(xaml);
            using var xml = System.Xml.XmlReader.Create(reader);

            return (FlowDocument)XamlReader.Load(xml);
        }
    }
}
