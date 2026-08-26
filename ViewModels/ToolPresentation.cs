using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// Formats Claude Code's built-in (and MCP) tool calls for display: an icon, a friendly
    /// name, a one-line summary for the collapsed card, and optional markdown detail for the
    /// expanded card.
    /// </summary>
    internal static class ToolPresentation
    {
        public static string GetIcon(string toolName) => toolName switch
        {
            "Read" => "\U0001F4C4",
            "Write" => "\U0001F4DD",
            "Edit" or "NotebookEdit" => "✏️",
            "Bash" or "BashOutput" or "KillShell" => "\U0001F4BB",
            "Glob" => "\U0001F5C2️",
            "Grep" => "\U0001F50D",
            "WebFetch" => "\U0001F310",
            "WebSearch" => "\U0001F50E",
            "Task" => "\U0001F916",
            "TodoWrite" => "✅",
            "ExitPlanMode" => "\U0001F4CB",
            "AskUserQuestion" => "❓",
            "SlashCommand" => "⚡",
            _ => "\U0001F527"
        };

        public static string GetDisplayName(string toolName)
        {
            switch (toolName)
            {
                case "Read": return "Read file";
                case "Write": return "Write file";
                case "Edit": return "Edit file";
                case "NotebookEdit": return "Edit notebook";
                case "Bash": return "Run command";
                case "BashOutput": return "Command output";
                case "KillShell": return "Stop command";
                case "Glob": return "Find files";
                case "Grep": return "Search files";
                case "WebFetch": return "Fetch URL";
                case "WebSearch": return "Search the web";
                case "Task": return "Subagent task";
                case "TodoWrite": return "Update plan";
                case "ExitPlanMode": return "Exit plan mode";
                case "AskUserQuestion": return "Question";
                case "SlashCommand": return "Slash command";
            }

            if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
            {
                var parts = toolName.Split('_').Where(p => p.Length > 0).ToArray();
                if (parts.Length >= 3)
                    return $"{parts[parts.Length - 1]} ({parts[1]})";
            }

            return toolName;
        }

        public static string GetSummary(string toolName, JObject? input)
        {
            input ??= [];

            switch (toolName)
            {
                case "Read":
                {
                    string path = ShortenPath(S(input, "file_path"));
                    int? offset = input.Value<int?>("offset");
                    int? limit = input.Value<int?>("limit");
                    if (offset.HasValue && limit.HasValue)
                        return $"{path} (lines {offset}-{offset + limit - 1})";
                    if (offset.HasValue)
                        return $"{path} (from line {offset})";
                    return path;
                }

                case "Write":
                    return ShortenPath(S(input, "file_path"));

                case "Edit":
                {
                    string path = ShortenPath(S(input, "file_path"));
                    return input.Value<bool?>("replace_all") == true ? $"{path} (replace all)" : path;
                }

                case "NotebookEdit":
                    return ShortenPath(S(input, "notebook_path"));

                case "Bash":
                {
                    string? desc = S(input, "description");
                    string cmd = Truncate(S(input, "command") ?? "", 100);
                    return string.IsNullOrEmpty(desc) ? cmd : desc!;
                }

                case "BashOutput":
                    return $"Shell {S(input, "bash_id")}";

                case "KillShell":
                    return $"Shell {S(input, "shell_id")}";

                case "Glob":
                {
                    string pattern = S(input, "pattern") ?? "";
                    string? path = S(input, "path");
                    return string.IsNullOrEmpty(path) ? pattern : $"{pattern} in {ShortenPath(path)}";
                }

                case "Grep":
                {
                    string pattern = $"\"{Truncate(S(input, "pattern") ?? "", 60)}\"";
                    string? path = S(input, "path");
                    return string.IsNullOrEmpty(path) ? pattern : $"{pattern} in {ShortenPath(path)}";
                }

                case "WebFetch":
                    return S(input, "url") ?? "";

                case "WebSearch":
                    return S(input, "query") ?? "";

                case "Task":
                    return S(input, "description") ?? S(input, "subagent_type") ?? "Subagent task";

                case "TodoWrite":
                {
                        JArray? todos = input["todos"] as JArray;
                    int total = todos?.Count ?? 0;
                    if (total == 0) return "Update plan";
                    int done = todos!.OfType<JObject>().Count(t => t.Value<string>("status") == "completed");
                    return $"{done}/{total} tasks complete";
                }

                case "ExitPlanMode":
                    return "Ready to start implementing";

                case "AskUserQuestion":
                {
                    var first = (input["questions"] as JArray)?.OfType<JObject>().FirstOrDefault();
                    return first?.Value<string>("question") ?? "Question for you";
                }

                case "SlashCommand":
                    return S(input, "command") ?? "";

                default:
                    return input.Count == 0 ? GetDisplayName(toolName) : Truncate(input.ToString(Newtonsoft.Json.Formatting.None), 120);
            }
        }

        /// <summary>
        /// Line-level "+"/"-"/context diff for Edit/NotebookEdit tool calls; null for everything
        /// else. Used by DiffViewer to render colored line backgrounds instead of the markdown
        /// renderer.
        /// </summary>
        public static string? GetRawDiff(string toolName, JObject? input)
        {
            if (toolName != "Edit" && toolName != "NotebookEdit") return null;
            input ??= [];

            string? oldStr = S(input, "old_string");
            string? newStr = S(input, "new_string");
            if (oldStr == null && newStr == null) return null;

            string[] oldLines = oldStr != null ? SplitLines(oldStr) : [];
            string[] newLines = newStr != null ? SplitLines(newStr) : [];
            return ComputeLineDiff(oldLines, newLines);
        }

        /// <summary>
        /// Minimal line-level diff via an LCS backtrack (old/new strings in an Edit call are a
        /// handful of lines, so the O(n*m) DP table is cheap). Unchanged lines are emitted plain
        /// (no prefix) so DiffViewer renders them as context instead of duplicating every
        /// unchanged line as both a removal and an addition.
        /// </summary>
        private static string ComputeLineDiff(string[] a, string[] b)
        {
            int n = a.Length, m = b.Length;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            StringBuilder sb = new StringBuilder();
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (a[x] == b[y])
                {
                    sb.AppendLine(a[x]);
                    x++; y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    sb.AppendLine("- " + a[x]);
                    x++;
                }
                else
                {
                    sb.AppendLine("+ " + b[y]);
                    y++;
                }
            }
            while (x < n) { sb.AppendLine("- " + a[x]); x++; }
            while (y < m) { sb.AppendLine("+ " + b[y]); y++; }

            return sb.ToString().TrimEnd();
        }

        /// <summary>Markdown for the expanded card body, or null if there's nothing beyond the summary.</summary>
        public static string? GetDetailMarkdown(string toolName, JObject? input, string? output, bool isError)
        {
            input ??= [];
            StringBuilder sb = new StringBuilder();

            switch (toolName)
            {
                case "Edit":
                {
                    string? oldStr = S(input, "old_string");
                    string? newStr = S(input, "new_string");
                    sb.AppendLine("```diff");
                    if (oldStr != null)
                        foreach (var line in SplitLines(oldStr))
                            sb.AppendLine("- " + line);
                    if (newStr != null)
                        foreach (var line in SplitLines(newStr))
                            sb.AppendLine("+ " + line);
                    sb.AppendLine("```");
                    break;
                }

                case "Write":
                {
                    string lang = LanguageFromPath(S(input, "file_path"));
                    sb.AppendLine($"````{lang}");
                    sb.AppendLine(S(input, "content") ?? "");
                    sb.AppendLine("````");
                    break;
                }

                case "Bash":
                {
                    sb.AppendLine("```bash");
                    sb.AppendLine(S(input, "command") ?? "");
                    sb.AppendLine("```");
                    break;
                }

                case "Grep":
                case "Glob":
                {
                    foreach (var prop in input.Properties())
                        sb.AppendLine($"- **{prop.Name}**: `{prop.Value}`");
                    break;
                }

                case "WebFetch":
                {
                    string? prompt = S(input, "prompt");
                    if (!string.IsNullOrEmpty(prompt))
                        sb.AppendLine($"**Prompt:** {prompt}");
                    break;
                }

                case "TodoWrite":
                {
                        if (input["todos"] is JArray todos)
                        {
                            foreach (var t in todos.OfType<JObject>())
                            {
                                string content = t.Value<string>("content") ?? "";
                                string status = t.Value<string>("status") ?? "pending";
                                string box = status switch
                                {
                                    "completed" => "[x]",
                                    "in_progress" => "[~]",
                                    _ => "[ ]"
                                };
                                sb.AppendLine($"- {box} {content}");
                            }
                        }
                        break;
                }

                case "ExitPlanMode":
                {
                    string? plan = S(input, "plan");
                    if (!string.IsNullOrEmpty(plan))
                        sb.AppendLine(plan);
                    break;
                }

                case "AskUserQuestion":
                {
                    var questions = (input["questions"] as JArray)?.OfType<JObject>() ?? [];
                    foreach (var q in questions)
                    {
                        sb.AppendLine($"**{q.Value<string>("question")}**");
                            if (q["options"] is JArray options)
                                foreach (var opt in options.OfType<JObject>())
                                    sb.AppendLine($"- {opt.Value<string>("label")}");
                            sb.AppendLine();
                    }
                    break;
                }

                case "Read":
                case "Task":
                case "SlashCommand":
                    break;

                default:
                    if (input.Count > 0)
                    {
                        sb.AppendLine("````json");
                        sb.AppendLine(input.ToString(Newtonsoft.Json.Formatting.Indented));
                        sb.AppendLine("````");
                    }
                    break;
            }

            AppendOutput(sb, output, isError);

            string result = sb.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        private static void AppendOutput(StringBuilder sb, string? output, bool isError)
        {
            if (string.IsNullOrEmpty(output)) return;

            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(isError ? "**Error:**" : "**Output:**");
            sb.AppendLine("````");
            sb.AppendLine(TruncateBlock(output!, 4000));
            sb.AppendLine("````");
        }

        private static string? S(JObject? o, string key) => o?.Value<string>(key);

        private static string[] SplitLines(string s) => s.Replace("\r\n", "\n").Split('\n');

        private static string Truncate(string s, int max)
        {
            s = s.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static string TruncateBlock(string s, int maxChars)
            => s.Length <= maxChars ? s : s.Substring(0, maxChars) + "\n… (truncated)";

        /// <summary>Shows the last few path segments so long absolute paths stay readable.</summary>
        private static string ShortenPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var parts = path!.Replace('\\', '/').Split('/');
            return parts.Length <= 3 ? string.Join("/", parts) : ".../" + string.Join("/", parts.Skip(parts.Length - 2));
        }

        private static string LanguageFromPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string ext = Path.GetExtension(path!).TrimStart('.').ToLowerInvariant();
            return ext switch
            {
                "cs" => "csharp",
                "ts" => "typescript",
                "tsx" => "tsx",
                "js" => "javascript",
                "jsx" => "jsx",
                "py" => "python",
                "rb" => "ruby",
                "go" => "go",
                "rs" => "rust",
                "java" => "java",
                "json" => "json",
                "xml" or "xaml" or "csproj" or "vsixmanifest" or "vsct" => "xml",
                "html" => "html",
                "css" => "css",
                "md" => "markdown",
                "yaml" or "yml" => "yaml",
                "sh" or "bash" => "bash",
                "ps1" => "powershell",
                "sql" => "sql",
                _ => ""
            };
        }
    }
}
