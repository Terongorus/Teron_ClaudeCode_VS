using System;
using System.Collections.Generic;
using System.Linq;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>One entry in the GAP-1 terminal hand-off catalog.</summary>
    public sealed class TerminalHandoffEntry(string key, string label, string menuDescription,
        string dialogTitle, string dialogDescription)
    {
        /// <summary>Menu label, e.g. "Memory".</summary>
        public string Label { get; } = label;

        /// <summary>Second line in the menu, e.g. "Manage Claude's memory".</summary>
        public string MenuDescription { get; } = menuDescription;

        /// <summary>Card title, e.g. "Continue in Terminal to edit memory?".</summary>
        public string DialogTitle { get; } = dialogTitle;

        /// <summary>Card body - what happens to the setting once it is changed.</summary>
        public string DialogDescription { get; } = dialogDescription;

        /// <summary>The slash command handed to the CLI as its initial prompt.</summary>
        public string SlashCommand { get; } = "/" + key;

        /// <summary>Catalog key - also the slash command name.</summary>
        public string Key { get; } = key;
    }

    /// <summary>
    /// GAP-1. The 2026-08-28 audit's most useful structural finding: five of baseline's seven
    /// "Customize" menu items are not GUI features at all. Each renders an in-chat card offering
    /// to continue in a terminal, with a sentence explaining how the setting gets back to the IDE.
    ///
    /// EVERY STRING BELOW IS BASELINE'S OWN, copied verbatim out of the official extension's
    /// `webview/index.js` (the `W30` wording table in v2.1.251) rather than paraphrased - these
    /// are user-facing promises about how configuration propagates, and getting a word wrong
    /// would make us promise something different from what the CLI actually does. The slash
    /// command each card launches is likewise baseline's: it calls
    /// `openClaudeInTerminal("/" + key)`, and all five were confirmed to be real interactive
    /// commands in the CLI binary (they are absent from the headless `init` event's
    /// `slash_commands` list because they open interactive TUI surfaces, which is exactly why
    /// they need a terminal in the first place).
    ///
    /// Baseline's table has a sixth entry, `plugins`, which it deliberately skips when building
    /// this menu (`if (Y === "plugins") continue;`) because plugins get a real GUI panel instead.
    /// We do the same - see FEAT-5.
    /// </summary>
    public static class TerminalHandoffCatalog
    {
        public static IReadOnlyList<TerminalHandoffEntry> Entries { get; } =
        [
            new TerminalHandoffEntry("memory", "Memory", "Manage Claude's memory",
                "Continue in Terminal to edit memory?",
                "Once configured, memories will be picked up by Claude Code here in your IDE."),

            new TerminalHandoffEntry("agents", "Agents", "Configure custom agents",
                "Continue in Terminal to configure agents?",
                "Once agents are configured in Terminal, you can reload this extension and ask Claude to use them here."),

            new TerminalHandoffEntry("hooks", "Hooks", "Set up event hooks",
                "Continue in Terminal to configure hooks?",
                "Once hooks are configured in this repository, they'll be active in your IDE, too."),

            // Baseline keys this one "config", not "output-style": the CLI has no /output-style
            // command, the setting lives inside /config, and its description says so.
            new TerminalHandoffEntry("config", "Output styles", "Change response formatting style",
                "Continue in Terminal to change output style?",
                "Output style is set via /config. After changing it in Terminal and reloading this extension, you'll be able to use it here."),

            new TerminalHandoffEntry("permissions", "Permissions", "Manage permission settings",
                "Continue in Terminal to manage permissions?",
                "Permission settings are shared between Terminal and this IDE."),
        ];

        public static TerminalHandoffEntry? Find(string key) =>
            Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
