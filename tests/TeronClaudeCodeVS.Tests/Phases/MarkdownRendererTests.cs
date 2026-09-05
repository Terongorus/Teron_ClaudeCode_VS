using System.Linq;
using System.Windows.Documents;
using System.Windows.Media;
using TeronClaudeCodeVS.Controls;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Regression coverage for a real bug found live 2026-09-05: a tool-call card showing a
    /// command followed by its output ("Run command" + "Output:") rendered the command's code
    /// block with the fixed-up accent highlight but left the output's code block on Markdig.Wpf's
    /// raw, unfixed light-grey default - reported as "the output field STILL uses the old ugly
    /// highlight" right after the highlight color itself had just been fixed.
    /// <para>
    /// Root cause: every Block/Inline in a FlowDocument shares one underlying TextContainer, so
    /// inserting a copy-button Floater into an earlier sibling paragraph's Inlines
    /// (<c>MarkdownRenderer.AddCopyAffordance</c>) bumped a version counter that invalidated
    /// <c>WalkBlocks</c>' own live <c>foreach</c> over the outer <c>BlockCollection</c> - "Collection
    /// was modified" on the next block. <c>PostProcess</c>'s broad <c>catch</c> swallowed it
    /// silently, so post-processing quietly stopped after the first code block in ANY multi-block
    /// document, for as long as the copy-button feature has existed. These tests assert every
    /// top-level code block gets the same treatment, not just the first.
    /// </para>
    /// </summary>
    public sealed class MarkdownRendererTests
    {
        [Fact]
        public void A_command_followed_by_its_output_colors_both_code_blocks_the_same_way()
        {
            Sta.Run(() =>
            {
                // Exactly the shape ToolPresentation.GetDetailMarkdown produces for a Bash tool
                // call: a ```bash command block, then a **Output:** heading and a plain ```` block.
                string? markdown = ToolPresentation.GetDetailMarkdown(
                    "Bash",
                    new Newtonsoft.Json.Linq.JObject { ["command"] = "grep -n \"#43\" foo.md" },
                    output: "3:description: \"contains [#43](https://example.com/43)\"",
                    isError: false);

                Assert.NotNull(markdown);

                FlowDocument doc = MarkdownRenderer.Render(markdown!);

                var codeParagraphs = doc.Blocks.OfType<Paragraph>()
                    .Where(p => p.Background is SolidColorBrush { Color.A: > 0 })
                    .ToList();

                Assert.Equal(2, codeParagraphs.Count);

                var colors = codeParagraphs
                    .Select(p => ((SolidColorBrush)p.Background).Color)
                    .Distinct()
                    .ToList();

                Assert.Single(colors); // both blocks got the same fixed-up brush, not one fixed and one left raw
                Assert.NotEqual(Color.FromArgb(0xFF, 0xD3, 0xD3, 0xD3), colors[0]); // not Markdig.Wpf's raw default
            });
        }

        [Fact]
        public void Three_sequential_fenced_code_blocks_all_get_fixed_up()
        {
            Sta.Run(() =>
            {
                string markdown =
                    "```bash\ncmd one\n```\n\n" +
                    "```bash\ncmd two\n```\n\n" +
                    "```bash\ncmd three\n```\n";

                FlowDocument doc = MarkdownRenderer.Render(markdown);

                var backgrounds = doc.Blocks.OfType<Paragraph>()
                    .Select(p => p.Background as SolidColorBrush)
                    .ToList();

                Assert.Equal(3, backgrounds.Count);
                Assert.All(backgrounds, b => Assert.NotNull(b));
                Assert.Single(backgrounds.Select(b => b!.Color).Distinct()); // all three match - none silently skipped
            });
        }
    }
}
