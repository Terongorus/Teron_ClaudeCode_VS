using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TeronClaudeCodeVS.ViewModels
{
    internal static class SessionHistoryStore
    {
        private static readonly string s_path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TeronClaudeCodeVS", "sessions.json");

        /// <summary>
        /// Session ids the user has deleted from History. Deliberately never touches the CLI's own
        /// transcript on disk - confirmed by reading the official VS Code extension's installed
        /// source (2026-09-05): its own "Delete" is actually labeled "Archive session" and works
        /// the same way, appending to a `hiddenSessionIds` list in its own extension storage and
        /// filtering the displayed list against it, never calling into the filesystem. Kept as a
        /// separate file (mirroring that extension's separate storage key) rather than a new field
        /// on <see cref="SessionHistoryEntry"/>, since a session can be hidden before it's ever been
        /// tracked in `sessions.json` at all (see ChatSessionViewModel.BeginDiscoverUntrackedSessions).
        /// </summary>
        private static readonly string s_hiddenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TeronClaudeCodeVS", "hidden-sessions.json");

        public static HashSet<string> LoadHiddenIds()
        {
            try
            {
                if (!File.Exists(s_hiddenPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string json = File.ReadAllText(s_hiddenPath);
                var list = JsonConvert.DeserializeObject<List<string>>(json);
                return new HashSet<string>(list ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        public static void SaveHiddenIds(IEnumerable<string> ids)
        {
            try
            {
                string dir = Path.GetDirectoryName(s_hiddenPath)!;
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(s_hiddenPath, JsonConvert.SerializeObject(ids.ToList(), Formatting.Indented));
            }
            catch { }
        }

        public static List<SessionHistoryEntry> Load()
        {
            try
            {
                if (!File.Exists(s_path)) return [];
                string json = File.ReadAllText(s_path);
                var list = JsonConvert.DeserializeObject<List<SessionHistoryEntry>>(json);
                return list?.OrderByDescending(e => e.LastUsed).Take(100).ToList()
                    ?? [];
            }
            catch { return []; }
        }

        /// <summary>One row's title as re-read from the CLI's transcript. See <see cref="ComputeTitleUpdates"/>.</summary>
        internal sealed class TitleUpdate(string sessionId, string? title, string stamp)
        {
            public string SessionId { get; } = sessionId;

            /// <summary>The new title, or null when only the stamp moved (transcript grew, title unchanged).</summary>
            public string? Title { get; } = title;

            /// <summary>Transcript identity at the moment it was read, for <see cref="SessionHistoryEntry.TitleStamp"/>.</summary>
            public string Stamp { get; } = stamp;
        }

        /// <summary>
        /// Re-reads generated titles for the given rows and returns what changed. Pure file I/O and
        /// no mutation, so it is safe to call off the UI thread - the caller applies the result
        /// back on it. Rows the user has renamed here are skipped outright, and so are rows whose
        /// transcript has not been written to since the last read.
        /// </summary>
        internal static List<TitleUpdate> ComputeTitleUpdates(IEnumerable<SessionHistoryEntry> entries)
        {
            List<TitleUpdate> updates = [];

            foreach (SessionHistoryEntry entry in entries)
            {
                if (entry.HasUserTitle) continue;

                string stamp;
                try
                {
                    string? path = TranscriptReplay.FindTranscriptPath(entry.WorkingDirectory, entry.SessionId);
                    if (path == null) continue;

                    FileInfo info = new(path);
                    stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                    if (stamp == entry.TitleStamp) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                SessionTitleReader.Result? found = SessionTitleReader.Read(entry.WorkingDirectory, entry.SessionId);
                bool changed = found != null && found.Title != entry.Title;
                updates.Add(new TitleUpdate(entry.SessionId, changed ? found!.Title : null, stamp));
            }

            return updates;
        }

        public static void Save(List<SessionHistoryEntry> entries)
        {
            try
            {
                string dir = Path.GetDirectoryName(s_path)!;
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(s_path, JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch { }
        }
    }
}
