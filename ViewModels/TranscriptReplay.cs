using TeronClaudeCodeVS.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// Reconstructs a resumed session's visible transcript by reading the CLI's own on-disk
    /// history file. Confirmed live (2026-08-26, CLI 2.1.246): `--resume` does NOT replay prior
    /// turns over the stream-json stdout wire - the CLI recovers conversation state server-side,
    /// but the client sees nothing but `init`/`status` until a new message is sent. The transcript
    /// file at `~/.claude/projects/&lt;cwd-hash&gt;/&lt;session-id&gt;.jsonl` is the only source for
    /// repopulating the UI. Different envelope schema from the live wire (extra per-line fields,
    /// full per-turn snapshots only - no incremental deltas), so this is a separate, tolerant,
    /// read-only parser rather than routing through <see cref="ClaudeMessage.Parse"/>.
    /// </summary>
    public static class TranscriptReplay
    {
        /// <summary>
        /// Confirmed live against several real cwd -&gt; `~/.claude/projects/&lt;folder&gt;` pairs
        /// (including one with spaces and one with an underscore in the path): ':', '\', '/', '_',
        /// and ' ' are each replaced 1-for-1 with '-' (no collapsing of consecutive dashes), case
        /// preserved, everything else left alone.
        /// </summary>
        private static string ComputeProjectFolderName(string cwd)
        {
            var sb = new StringBuilder(cwd.Length);
            foreach (char c in cwd)
                sb.Append(c == ':' || c == '\\' || c == '/' || c == '_' || c == ' ' ? '-' : c);
            return sb.ToString();
        }

        /// <summary>Locates the transcript file for a given cwd/session, or null if it doesn't exist.</summary>
        public static string? FindTranscriptPath(string workingDirectory, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(sessionId))
                return null;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string folder = ComputeProjectFolderName(workingDirectory);
            string path = Path.Combine(home, ".claude", "projects", folder, sessionId + ".jsonl");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Reads and parses the transcript, returning finished messages in chronological order.
        /// Best-effort: tolerant of unknown/unparseable lines (queue-operation, attachment,
        /// sidechain entries, future schema additions not yet seen) - skips them silently rather
        /// than throwing, since this only hydrates the UI and is never the source of truth for the
        /// actual conversation (the CLI itself holds that server-side).
        /// </summary>
        public static List<ChatMessageViewModel> Load(string workingDirectory, string sessionId)
        {
            var messages = new List<ChatMessageViewModel>();

            string? path = FindTranscriptPath(workingDirectory, sessionId);
            if (path == null)
                return messages;

            var toolCallsByUseId = new Dictionary<string, ToolCallViewModel>();

            // Each API round within one turn (tool call, then its result, then a follow-up round
            // of text) is a *separate* "assistant" transcript line, but live mode keeps them all
            // in one chat bubble until the turn's `result` message resets it - mirror that here by
            // accumulating into the same bubble until a genuine new user prompt starts the next turn.
            ChatMessageViewModel? currentAssistantMessage = null;

            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JObject root;
                try { root = JObject.Parse(line); }
                catch { continue; }

                // Sidechains are Task-tool subagent runs, not the main top-level conversation.
                if (root.Value<bool?>("isSidechain") == true)
                    continue;

                string? type = root.Value<string>("type");
                if (type != "user" && type != "assistant")
                    continue;

                JToken? content = root["message"]?["content"];
                if (content == null)
                    continue;

                if (type == "user")
                {
                    // A pure tool_result relay stays within the current turn/bubble.
                    if (TryApplyToolResults(content, toolCallsByUseId))
                        continue;

                    // A genuine new user prompt ends whatever assistant turn was in progress.
                    currentAssistantMessage = null;

                    var userMsg = new ChatMessageViewModel(ChatRole.User);
                    BuildBlocks(userMsg, content, toolCallsByUseId);
                    if (userMsg.Blocks.Count > 0)
                        messages.Add(userMsg);
                    continue;
                }

                if (currentAssistantMessage == null)
                {
                    currentAssistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
                    messages.Add(currentAssistantMessage);
                }
                BuildBlocks(currentAssistantMessage, content, toolCallsByUseId);
            }

            // An assistant bubble is added to the list as soon as it's created (so later
            // same-turn lines keep appending to it), but a line whose only content was an
            // unrecognized block type could leave it empty - drop those rather than showing a
            // blank bubble.
            messages.RemoveAll(m => m.Blocks.Count == 0);
            return messages;
        }

        /// <summary>
        /// If every item in a "user" line's content array is a tool_result, attaches each to its
        /// matching (already-seen) tool call and returns true. Returns false (no-op) for a normal
        /// user prompt, so the caller falls through to building a real chat bubble.
        /// </summary>
        private static bool TryApplyToolResults(JToken content, Dictionary<string, ToolCallViewModel> toolCallsByUseId)
        {
            if (content is not JArray arr || arr.Count == 0)
                return false;

            var items = arr.OfType<JObject>().ToList();
            if (items.Count == 0 || items.Any(i => i.Value<string>("type") != "tool_result"))
                return false;

            foreach (var item in items)
            {
                string toolUseId = item.Value<string>("tool_use_id") ?? "";
                bool isError = item.Value<bool?>("is_error") ?? false;
                string text = ClaudeMessage.ExtractText(item["content"]);

                if (toolCallsByUseId.TryGetValue(toolUseId, out var call))
                {
                    call.Output = text;
                    call.Status = isError ? ToolCallStatus.Error : ToolCallStatus.Done;
                }
            }

            return true;
        }

        /// <summary>
        /// Builds finished content blocks from a full content array/string. Transcript snapshots
        /// are always complete, never incremental deltas - unlike the live wire, so this builds
        /// text/thinking/tool_use blocks directly rather than reusing the delta-based handlers.
        /// </summary>
        private static void BuildBlocks(ChatMessageViewModel msg, JToken content, Dictionary<string, ToolCallViewModel> toolCallsByUseId)
        {
            if (content.Type == JTokenType.String)
            {
                string text = content.Value<string>() ?? "";
                if (text.Length > 0)
                    msg.Blocks.Add(new TextBlockViewModel { Text = text });
                return;
            }

            if (content is not JArray arr)
                return;

            foreach (var block in arr.OfType<JObject>())
            {
                switch (block.Value<string>("type"))
                {
                    case "text":
                    {
                        string text = block.Value<string>("text") ?? "";
                        if (text.Length > 0)
                            msg.Blocks.Add(new TextBlockViewModel { Text = text });
                        break;
                    }

                    case "thinking":
                    {
                        string text = block.Value<string>("thinking") ?? "";
                        if (text.Length > 0)
                            msg.Blocks.Add(new ThinkingBlockViewModel { Text = text });
                        break;
                    }

                    case "tool_use":
                    {
                        string id = block.Value<string>("id") ?? "";
                        string name = block.Value<string>("name") ?? "Tool";
                        var call = new ToolCallViewModel(id, name)
                        {
                            Input = block["input"] as JObject,
                            // Optimistic default for history - a matching tool_result (if this
                            // transcript captured one) corrects it to Done/Error via TryApplyToolResults.
                            Status = ToolCallStatus.Done
                        };
                        msg.Blocks.Add(call);
                        if (id.Length > 0)
                            toolCallsByUseId[id] = call;
                        break;
                    }

                    // tool_result never appears here - pure tool_result lines are intercepted by
                    // TryApplyToolResults before BuildBlocks is called.
                }
            }
        }
    }
}
