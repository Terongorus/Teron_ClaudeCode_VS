using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Protocol;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase H (FEAT-6 web context, FEAT-7 model fallback), ported from
    /// <c>comparison-audit/scripts/phase-h-unit.ps1</c>.
    /// <para>
    /// The FEAT-7 fixtures are not invented. The four <c>system</c> subtypes, their field names,
    /// their trigger vocabulary and the exact sentences they carry were read out of the shipped CLI
    /// binary (v2.1.251) on 2026-08-30 - schemas plus the message builders themselves. See the
    /// original script for the annotated reference.
    /// </para>
    /// <para>
    /// Rigor rule #6 throughout: every assertion that something is null, empty or ignored is paired
    /// with one showing the same code path accepts what it should.
    /// </para>
    /// </summary>
    public sealed class WebContextAndFallbackTests
    {
        private static string? Compose(string? input) => WebContextComposer.Compose(input);

        private static string? Url(string text) =>
            Reflect.StaticCall<string>(typeof(WebContextComposer), "TryNormalizeUrl", text);

        // ─── FEAT-6: a URL is recognised and fetched ────────────────────────────────────────────

        [Theory]
        [InlineData("https://docs.claude.com/en/docs/mcp",
            "Read https://docs.claude.com/en/docs/mcp and use it as context for this conversation.")]
        [InlineData("http://localhost:3000/health",
            "Read http://localhost:3000/health and use it as context for this conversation.")]
        [InlineData("docs.claude.com/en/docs/mcp",
            "Read https://docs.claude.com/en/docs/mcp and use it as context for this conversation.")]
        [InlineData("   https://example.com/a   ",
            "Read https://example.com/a and use it as context for this conversation.")]
        [InlineData("https://example.com/s?q=a&b=c#frag",
            "Read https://example.com/s?q=a&b=c#frag and use it as context for this conversation.")]
        public void A_url_is_composed_as_a_read_request(string typed, string expected)
        {
            Assert.Equal(expected, Compose(typed));
        }

        // ─── FEAT-6: anything else is searched for ──────────────────────────────────────────────

        [Fact]
        public void A_multi_word_phrase_becomes_a_search()
        {
            Assert.Equal(
                "Search the web for \"claude code pricing 2026\" and use the results as context for this conversation.",
                Compose("claude code pricing 2026"));
        }

        [Fact]
        public void A_single_word_with_no_dot_is_a_search_not_a_host()
        {
            Assert.Equal(
                "Search the web for \"kubernetes\" and use the results as context for this conversation.",
                Compose("kubernetes"));
        }

        [Fact]
        public void A_phrase_containing_a_dotted_word_is_still_a_search_because_it_has_spaces()
        {
            Assert.Equal(
                "Search the web for \"what is example.com used for\" and use the results as context for this conversation.",
                Compose("what is example.com used for"));
        }

        [Fact]
        public void Double_quotes_in_the_terms_are_re_quoted_so_the_span_cannot_close_early()
        {
            Assert.Equal(
                "Search the web for \"the 'big' one\" and use the results as context for this conversation.",
                Compose("the \"big\" one"));
        }

        // ─── FEAT-6: nothing typed produces nothing ─────────────────────────────────────────────

        [Fact]
        public void Empty_or_whitespace_only_input_composes_nothing()
        {
            Assert.Null(Compose(""));
            Assert.Null(Compose("  \t  "));

            // CONTROL: one character is enough to compose something.
            Assert.NotNull(Compose("x"));
        }

        // ─── FEAT-6: the URL test is narrow on purpose ──────────────────────────────────────────

        [Theory]
        [InlineData("file:///c:/temp/x.txt")]
        [InlineData("mailto:someone@example.com")]
        [InlineData("kubernetes")]
        [InlineData("example.")]
        [InlineData(".com")]
        [InlineData("/usr/local/bin")]
        [InlineData(@"C:\temp\notes.md")]
        public void These_are_not_treated_as_a_host(string text)
        {
            Assert.Null(Url(text));
        }

        [Fact]
        public void A_real_host_and_a_real_absolute_url_are_accepted()
        {
            Assert.Equal("https://example.com", Url("example.com"));
            Assert.Equal("https://example.com", Url("https://example.com"));
        }

        [Fact]
        public void A_relative_file_path_with_an_extension_is_not_turned_into_a_url()
        {
            // The case most likely to be mis-read as a host.
            Assert.StartsWith("Search the web for", Compose("src/Program.cs")!);
        }

        // ─── FEAT-7: the four subtypes the CLI actually emits ───────────────────────────────────

        private const string Overloaded =
            "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"trigger\":\"overloaded\"," +
            "\"original_model\":\"claude-opus-4-5-20251101\",\"fallback_model\":\"claude-haiku-4-5-20251001\"," +
            "\"content\":\"Switched to claude-haiku-4-5-20251001 due to high demand for claude-opus-4-5-20251101\"," +
            "\"uuid\":\"6d0f2f6e-1f7b-4a2e-9c6a-9d3f1e2b7c40\",\"session_id\":\"s1\"}";

        [Fact]
        public void Model_fallback_parses_with_every_field_carried()
        {
            var m = Assert.IsType<ModelFallbackEvent>(ClaudeMessage.Parse(Overloaded));

            Assert.Equal("model_fallback", m.Subtype);
            Assert.Equal("overloaded", m.Trigger);
            Assert.Equal("claude-opus-4-5-20251101", m.OriginalModel);
            Assert.Equal("claude-haiku-4-5-20251001", m.FallbackModel);
            Assert.Equal(
                "Switched to claude-haiku-4-5-20251001 due to high demand for claude-opus-4-5-20251101",
                m.NoticeText);
            Assert.False(m.IsFailure, "a successful switch is not an error");
        }

        [Fact]
        public void The_model_not_found_trigger_parses_with_its_own_wording()
        {
            const string json =
                "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"trigger\":\"model_not_found\"," +
                "\"original_model\":\"claude-opus-3\",\"fallback_model\":\"sonnet\"," +
                "\"content\":\"Switched to sonnet because claude-opus-3 is not available\"," +
                "\"uuid\":\"u\",\"session_id\":\"s\"}";

            var m = Assert.IsType<ModelFallbackEvent>(ClaudeMessage.Parse(json));

            Assert.Equal("model_not_found", m.Trigger);
            Assert.Equal("Switched to sonnet because claude-opus-3 is not available", m.NoticeText);
        }

        [Fact]
        public void The_last_resort_trigger_keeps_its_parenthesised_detail_intact()
        {
            const string json =
                "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"trigger\":\"last_resort\"," +
                "\"original_model\":\"opus\",\"fallback_model\":\"haiku\"," +
                "\"content\":\"Switched to haiku because opus returned an error that could not be retried (503 upstream)\"," +
                "\"uuid\":\"u\",\"session_id\":\"s\"}";

            var m = ClaudeMessage.Parse(json) as ModelFallbackEvent;

            Assert.Equal(
                "Switched to haiku because opus returned an error that could not be retried (503 upstream)",
                m?.NoticeText);
        }

        private const string Consent =
            "{\"type\":\"system\",\"subtype\":\"model_consent_fallback\",\"choice\":\"consent\"," +
            "\"original_model\":\"claude-opus-4-5-20251101\",\"fallback_model\":\"claude-haiku-4-5-20251001\"," +
            "\"persisted_as_default\":false," +
            "\"content\":\"Switched to claude-haiku-4-5-20251001 for this session · claude-opus-4-5-20251101 requires usage credits · /model to change\"," +
            "\"uuid\":\"u\",\"session_id\":\"s\"}";

        [Fact]
        public void Model_consent_fallback_parses_and_keeps_the_middot_sentence_verbatim()
        {
            var m = Assert.IsType<ModelFallbackEvent>(ClaudeMessage.Parse(Consent));

            Assert.Equal("model_consent_fallback", m.Subtype);
            Assert.StartsWith("Switched to claude-haiku-4-5-20251001", m.NoticeText);
            Assert.EndsWith("· claude-opus-4-5-20251101 requires usage credits · /model to change", m.NoticeText);
            Assert.False(m.IsFailure, "a consented switch is not an error either");
        }

        private const string RefusalSession =
            "{\"type\":\"system\",\"subtype\":\"model_refusal_fallback\",\"trigger\":\"refusal\"," +
            "\"direction\":\"retry\",\"scope\":\"session\",\"original_model\":\"opus\",\"fallback_model\":\"sonnet\"," +
            "\"request_id\":\"req_1\",\"api_refusal_category\":\"cyber\"," +
            "\"content\":\"Switched to sonnet. This response was generated by sonnet instead.\"," +
            "\"uuid\":\"u\",\"session_id\":\"s\"}";

        [Fact]
        public void Model_refusal_fallback_parses_with_its_scope_and_trigger()
        {
            var m = Assert.IsType<ModelFallbackEvent>(ClaudeMessage.Parse(RefusalSession));

            Assert.Equal("model_refusal_fallback", m.Subtype);
            Assert.Equal("session", m.Scope);
            Assert.Equal("refusal", m.Trigger);
            Assert.False(m.IsFailure, "a refusal that was rescued is not an error");
        }

        [Fact]
        public void The_local_scope_is_carried_too()
        {
            const string json =
                "{\"type\":\"system\",\"subtype\":\"model_refusal_fallback\",\"trigger\":\"refusal\"," +
                "\"direction\":\"retry\",\"scope\":\"local\",\"original_model\":\"opus\",\"fallback_model\":\"sonnet\"," +
                "\"content\":\"Switched to sonnet for this response.\",\"uuid\":\"u\",\"session_id\":\"s\"}";

            Assert.Equal("local", (ClaudeMessage.Parse(json) as ModelFallbackEvent)?.Scope);
        }

        private const string NoFallback =
            "{\"type\":\"system\",\"subtype\":\"model_refusal_no_fallback\",\"original_model\":\"opus\"," +
            "\"request_id\":null,\"api_refusal_category\":\"cyber\"," +
            "\"content\":\"opus declined this request and no fallback model is configured.\"," +
            "\"uuid\":\"u\",\"session_id\":\"s\"}";

        [Fact]
        public void Model_refusal_no_fallback_is_the_one_subtype_flagged_as_a_failure()
        {
            var m = Assert.IsType<ModelFallbackEvent>(ClaudeMessage.Parse(NoFallback));

            Assert.Equal("model_refusal_no_fallback", m.Subtype);
            Assert.True(string.IsNullOrEmpty(m.FallbackModel), "it has no fallback model, by definition");
            Assert.True(m.IsFailure);

            // CONTROL: the other three subtypes are not flagged as failures.
            Assert.False(((ModelFallbackEvent)ClaudeMessage.Parse(Overloaded)!).IsFailure);
            Assert.False(((ModelFallbackEvent)ClaudeMessage.Parse(Consent)!).IsFailure);
            Assert.False(((ModelFallbackEvent)ClaudeMessage.Parse(RefusalSession)!).IsFailure);
        }

        // ─── FEAT-7: older CLIs, and lines that say nothing ─────────────────────────────────────

        [Fact]
        public void A_subtype_with_no_content_rebuilds_a_notice_from_the_two_models()
        {
            const string json =
                "{\"type\":\"system\",\"subtype\":\"model_fallback\",\"trigger\":\"overloaded\"," +
                "\"original_model\":\"opus\",\"fallback_model\":\"haiku\",\"uuid\":\"u\",\"session_id\":\"s\"}";

            var m = ClaudeMessage.Parse(json) as ModelFallbackEvent;

            Assert.NotNull(m);
            Assert.Equal("Switched to haiku from opus", m!.NoticeText);
        }

        [Fact]
        public void A_line_with_neither_a_sentence_nor_a_model_is_dropped()
        {
            const string json = "{\"type\":\"system\",\"subtype\":\"model_refusal_no_fallback\",\"uuid\":\"u\",\"session_id\":\"s\"}";
            Assert.Null(ClaudeMessage.Parse(json));
        }

        [Fact]
        public void One_model_is_enough_to_keep_the_line_and_it_still_says_something_actionable()
        {
            const string json =
                "{\"type\":\"system\",\"subtype\":\"model_refusal_no_fallback\",\"original_model\":\"opus\"," +
                "\"uuid\":\"u\",\"session_id\":\"s\"}";

            var m = ClaudeMessage.Parse(json) as ModelFallbackEvent;

            Assert.NotNull(m);
            Assert.Equal("opus refused this turn and no fallback model is configured", m!.NoticeText);
        }

        // ─── FEAT-7: the parser is not over-eager ───────────────────────────────────────────────

        [Fact]
        public void Unrelated_or_merely_similar_system_subtypes_are_ignored()
        {
            Assert.Null(ClaudeMessage.Parse("{\"type\":\"system\",\"subtype\":\"permission_denied\",\"tool\":\"Edit\"}"));
            Assert.Null(ClaudeMessage.Parse(
                "{\"type\":\"system\",\"subtype\":\"compact_no_model_fallback_env\",\"content\":\"x\"}"));
        }

        [Fact]
        public void Init_and_compact_boundary_still_parse_as_before()
        {
            var init = ClaudeMessage.Parse(
                "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"model\":\"sonnet\"," +
                "\"permissionMode\":\"acceptEdits\",\"cwd\":\"c:\\\\x\",\"slash_commands\":[\"help\"]}");
            Assert.IsType<InitMessage>(init);

            var compact = ClaudeMessage.Parse(
                "{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":" +
                "{\"trigger\":\"manual\",\"pre_tokens\":10,\"post_tokens\":2,\"cumulative_dropped_tokens\":8}}");
            Assert.IsType<CompactBoundaryEvent>(compact);
        }

        // ─── FEAT-7: the flag is only emitted when it means something ──────────────────────────

        [Fact]
        public void ClaudeSessionStartOptions_carries_a_fallback_model()
        {
            Assert.NotNull(typeof(ClaudeSessionStartOptions).GetProperty("FallbackModel"));
        }

        [Fact]
        public void The_options_page_declares_the_two_settings_correctly_categorised_and_described()
        {
            // ClaudeCodeOptionsPage is a DialogPage: its constructor needs a live VS service
            // provider, so it cannot be instantiated here. Its DEFAULTS are checked live instead
            // (phase-h-live.ps1 / the Phase L re-verification). What is checkable headlessly is
            // that the two properties exist, are the right type, and are surfaced on the page.
            Type pageType = typeof(ClaudeCodeOptionsPage);

            PropertyInfo? toggle = pageType.GetProperty("SwitchModelsAutomatically");
            PropertyInfo? target = pageType.GetProperty("FallbackModel");

            Assert.NotNull(toggle);
            Assert.Equal(typeof(bool), toggle!.PropertyType);
            Assert.NotNull(target);
            Assert.Equal(typeof(string), target!.PropertyType);

            foreach (PropertyInfo property in new[] { toggle, target })
            {
                var category = property.GetCustomAttribute<CategoryAttribute>();
                var displayName = property.GetCustomAttribute<DisplayNameAttribute>();
                var description = property.GetCustomAttribute<DescriptionAttribute>();

                Assert.NotNull(category);
                Assert.Equal("Defaults", category!.Category);
                Assert.NotNull(displayName);
                Assert.NotNull(description);
                Assert.True(description!.Description.Length > 40, $"{property.Name}'s description reads as a placeholder");
            }

            // CONTROL: the same attribute reader must see the internal throttle field as NOT
            // browsable, so a missing attribute anywhere above would show up as a difference
            // rather than as an empty read everywhere.
            var browsable = pageType.GetProperty("LastUpdateCheckUtc")?.GetCustomAttribute<BrowsableAttribute>();
            Assert.NotNull(browsable);
            Assert.False(browsable!.Browsable);
        }

        [Theory]
        [InlineData(false, "haiku", null)]
        [InlineData(true, "", null)]
        [InlineData(true, "   ", null)]
        [InlineData(true, "haiku", "haiku")]
        [InlineData(true, "  sonnet  ", "sonnet")]
        [InlineData(true, "sonnet,haiku", "sonnet,haiku")]
        public void The_fallback_flag_is_only_emitted_when_the_toggle_and_model_both_say_so(
            bool toggleOn, string model, string? expected)
        {
            var vm = new ChatSessionViewModel();
            vm.SetAdvancedOptions("", "", "", "", "", "", false, toggleOn, model);

            FieldInfo field = typeof(ChatSessionViewModel)
                .GetField("_advancedOptions", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var options = (ClaudeSessionStartOptions)field.GetValue(vm)!;

            Assert.Equal(expected, options.FallbackModel);
        }

        // ─── The new markup's element references resolve ───────────────────────────────────────

        [Fact]
        public void Every_ElementName_reference_in_the_add_menu_markup_resolves()
        {
            // Click/KeyDown handler names are a compile error when missing, so the build already
            // covers those. ElementName is not - it resolves at runtime and fails silently, exactly
            // like a binding path.
            string xaml = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml"));

            string[] declaredNames = Regex.Matches(xaml, "x:Name=\"([A-Za-z0-9_]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

            string[] references = Regex.Matches(xaml, "ElementName=([A-Za-z0-9_]+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();

            Assert.True(references.Length >= 3, $"only {references.Length} ElementName reference(s) found");

            foreach (string reference in references)
                Assert.Contains(reference, declaredNames);

            // CONTROL: the same check rejects a name that was never declared.
            Assert.DoesNotContain("AddMenuButtonn", declaredNames);

            foreach (string expected in new[] { "AddMenuButton", "AddMenuPopup", "WebQueryPanel", "WebQueryBox" })
                Assert.Contains(expected, declaredNames);

            foreach (string label in new[] { "Upload from computer", "Add context", "Browse the web" })
                Assert.Contains($"Text=\"{label}\"", xaml);
        }
    }
}
