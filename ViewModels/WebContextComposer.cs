using System;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// FEAT-6, the "Browse the web" half. Turns what the user typed into the add-menu's web box
    /// into a line of prompt text.
    ///
    /// <para><b>Why this exists rather than a browser bridge.</b> Baseline's own "Browse the web"
    /// entry does not browse anything itself: read out of the shipped VS Code extension's webview
    /// bundle (v2.1.251, 2026-08-30), it inserts the literal mention prefix <c>@browser:</c>, whose
    /// expander then calls that extension's <c>ensureChromeMcpEnabled()</c> and
    /// <c>createNewBrowserTab()</c> to attach open Chrome tabs. The entry is gated on
    /// <c>browserIntegrationSupported</c>, which that extension defines as
    /// <c>authMethod === "claudeai"</c>. So the feature is the Claude-in-Chrome integration - a
    /// browser extension plus that host's own MCP bridge - and <c>@browser:</c> resolves to nothing
    /// outside it. There is no flag we could pass the CLI to obtain it.</para>
    ///
    /// <para>What the CLI does give every session is <c>WebFetch</c> and <c>WebSearch</c> (both
    /// present in the shipped binary). So the entry keeps baseline's label and its place in the
    /// menu, and delivers the same outcome - web content as conversation context - by the route
    /// that is actually available here. The divergence is real and is documented in the Phase 7
    /// doc; it is not presented as tab attachment.</para>
    /// </summary>
    public static class WebContextComposer
    {
        /// <summary>
        /// Composes the line to insert, or null when there is nothing to compose from.
        /// A URL becomes a fetch instruction, anything else becomes a search instruction.
        /// </summary>
        public static string? Compose(string? input)
        {
            string text = (input ?? "").Trim();
            if (text.Length == 0) return null;

            string? url = TryNormalizeUrl(text);
            if (url != null)
                return $"Read {url} and use it as context for this conversation.";

            // Quotes inside the terms would close the quoted span early and read as if the search
            // ended there; a single quoting style throughout is what keeps the sentence unambiguous.
            string terms = text.Replace('"', '\'');
            return $"Search the web for \"{terms}\" and use the results as context for this conversation.";
        }

        /// <summary>
        /// Returns an absolute http/https URL when the text is one, else null.
        ///
        /// A bare host like <c>docs.anthropic.com/en/api</c> is accepted and given an https scheme:
        /// it is what people paste, and Uri.TryCreate on its own would reject it as relative. The
        /// dot-and-no-whitespace test is what separates it from a search phrase - "claude code
        /// pricing" has spaces, "example.com" does not.
        /// </summary>
        internal static string? TryNormalizeUrl(string text)
        {
            if (text.IndexOf(' ') >= 0 || text.IndexOf('\t') >= 0 || text.IndexOf('\n') >= 0)
                return null;

            if (Uri.TryCreate(text, UriKind.Absolute, out Uri? parsed))
            {
                // Absolute, but not necessarily web: "file:///c:/x" and "mailto:a@b" both parse.
                return parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps
                    ? text
                    : null;
            }

            // No scheme. Only treat it as a host if there is a dot with something either side of
            // it, so that a one-word search term ("kubernetes") is not silently turned into a URL.
            int dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1) return null;
            if (text.IndexOf('/') == 0) return null;

            string candidate = "https://" + text;
            return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? guessed) && guessed.Host.IndexOf('.') > 0
                ? candidate
                : null;
        }
    }
}
