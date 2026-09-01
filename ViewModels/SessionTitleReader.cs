using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// Reads the short session title the CLI generates for itself, out of its own transcript.
    ///
    /// Measured against 99 real transcripts (2026-08-29, CLI 2.1.251), not assumed. Two record
    /// types carry a title, each one line of the session `.jsonl`:
    ///
    ///   {"type":"ai-title","aiTitle":"Check compiler diagnostics","sessionId":"..."}
    ///   {"type":"custom-title","customTitle":"Latest - Session history review","sessionId":"..."}
    ///
    /// Three things this reading is only correct because they were checked:
    ///
    /// 1. <b>Neither record appears once.</b> They are re-emitted as the session runs - one file
    ///    carried 236 of them - and the generated title is genuinely <i>revised</i> along the way
    ///    ("Teronserver services consolidation" later became "Consolidate projects into common
    ///    solution"). The last record of a kind is the current one; the first is stale.
    /// 2. <b>The last record in the file is not the answer.</b> In several transcripts a
    ///    `custom-title` the user had typed ("11.08.26 - Import and review previous session
    ///    history") is followed by a later `ai-title` holding the generated text - and the real
    ///    client still shows the custom one. So this is not last-wins: the last `custom-title`
    ///    wins outright, and `ai-title` only answers when no custom title was ever set.
    /// 3. <b>Field order varies</b> between `sessionId` and the title field, so these are parsed as
    ///    JSON rather than pattern-matched. (`type` was first on every record seen, which is what
    ///    makes the cheap pre-filter below safe.) Every title record across all 99 files named its
    ///    own file's session, so the file itself is treated as the identifying fact.
    ///
    /// Transcripts reach 45 MB in this workspace alone, and a history refresh asks about up to 100
    /// of them, so this reads a <see cref="TailBytes"/> window off the end of the file and only
    /// falls back to a full scan when that window holds no title at all.
    ///
    /// Best-effort throughout, like every other read of the CLI's private state: anything
    /// unreadable, unflushed or unrecognised returns null and the caller keeps the title it had.
    /// </summary>
    public static class SessionTitleReader
    {
        /// <summary>A title found on disk, and whether the user had typed it themselves.</summary>
        public sealed class Result(string title, bool isCustom)
        {

            /// <summary>The title text, trimmed and never empty.</summary>
            public string Title { get; } = title;

            /// <summary>True for a `custom-title` (typed by the user), false for a generated `ai-title`.</summary>
            public bool IsCustom { get; } = isCustom;
        }

        /// <summary>
        /// How much of the end of the transcript to read before resorting to a full scan. Titles
        /// are rewritten every turn, so the last turn's worth of lines is almost always enough -
        /// but a single turn holding large tool output can exceed this, hence the fallback.
        /// </summary>
        private const int TailBytes = 1024 * 1024;

        /// <summary>Title for a session, or null when its transcript is missing or carries none.</summary>
        public static Result? Read(string workingDirectory, string sessionId)
        {
            string? path = TranscriptReplay.FindTranscriptPath(workingDirectory, sessionId);
            return path == null ? null : ReadFile(path);
        }

        /// <summary>
        /// Title held by a transcript file, or null. Separated from <see cref="Read"/> so it can be
        /// pointed at a specific file without going through the cwd-to-folder mapping.
        /// </summary>
        public static Result? ReadFile(string transcriptPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
                    return null;

                Result? fromTail = Scan(ReadTailLines(transcriptPath, out bool truncated));
                if (fromTail != null || !truncated)
                    return fromTail;

                // The tail window held no title record at all and there is more file behind it -
                // the only remaining answer is in the part not read.
                return Scan(File.ReadLines(transcriptPath));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>
        /// Last `custom-title` if the sequence has one, else last `ai-title`, else null. Walks the
        /// whole sequence rather than stopping early, because both kinds keep being re-emitted and
        /// only the final occurrence of each is current.
        /// </summary>
        private static Result? Scan(IEnumerable<string> lines)
        {
            string? ai = null;
            string? custom = null;

            foreach (string line in lines)
            {
                if (!LooksLikeTitleRecord(line))
                    continue;

                JObject root;
                try { root = JObject.Parse(line); }
                catch { continue; }

                switch ((string?)root["type"])
                {
                    case "ai-title":
                        ai = Clean((string?)root["aiTitle"]) ?? ai;
                        break;
                    case "custom-title":
                        custom = Clean((string?)root["customTitle"]) ?? custom;
                        break;
                }
            }

            if (custom != null) return new Result(custom, isCustom: true);
            if (ai != null) return new Result(ai, isCustom: false);
            return null;
        }

        /// <summary>
        /// Cheap gate before paying for a JSON parse. Title records are short and lead with their
        /// `type`; assistant content that merely mentions the same words sits on lines orders of
        /// magnitude longer, so the length bound alone rejects nearly all of the file.
        /// </summary>
        private static bool LooksLikeTitleRecord(string line)
        {
            if (line.Length < 20 || line.Length > 2048) return false;
            if (line[0] != '{') return false;
            return line.IndexOf("\"ai-title\"", StringComparison.Ordinal) >= 0
                || line.IndexOf("\"custom-title\"", StringComparison.Ordinal) >= 0;
        }

        private static string? Clean(string? value)
        {
            if (value == null) return null;
            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// The last <see cref="TailBytes"/> of the file as complete lines. Sets
        /// <paramref name="truncated"/> when the file was longer than that window, so the caller
        /// can tell "no title in this window" from "no title in this file". The first line of a
        /// truncated read is dropped: seeking to a byte offset lands mid-line, and on a multi-byte
        /// character mid-character too.
        /// </summary>
        private static IEnumerable<string> ReadTailLines(string path, out bool truncated)
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            truncated = stream.Length > TailBytes;
            if (truncated)
                stream.Seek(-TailBytes, SeekOrigin.End);

            byte[] buffer = new byte[(int)Math.Min(stream.Length, TailBytes)];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }

            // Non-throwing decode: a partial leading character goes out with the partial
            // leading line anyway, and a malformed byte must not take the whole read down.
            string text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer, 0, read);
            string[] lines = text.Split('\n');

            List<string> result = new(lines.Length);
            for (int i = truncated ? 1 : 0; i < lines.Length; i++)
                result.Add(lines[i].TrimEnd('\r'));
            return result;
        }
    }
}
