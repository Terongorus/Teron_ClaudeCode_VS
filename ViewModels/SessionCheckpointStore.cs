using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// Read-only view of the checkpoint store the CLI keeps for itself.
    ///
    /// Verified against real on-disk data (2026-08-29, CLI 2.1.251) rather than assumed, and then
    /// corrected once when a live run disagreed with the first reading. Every session transcript
    /// carries `file-history-snapshot` and `file-history-delta` records. Both name a backup as
    /// `&lt;hash&gt;@v&lt;N&gt;`, a real file under `~/.claude/file-history/&lt;sessionId&gt;/`
    /// holding the tracked file's earlier contents - confirmed by checking that one contains an
    /// Edit call's `old_string` and not its `new_string`.
    ///
    /// The link from one of our tool cards into that history is the assistant transcript record: a
    /// delta's `messageId` is that record's `uuid`, and the same record's `message.content` holds
    /// the `tool_use` block whose `id` is the `toolUseId` we already track on every tool card.
    ///
    /// The correction, because it is the kind of thing that reads as working until it does not: a
    /// delta is only written the first time a given file is backed up. Afterwards the CLI carries
    /// the file forward in each turn's snapshot instead, so a second edit to an already-tracked
    /// file has no delta of its own. Reading deltas alone therefore answers with a real backup of
    /// the right file from the wrong point in its history - which is worse than answering nothing,
    /// since a plausible wrong diff invites the user to trust it. See the method below.
    ///
    /// Why this matters for FEAT-2: for an edit that has ALREADY been applied, the working copy on
    /// disk is the "after" side and there is no honest way to reconstruct the "before" side from
    /// the tool input alone (a Write call simply does not say what it overwrote). The CLI's own
    /// backup is that missing side, and it is authoritative rather than inferred.
    ///
    /// Best-effort throughout: a transcript that has not been flushed yet, a pruned backup, or a
    /// schema that grows a field all return null, and the caller falls back or explains itself.
    /// This is a read of somebody else's private store, so it must never be load-bearing.
    /// </summary>
    public static class SessionCheckpointStore
    {
        /// <summary>
        /// The contents of <paramref name="filePath"/> immediately before the edit made by
        /// <paramref name="toolUseId"/>: an empty string when the CLI recorded that the file did
        /// not exist yet (a creation), or null when no backup can be found for that pairing.
        ///
        /// Two record types answer this, and both are needed - measured, after a first version
        /// that used only deltas came back with the right file at the wrong moment in its history.
        /// A `file-history-delta` is written when a message is about to change a file the CLI has
        /// not backed up yet, and names the backup holding the state just before that message. But
        /// when the file is ALREADY tracked, the CLI writes no delta at all: it rolls the current
        /// version into the next turn's `file-history-snapshot`, whose `trackedFileBackups` map
        /// carries a backup per tracked file as of that turn. A Write over a file that an earlier
        /// Edit already touched produces exactly that shape - no delta of its own, and its real "before"
        /// sitting in the preceding snapshot.
        /// </summary>
        public static string? TryReadContentBeforeEdit(
            string workingDirectory, string sessionId, string toolUseId, string filePath)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(toolUseId))
                return null;

            string? transcript = TranscriptReplay.FindTranscriptPath(workingDirectory, sessionId);
            if (transcript == null)
                return null;

            // The backup naming the state as of the last turn boundary before the target message.
            string? fromSnapshot = null;
            // The backup the target message wrote for itself, if it needed one. Beats the snapshot:
            // it is scoped to this exact message rather than to the turn it sits in.
            string? fromDelta = null;
            bool targetHasDelta = false;
            bool reachedTarget = false;
            string? messageId = null;

            try
            {
                foreach (string line in File.ReadLines(transcript))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JObject record;
                    try { record = JObject.Parse(line); }
                    catch { continue; }

                    string? type = record.Value<string>("type");

                    if (type == "assistant" && !reachedTarget && CarriesToolUse(record, toolUseId))
                    {
                        // Freeze the snapshot answer here. Anything recorded after this point
                        // describes later edits, not the state this call was about to change.
                        reachedTarget = true;
                        messageId = record.Value<string>("uuid");
                        continue;
                    }

                    if (type == "file-history-snapshot")
                    {
                        if (reachedTarget) continue;
                        JObject? tracked = (record["snapshot"] as JObject)?["trackedFileBackups"] as JObject;
                        if (tracked == null) continue;
                        foreach (var entry in tracked.Properties())
                        {
                            if (!SamePath(workingDirectory, entry.Name, filePath)) continue;
                            fromSnapshot = (entry.Value as JObject)?.Value<string>("backupFileName") ?? fromSnapshot;
                        }
                    }
                    else if (type == "file-history-delta")
                    {
                        string? tracking = record.Value<string>("trackingPath");
                        if (tracking == null || !SamePath(workingDirectory, tracking, filePath))
                            continue;

                        string? name = (record["backup"] as JObject)?.Value<string>("backupFileName");

                        // The target's own delta is written just after its assistant record, so it
                        // is matched by id rather than by position.
                        if (messageId != null &&
                            string.Equals(record.Value<string>("messageId"), messageId, StringComparison.Ordinal))
                        {
                            targetHasDelta = true;
                            fromDelta = name;
                        }
                        else if (!reachedTarget)
                        {
                            fromSnapshot = name ?? fromSnapshot;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The CLI is appending to this file continuously; a transient share violation is
                // not an error worth surfacing, it just means "no backup available right now".
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            if (!reachedTarget)
                return null;

            if (targetHasDelta && fromDelta != null)
                return ReadBackup(sessionId, fromDelta);

            if (fromSnapshot != null)
                return ReadBackup(sessionId, fromSnapshot);

            // A delta that names no backup, with nothing tracked before it, is how the CLI records
            // "there was nothing to back up" - the edit created the file.
            if (targetHasDelta)
                return "";

            return null;
        }

        private static string? ReadBackup(string sessionId, string backupFileName)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string path = Path.Combine(home, ".claude", "file-history", sessionId, backupFileName);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static bool CarriesToolUse(JObject record, string toolUseId)
        {
            if (record["message"] is not JObject message)
                return false;
            if (message["content"] is not JArray content)
                return false;

            foreach (JToken block in content)
            {
                if (block is not JObject obj) continue;
                if (obj.Value<string>("type") != "tool_use") continue;
                if (string.Equals(obj.Value<string>("id"), toolUseId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// `trackingPath` is usually absolute but is genuinely relative in some records (seen in
        /// real data), so compare both as written and resolved against the working directory.
        /// </summary>
        private static bool SamePath(string workingDirectory, string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(Resolve(workingDirectory, a), Resolve(workingDirectory, b),
                                 StringComparison.OrdinalIgnoreCase);
        }

        internal static string Resolve(string workingDirectory, string path)
        {
            try
            {
                string combined = Path.IsPathRooted(path) || workingDirectory.Length == 0
                    ? path
                    : Path.Combine(workingDirectory, path);
                return Path.GetFullPath(combined);
            }
            catch { return path; }
        }
    }
}
