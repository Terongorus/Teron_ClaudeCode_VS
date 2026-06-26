using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClaudeCodeGUI.ViewModels
{
    internal static class SessionHistoryStore
    {
        private static readonly string s_path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeCodeGUI", "sessions.json");

        public static List<SessionHistoryEntry> Load()
        {
            try
            {
                if (!File.Exists(s_path)) return new List<SessionHistoryEntry>();
                string json = File.ReadAllText(s_path);
                var list = JsonConvert.DeserializeObject<List<SessionHistoryEntry>>(json);
                return list?.OrderByDescending(e => e.LastUsed).Take(100).ToList()
                    ?? new List<SessionHistoryEntry>();
            }
            catch { return new List<SessionHistoryEntry>(); }
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
