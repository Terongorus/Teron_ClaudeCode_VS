using Newtonsoft.Json.Linq;
using System;
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
            return type switch
            {
                "system" => ParseSystem(root),
                "stream_event" => ParseStreamEvent(root),
                "assistant" => ParseAssistantSnapshot(root),
                "user" => ParseUserMessage(root),
                "result" => ParseResult(root),
                "control_request" => ParseControlRequest(root),
                "control_response" => ParseControlResponse(root),
                "rate_limit_event" => ParseRateLimitEvent(root),
                _ => null,
            };
        }

        /// <summary>
        /// The CLI has no standalone "usage" subcommand (confirmed live: `claude usage` is not a
        /// real command - it gets swallowed as a chat prompt) - real-time rate-limit utilization is
        /// only ever emitted as this side-channel event during a live session, right after the
        /// `status:"requesting"` system message and before the turn's own content starts streaming.
        /// </summary>
        private static ClaudeMessage? ParseRateLimitEvent(JObject root)
        {
            JObject? info = root["rate_limit_info"] as JObject;
            if (info?["unifiedWindows"] is not JObject windows) return null;

            JObject? fiveHour = windows["five_hour"] as JObject;
            JObject? sevenDay = windows["seven_day"] as JObject;
            if (fiveHour == null && sevenDay == null) return null;

            return new RateLimitEvent
            {
                SessionUtilization = fiveHour?.Value<double?>("utilization"),
                SessionResetsAt = fiveHour?.Value<long?>("resetsAt"),
                WeeklyUtilization = sevenDay?.Value<double?>("utilization"),
                WeeklyResetsAt = sevenDay?.Value<long?>("resetsAt"),
            };
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
                        .ToArray() ?? []
                };
            }

            if (subtype == "status")
            {
                return new StatusMessage
                {
                    Status = root.Value<string>("status") ?? "",
                    CompactResult = root.Value<string>("compact_result"),
                    CompactError = root.Value<string>("compact_error")
                };
            }

            // FEAT-7. The CLI announces a model switch as one of four `system` subtypes, all of
            // which carry a ready-made human sentence in `content` (schemas and message builders
            // read out of the shipped binary, v2.1.251, 2026-08-30). That sentence is what gets
            // shown - the CLI knows why it switched and words it better than a reconstruction
            // from the parts would.
            if (subtype == ModelFallbackEvent.ModelFallback ||
                subtype == ModelFallbackEvent.ConsentFallback ||
                subtype == ModelFallbackEvent.RefusalFallback ||
                subtype == ModelFallbackEvent.RefusalNoFallback)
            {
                string content = root.Value<string>("content") ?? "";
                string original = root.Value<string>("original_model") ?? "";
                string? fallback = root.Value<string>("fallback_model");

                // A subtype with neither a sentence nor the models it moved between says nothing
                // a reader could act on; better no notice than an empty one.
                if (content.Length == 0 && original.Length == 0 && string.IsNullOrEmpty(fallback))
                    return null;

                return new ModelFallbackEvent
                {
                    Subtype = subtype!,
                    Content = content,
                    OriginalModel = original,
                    FallbackModel = fallback,
                    Trigger = root.Value<string>("trigger"),
                    Scope = root.Value<string>("scope"),
                };
            }

            if (subtype == "compact_boundary")
            {
                var meta = root["compact_metadata"] as JObject;
                return new CompactBoundaryEvent
                {
                    Trigger = meta?.Value<string>("trigger") ?? "manual",
                    PreTokens = meta?.Value<long?>("pre_tokens"),
                    PostTokens = meta?.Value<long?>("post_tokens"),
                    TokensFreed = meta?.Value<long?>("cumulative_dropped_tokens")
                };
            }

            return null;
        }

        private static ClaudeMessage? ParseStreamEvent(JObject root)
        {
            if (root["event"] is not JObject evt) return null;

            string? eventType = evt.Value<string>("type");
            switch (eventType)
            {
                case "message_start":
                    return new MessageStartEvent();

                case "message_stop":
                    return new MessageStopEvent();

                case "content_block_start":
                {
                        JObject? block = evt["content_block"] as JObject;
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
                        JObject? delta = evt["delta"] as JObject;
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
            var content = root["message"]?["content"] as JArray ?? [];
            JToken? usage = root["message"]?["usage"];

            return new AssistantSnapshotEvent
            {
                Content = content,
                // Confirmed against the official VS Code extension's own usage-tracking (2026-09-05):
                // a sub-agent's (Task tool) own assistant turns carry a parent_tool_use_id and must
                // be excluded from context-window tracking - only the main loop's own usage counts
                // toward what will be sent back to it next turn.
                IsTopLevel = root["parent_tool_use_id"] == null || root["parent_tool_use_id"]!.Type == JTokenType.Null,
                InputTokens = usage?.Value<int?>("input_tokens"),
                CacheCreationInputTokens = usage?.Value<int?>("cache_creation_input_tokens"),
                CacheReadInputTokens = usage?.Value<int?>("cache_read_input_tokens"),
                OutputTokens = usage?.Value<int?>("output_tokens"),
            };
        }

        private static ClaudeMessage? ParseUserMessage(JObject root)
        {
            if (root["message"]?["content"] is not JArray content) return null;

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
                    : [],
                QueuedTurnCount = root.Value<int?>("queued_turn_count") ?? 0,
                ModelUsage = root["modelUsage"] is JObject modelUsage
                    ? modelUsage.Properties().ToDictionary(
                        p => p.Name,
                        p => new ModelUsageInfo
                        {
                            ContextWindow = p.Value.Value<int?>("contextWindow") ?? 0,
                            MaxOutputTokens = p.Value.Value<int?>("maxOutputTokens") ?? 0,
                        })
                    : new Dictionary<string, ModelUsageInfo>()
            };
        }

        private static ClaudeMessage ParseControlRequest(JObject root)
        {
            var request = root["request"] as JObject ?? [];
            string requestId = root.Value<string>("request_id") ?? "";
            string subtype = request.Value<string>("subtype") ?? "";

            if (subtype == "ask_user_question")
            {
                return new AskUserQuestionEvent { RequestId = requestId, Questions = ParseQuestions(request["questions"] as JArray) };
            }

            return new PermissionRequestEvent
            {
                RequestId = requestId,
                Subtype = subtype,
                ToolName = request.Value<string>("tool_name") ?? "",
                ToolUseId = request.Value<string>("tool_use_id"),
                Input = request["input"] as JObject ?? [],
                Title = request.Value<string>("title"),
                Description = request.Value<string>("description"),
                RequiresUserInteraction = request.Value<bool?>("requires_user_interaction") ?? false
            };
        }

        /// <summary>
        /// Shared by the dedicated `ask_user_question` control_request subtype and the built-in
        /// `AskUserQuestion` tool's `can_use_tool` input (same "questions" shape in both places).
        /// </summary>
        internal static List<AskQuestion> ParseQuestions(JArray? arr)
        {
            List<AskQuestion> questions = [];
            if (arr == null) return questions;

            foreach (var token in arr.OfType<JObject>())
            {
                AskQuestion q = new()
                {
                    QuestionText = token.Value<string>("question") ?? "",
                    Header = token.Value<string>("header") ?? "",
                    IsMultiSelect = token.Value<bool?>("multiSelect") ?? false
                };
                if (token["options"] is JArray opts)
                {
                    q.Options = [.. opts.OfType<JObject>().Select(o => new AskQuestionOption
                    {
                        Label = o.Value<string>("label") ?? "",
                        Description = o.Value<string>("description") ?? "",
                        Value = o.Value<string>("value") ?? o.Value<string>("label") ?? ""
                    })];
                }
                questions.Add(q);
            }
            return questions;
        }

        /// <summary>
        /// Parses a client-originated control_request's reply, e.g. the answer to an interrupt
        /// request. Wire shape (confirmed live): {"type":"control_response","response":
        /// {"subtype":"success","request_id":"...","response":{...payload...}}} - note request_id
        /// lives inside the outer "response" object here, unlike control_request where it's top-level.
        /// </summary>
        private static ClaudeMessage ParseControlResponse(JObject root)
        {
            var envelope = root["response"] as JObject ?? [];
            return new ControlResponseEvent
            {
                RequestId = envelope.Value<string>("request_id") ?? "",
                Subtype = envelope.Value<string>("subtype") ?? "",
                // A rejected request comes back as {"subtype":"error","error":"..."} with no
                // "response" payload at all. Capturing the reason here is what lets GAP-3's
                // commands report *why* they failed instead of just going quiet.
                Error = envelope.Value<string>("error"),
                Response = envelope["response"] as JObject ?? []
            };
        }
    }

    /// <summary>A line that wasn't valid JSON (e.g. stderr noise) - surfaced for the raw output panel only.</summary>
    public sealed class RawTextMessage(string text) : ClaudeMessage
    {
        public string Text { get; } = text;
    }

    public sealed class InitMessage : ClaudeMessage
    {
        public string SessionId { get; set; } = "";
        public string Model { get; set; } = "";
        public string PermissionMode { get; set; } = "manual";
        public string Cwd { get; set; } = "";
        public string[] SlashCommands { get; set; } = [];
    }

    public sealed class StatusMessage : ClaudeMessage
    {
        public string Status { get; set; } = "";

        /// <summary>Only set on the status line that reports a /compact outcome ("success"/"failed") - confirmed live (2026-08-26): this line's own `status` field is null, the result lives here instead.</summary>
        public string? CompactResult { get; set; }
        public string? CompactError { get; set; }
    }

    /// <summary>
    /// Emitted after a successful /compact, confirmed live (2026-08-26) against the real CLI:
    /// {"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual",
    /// "pre_tokens":33547,"post_tokens":885,"cumulative_dropped_tokens":32662,...}}. On failure
    /// (e.g. "Not enough messages to compact.") no boundary event follows - only the StatusMessage
    /// above carries CompactResult="failed"/CompactError.
    /// </summary>
    public sealed class CompactBoundaryEvent : ClaudeMessage
    {
        public string Trigger { get; set; } = "manual";
        public long? PreTokens { get; set; }
        public long? PostTokens { get; set; }
        public long? TokensFreed { get; set; }
    }

    /// <summary>
    /// FEAT-7. The CLI switched models mid-session, or refused to and said so.
    ///
    /// <para>Four <c>system</c> subtypes carry this, all of them with a finished sentence in
    /// <c>content</c>. Read out of the shipped CLI binary's own schemas (v2.1.251, 2026-08-30):</para>
    /// <list type="bullet">
    ///   <item><c>model_fallback</c> - the configured <c>--fallback-model</c> took over for this
    ///     turn because the primary failed. <c>trigger</c> is one of <c>model_not_found</c>,
    ///     <c>permission_denied</c>, <c>overloaded</c>, <c>server_error</c>, <c>last_resort</c>,
    ///     <c>model_blocked</c>. Turn-scoped - the primary is re-tried on the next user turn.</item>
    ///   <item><c>model_refusal_fallback</c> - the primary ended the stream with
    ///     <c>stop_reason: "refusal"</c> and the turn was retried on the fallback. Driven by the
    ///     CLI's own <c>switchModelsOnFlag</c> setting, which is <b>on by default</b> and is not
    ///     ours to set - so this one can arrive even with our own fallback option turned off.</item>
    ///   <item><c>model_consent_fallback</c> - the account reached a usage-credit boundary and the
    ///     user consented to continue on a cheaper model. This is the path behind the
    ///     <c>Switched to claude-haiku-4-5-20251001</c> line the 2026-08-28 audit saw baseline
    ///     print near its weekly limit; the audit attributed that to <c>switchModelsOnFlag</c>,
    ///     but that setting covers safeguard refusals - it is this subtype whose own wording
    ///     ("... requires usage credits ...") matches what was observed.</item>
    ///   <item><c>model_refusal_no_fallback</c> - a refusal with no retry, because nothing was
    ///     configured to fall back to. <see cref="FallbackModel"/> is null here, and this is the
    ///     only one of the four that is bad news rather than a status report.</item>
    /// </list>
    /// </summary>
    public sealed class ModelFallbackEvent : ClaudeMessage
    {
        public const string ModelFallback = "model_fallback";
        public const string ConsentFallback = "model_consent_fallback";
        public const string RefusalFallback = "model_refusal_fallback";
        public const string RefusalNoFallback = "model_refusal_no_fallback";

        /// <summary>Which of the four subtypes above this is.</summary>
        public string Subtype { get; set; } = "";

        /// <summary>The CLI's own finished sentence, e.g. "Switched to haiku due to high demand for opus".</summary>
        public string Content { get; set; } = "";

        public string OriginalModel { get; set; } = "";

        /// <summary>The model taken up instead - null for <c>model_refusal_no_fallback</c>.</summary>
        public string? FallbackModel { get; set; }

        /// <summary>Why, for <c>model_fallback</c>; "refusal" for the refusal subtypes; else null.</summary>
        public string? Trigger { get; set; }

        /// <summary>"session" or "local" on the refusal subtypes; absent on older CLIs, null elsewhere.</summary>
        public string? Scope { get; set; }

        /// <summary>True only for a refusal that had nowhere to fall back to.</summary>
        public bool IsFailure => Subtype == RefusalNoFallback;

        /// <summary>
        /// What to show in the transcript. Prefers the CLI's sentence; falls back to naming the
        /// two models only when an older CLI sent none.
        /// </summary>
        public string NoticeText
        {
            get
            {
                if (Content.Length > 0) return Content;
                if (!string.IsNullOrEmpty(FallbackModel))
                    return OriginalModel.Length > 0
                        ? $"Switched to {FallbackModel} from {OriginalModel}"
                        : $"Switched to {FallbackModel}";
                return OriginalModel.Length > 0
                    ? $"{OriginalModel} refused this turn and no fallback model is configured"
                    : "The model refused this turn and no fallback model is configured";
            }
        }
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
        public JArray Content { get; set; } = [];

        /// <summary>False for a Task-tool sub-agent's own assistant turn (carries a parent_tool_use_id).</summary>
        public bool IsTopLevel { get; set; }

        // Present only once this API round's usage is known (arrives with the full snapshot, not
        // incrementally). Null on every earlier snapshot of the same round.
        public int? InputTokens { get; set; }
        public int? CacheCreationInputTokens { get; set; }
        public int? CacheReadInputTokens { get; set; }
        public int? OutputTokens { get; set; }
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
        public IReadOnlyList<string> Errors { get; set; } = [];

        /// <summary>How many more turns are already queued behind this one - confirmed live (2026-08-26). 0 means this was the last turn in the queue, so the session can go idle.</summary>
        public int QueuedTurnCount { get; set; }

        /// <summary>Per-model context window/max-output-tokens, keyed by model id - used for the
        /// context-usage indicator. Confirmed against the official VS Code extension's own source
        /// (2026-09-05): it reads `modelUsage[currentModel].contextWindow`/`.maxOutputTokens` here,
        /// falling back to the previous known value if the current model's entry is absent from a
        /// given result (mirrored in ChatSessionViewModel rather than here, since only it knows the
        /// currently selected model).</summary>
        public IReadOnlyDictionary<string, ModelUsageInfo> ModelUsage { get; set; } = new Dictionary<string, ModelUsageInfo>();
    }

    public sealed class ModelUsageInfo
    {
        public int ContextWindow { get; set; }
        public int MaxOutputTokens { get; set; }
    }

    /// <summary>
    /// Live session/weekly rate-limit utilization, resolved as a fraction in [0,1]. `ResetsAt`
    /// values are Unix seconds. Arrives once per turn, before the turn's content streams.
    /// </summary>
    public sealed class RateLimitEvent : ClaudeMessage
    {
        public double? SessionUtilization { get; set; }
        public long? SessionResetsAt { get; set; }
        public double? WeeklyUtilization { get; set; }
        public long? WeeklyResetsAt { get; set; }
    }

    /// <summary>A `can_use_tool` control request that must be answered via a control_response.</summary>
    public sealed class PermissionRequestEvent : ClaudeMessage
    {
        public string RequestId { get; set; } = "";
        public string Subtype { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string? ToolUseId { get; set; }
        public JObject Input { get; set; } = [];
        public string? Title { get; set; }
        public string? Description { get; set; }

        /// <summary>
        /// True for the built-in `AskUserQuestion` tool's own can_use_tool request (confirmed live,
        /// 2026-08-26) - distinct from the separate `ask_user_question` control_request subtype
        /// above. Answering it needs real answer data, not just allow/deny; see
        /// ChatSessionViewModel.OnPermissionRequested for the routing.
        /// </summary>
        public bool RequiresUserInteraction { get; set; }
    }

    /// <summary>One question inside an `ask_user_question` control request.</summary>
    public sealed class AskQuestion
    {
        public string QuestionText { get; set; } = "";
        public string Header { get; set; } = "";
        public bool IsMultiSelect { get; set; }
        public AskQuestionOption[] Options { get; set; } = [];
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
        public IReadOnlyList<AskQuestion> Questions { get; set; } = [];
    }

    /// <summary>The CLI's reply to a client-originated control_request (e.g. an interrupt), correlated by RequestId.</summary>
    public sealed class ControlResponseEvent : ClaudeMessage
    {
        public string RequestId { get; set; } = "";
        public string Subtype { get; set; } = "";

        /// <summary>Set when <see cref="Subtype"/> is "error" - the CLI's own explanation.</summary>
        public string? Error { get; set; }

        public JObject Response { get; set; } = [];

        /// <summary>True when the CLI accepted and completed the request.</summary>
        public bool IsSuccess => string.Equals(Subtype, "success", StringComparison.OrdinalIgnoreCase);
    }
}
