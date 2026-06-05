namespace Antigravity_CLI_GUI.Core
{
    public enum TerminalMessageType
    {
        AssistantText,
        Error,
        Warning,
        System,
        CodeBlock,
        Json,
        Raw
    }

    public sealed class TerminalMessage
    {
        public TerminalMessageType Type { get; set; }
        public string Content { get; set; } = "";
        public string? Language { get; set; } // for code blocks
    }
}
