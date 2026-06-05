using System;
using System.Text.Json;

namespace Antigravity_CLI_GUI.Core
{
    public static class TerminalMessageParser
    {
        public static TerminalMessage Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return new TerminalMessage { Type = TerminalMessageType.Raw, Content = "" };

            line = line.Replace("\r", "");

            // Errors
            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                return new TerminalMessage
                {
                    Type = TerminalMessageType.Error,
                    Content = line.Substring("ERROR:".Length).Trim()
                };

            // Warnings
            if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
                return new TerminalMessage
                {
                    Type = TerminalMessageType.Warning,
                    Content = line.Substring("WARNING:".Length).Trim()
                };

            // System messages (e.g. model change confirmations)
            if (line.StartsWith("[system]", StringComparison.OrdinalIgnoreCase))
                return new TerminalMessage
                {
                    Type = TerminalMessageType.System,
                    Content = line.Substring("[system]".Length).Trim()
                };

            // Code fences: ```lang
            if (line.StartsWith("```"))
            {
                var lang = line.Trim('`', ' ');
                return new TerminalMessage
                {
                    Type = TerminalMessageType.CodeBlock,
                    Language = string.IsNullOrWhiteSpace(lang) ? null : lang,
                    Content = "" // content will be accumulated by caller if you later support multi-line blocks
                };
            }

            // JSON (best‑effort)
            if ((line.StartsWith("{") && line.EndsWith("}")) ||
                (line.StartsWith("[") && line.EndsWith("]")))
            {
                try
                {
                    JsonDocument.Parse(line);
                    return new TerminalMessage
                    {
                        Type = TerminalMessageType.Json,
                        Content = line
                    };
                }
                catch
                {
                    // fall through to assistant text
                }
            }

            // Default: assistant text
            return new TerminalMessage
            {
                Type = TerminalMessageType.AssistantText,
                Content = line
            };
        }
    }
}
