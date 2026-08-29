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
        internal sealed class TitleUpdate
        {
            public TitleUpdate(string sessionId, string? title, string stamp)
            {
                SessionId = sessionId;
                Title = title;
                Stamp = stamp;
            }

            public string SessionId { get; }

            /// <summary>The new title, or null when only the stamp moved (transcript grew, title unchanged).</summary>
            public string? Title { get; }

            /// <summary>Transcript identity at the moment it was read, for <see cref="SessionHistoryEntry.TitleStamp"/>.</summary>
            public string Stamp { get; }
        }

        /// <summary>
        /// Re-reads generated titles for the given rows and returns what changed. Pure file I/O and
        /// no mutation, so it is safe to call off the UI thread - the caller applies the result
        /// back on it. Rows the user has renamed here are skipped outright, and so are rows whose
        /// transcript has not been written to since the last read.
        /// </summary>
        internal static List<TitleUpdate> ComputeTitleUpdates(IEnumerable<SessionHistoryEntry> entries)
        {
            List<TitleUpdate> updates = new List<TitleUpdate>();

            foreach (SessionHistoryEntry entry in entries)
            {
                if (entry.HasUserTitle) continue;

                string stamp;
                try
                {
                    string? path = TranscriptReplay.FindTranscriptPath(entry.WorkingDirectory, entry.SessionId);
                    if (path == null) continue;

                    FileInfo info = new FileInfo(path);
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
