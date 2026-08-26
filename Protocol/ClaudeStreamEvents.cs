using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace TeronClaudeCodeVS.Protocol
{
    /// <summary>
    /// Base type for a single parsed line of the `claude -p --input-format stream-json
    /// --output-format stream-json --include-partial-messages --verbose` NDJSON protocol.
    /// </summary>
    public abstract class ClaudeMessage
    {
        /// <summary>Parses one NDJSON line. Returns null for event kinds we intentionally ignore.</summary>
        public static ClaudeMessage? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return null;

            JObject root;
            try
            {
                root = JObject.Parse(line);
            }
            catch
            {
                return new RawTextMessage(line);
            }

            string? type = root.Value<string>("type");
            switch (type)
            {
                case "system":
                    return ParseSystem(root);

                case "stream_event":
                    return ParseStreamEvent(root);

                case "assistant":
                    return ParseAssistantSnapshot(root);

                case "user":
                    return ParseUserMessage(root);

                case "result":
                    return ParseResult(root);

                case "control_request":
                    return ParseControlRequest(root);

                case "control_response":
                    return ParseControlResponse(root);

                default:
                    return null;
            }
        }

        private static ClaudeMessage? ParseSystem(JObject root)
        {
            string? subtype = root.Value<string>("subtype");
            if (subtype == "init")
            {
                return new InitMessage
                {
                    SessionId = root.Value<string>("session_id") ?? "",
                    Model = root.Value<string>("model") ?? "",
                    PermissionMode = root.Value<string>("permissionMode") ?? "manual",
                    Cwd = root.Value<string>("cwd") ?? "",
                    SlashCommands = root["slash_commands"]?
                        .Select(t => t.Value<string>() ?? "")
                        .Where(s => s.Length > 0)
                        .ToArray() ?? System.Array.Empty<string>()
                };
            }

            if (subtype == "status")
            {
                return new StatusMessage { Status = root.Value<string>("status") ?? "" };
            }

            return null;
        }

        private static ClaudeMessage? ParseStreamEvent(JObject root)
        {
            var evt = root["event"] as JObject;
            if (evt == null) return null;

            string? eventType = evt.Value<string>("type");
            switch (eventType)
            {
                case "message_start":
                    return new MessageStartEvent();

                case "message_stop":
                    return new MessageStopEvent();

                case "content_block_start":
                {
                    var block = evt["content_block"] as JObject;
                    string blockType = block?.Value<string>("type") ?? "";
                    return new ContentBlockStartEvent
                    {
                        Index = evt.Value<int?>("index") ?? 0,
                        BlockType = blockType,
                        ToolUseId = blockType == "tool_use" ? block?.Value<string>("id") : null,
                        ToolName = blockType == "tool_use" ? block?.Value<string>("name") : null
                    };
                }

                case "content_block_delta":
                {
                    var delta = evt["delta"] as JObject;
                    string? deltaType = delta?.Value<string>("type");
                    int index = evt.Value<int?>("index") ?? 0;

                    return deltaType switch
                    {
                        "text_delta" => new TextDeltaEvent { Index = index, Delta = delta!.Value<string>("text") ?? "" },
                        "thinking_delta" => new ThinkingDeltaEvent { Index = index, Delta = delta!.Value<string>("thinking") ?? "" },
                        _ => null
                    };
                }

                case "content_block_stop":
                    return new ContentBlockStopEvent { Index = evt.Value<int?>("index") ?? 0 };

                default:
                    return null;
            }
        }

        private static ClaudeMessage ParseAssistantSnapshot(JObject root)
        {
            var content = root["message"]?["content"] as JArray ?? new JArray();
            return new AssistantSnapshotEvent { Content = content };
        }

        private static ClaudeMessage? ParseUserMessage(JObject root)
        {
            var content = root["message"]?["content"] as JArray;
            if (content == null) return null;

            foreach (var item in content.OfType<JObject>())
            {
                if (item.Value<string>("type") != "tool_result") continue;

                string toolUseId = item.Value<string>("tool_use_id") ?? "";
                bool isError = item.Value<bool?>("is_error") ?? false;
                string text = ExtractText(item["content"]);

                return new ToolResultEvent { ToolUseId = toolUseId, ResultText = text, IsError = isError };
            }

            return null;
        }

        /// <summary>Extracts plain text from a tool_result's `content` field (string or an array of text blocks). Also reused by <see cref="TeronClaudeCodeVS.ViewModels.TranscriptReplay"/> for the on-disk transcript, which uses the identical shape.</summary>
        internal static string ExtractText(JToken? content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return content.Value<string>() ?? "";

            if (content is JArray arr)
            {
                return string.Join("\n", arr.OfType<JObject>()
                    .Where(o => o.Value<string>("type") == "text")
                    .Select(o => o.Value<string>("text") ?? ""));
            }

            return "";
        }

        private static ClaudeMessage ParseResult(JObject root)
        {
            return new ResultMessage
            {
                IsError = root.Value<bool?>("is_error") ?? false,
                SessionId = root.Value<string>("session_id") ?? "",
                ResultText = root.Value<string>("result"),
                TotalCostUsd = root.Value<double?>("total_cost_usd"),
                DurationMs = root.Value<long?>("duration_ms") ?? 0,
                NumTurns = root.Value<int?>("num_turns") ?? 0,
                InputTokens = root["usage"]?.Value<int?>("input_tokens"),
                OutputTokens = root["usage"]?.Value<int?>("output_tokens"),
                Errors = root["errors"] is JArray errs
                    ? errs.Select(t => t.Value<string>() ?? "").Where(s => s.Length > 0).ToArray()
                    : System.Array.Empty<string>()
            };
        }

        private static ClaudeMessage ParseControlRequest(JObject root)
        {
            var request = root["request"] as JObject ?? new JObject();
            string requestId = root.Value<string>("request_id") ?? "";
            string subtype = request.Value<string>("subtype") ?? "";

            if (subtype == "ask_user_question")
            {
                var questions = new List<AskQuestion>();
                if (request["questions"] is JArray arr)
                {
                    foreach (var token in arr.OfType<JObject>())
                    {
                        var q = new AskQuestion
                        {
                            QuestionText = token.Value<string>("question") ?? "",
                            Header = token.Value<string>("header") ?? "",
                            IsMultiSelect = token.Value<bool?>("multiSelect") ?? false
                        };
                        if (token["options"] is JArray opts)
                        {
                            q.Options = opts.OfType<JObject>().Select(o => new AskQuestionOption
                            {
                                Label = o.Value<string>("label") ?? "",
                                Description = o.Value<string>("description") ?? "",
                                Value = o.Value<string>("value") ?? o.Value<string>("label") ?? ""
                            }).ToArray();
                        }
                        questions.Add(q);
                    }
                }
                return new AskUserQuestionEvent { RequestId = requestId, Questions = questions };
            }

            return new PermissionRequestEvent
            {
                RequestId = requestId,
                Subtype = subtype,
                ToolName = request.Value<string>("tool_name") ?? "",
                ToolUseId = request.Value<string>("tool_use_id"),
                Input = request["input"] as JObject ?? new JObject(),
                Title = request.Value<string>("title"),
                Description = request.Value<string>("description")
            };
        }

        /// <summary>
        /// Parses a client-originated control_request's reply, e.g. the answer to an interrupt
        /// request. Wire shape (confirmed live): {"type":"control_response","response":
        /// {"subtype":"success","request_id":"...","response":{...payload...}}} - note request_id
        /// lives inside the outer "response" object here, unlike control_request where it's top-level.
        /// </summary>
        private static ClaudeMessage ParseControlResponse(JObject root)
        {
            var envelope = root["response"] as JObject ?? new JObject();
            return new ControlResponseEvent
            {
                RequestId = envelope.Value<string>("request_id") ?? "",
                Subtype = envelope.Value<string>("subtype") ?? "",
                Response = envelope["response"] as JObject ?? new JObject()
            };
        }
    }

    /// <summary>A line that wasn't valid JSON (e.g. stderr noise) - surfaced for the raw output panel only.</summary>
    public sealed class RawTextMessage : ClaudeMessage
    {
        public string Text { get; }
        public RawTextMessage(string text) => Text = text;
    }

    public sealed class InitMessage : ClaudeMessage
    {
        public string SessionId { get; set; } = "";
        public string Model { get; set; } = "";
        public string PermissionMode { get; set; } = "manual";
        public string Cwd { get; set; } = "";
        public string[] SlashCommands { get; set; } = System.Array.Empty<string>();
    }

    public sealed class StatusMessage : ClaudeMessage
    {
        public string Status { get; set; } = "";
    }

    public sealed class MessageStartEvent : ClaudeMessage { }

    public sealed class MessageStopEvent : ClaudeMessage { }

    public sealed class ContentBlockStartEvent : ClaudeMessage
    {
        public int Index { get; set; }
        public string BlockType { get; set; } = "";
        public string? ToolUseId { get; set; }
        public string? ToolName { get; set; }
    }

    public sealed class ContentBlockStopEvent : ClaudeMessage
    {
        public int Index { get; set; }
    }

    public sealed class TextDeltaEvent : ClaudeMessage
    {
        public int Index { get; set; }
        public string Delta { get; set; } = "";
    }

    public sealed class ThinkingDeltaEvent : ClaudeMessage
    {
        public int Index { get; set; }
        public string Delta { get; set; } = "";
    }

    /// <summary>Cumulative snapshot of the current assistant message's content blocks (incl. finalized tool_use inputs).</summary>
    public sealed class AssistantSnapshotEvent : ClaudeMessage
    {
        public JArray Content { get; set; } = new JArray();
    }

    public sealed class ToolResultEvent : ClaudeMessage
    {
        public string ToolUseId { get; set; } = "";
        public string ResultText { get; set; } = "";
        public bool IsError { get; set; }
    }

    public sealed class ResultMessage : ClaudeMessage
    {
        public bool IsError { get; set; }
        public string SessionId { get; set; } = "";
        public string? ResultText { get; set; }
        public double? TotalCostUsd { get; set; }
        public long DurationMs { get; set; }
        public int NumTurns { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = System.Array.Empty<string>();
    }

    /// <summary>A `can_use_tool` control request that must be answered via a control_response.</summary>
    public sealed class PermissionRequestEvent : ClaudeMessage
    {
        public string RequestId { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string? ToolUseId { get; set; }
        public JObject Input { get; set; } = new JObject();
        public string? Title { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>One question inside an `ask_user_question` control request.</summary>
    public sealed class AskQuestion
    {
        public string QuestionText { get; set; } = "";
        public string Header { get; set; } = "";
        public bool IsMultiSelect { get; set; }
        public AskQuestionOption[] Options { get; set; } = System.Array.Empty<AskQuestionOption>();
    }

    public sealed class AskQuestionOption
    {
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>An `ask_user_question` control request — Claude is asking the user to make choices before proceeding.</summary>
    public sealed class AskUserQuestionEvent : ClaudeMessage
    {
        public string RequestId { get; set; } = "";
        public IReadOnlyList<AskQuestion> Questions { get; set; } = System.Array.Empty<AskQuestion>();
    }

    /// <summary>The CLI's reply to a client-originated control_request (e.g. an interrupt), correlated by RequestId.</summary>
    public sealed class ControlResponseEvent : ClaudeMessage
    {
        public string RequestId { get; set; } = "";
        public string Subtype { get; set; } = "";
        public JObject Response { get; set; } = new JObject();
    }
}
