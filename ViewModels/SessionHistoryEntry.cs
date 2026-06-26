using Newtonsoft.Json;
using System;

namespace ClaudeCodeGUI.ViewModels
{
    public sealed class SessionHistoryEntry : ObservableObject
    {
        [JsonProperty("id")]
        public string SessionId { get; set; } = "";

        private string _title = "Untitled";
        [JsonProperty("title")]
        public string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        [JsonProperty("lastUsed")]
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;

        [JsonProperty("cwd")]
        public string WorkingDirectory { get; set; } = "";

        private bool _isEditing;
        [JsonIgnore]
        public bool IsEditing
        {
            get => _isEditing;
            set => SetField(ref _isEditing, value);
        }

        [JsonIgnore]
        public string TimeAgo
        {
            get
            {
                TimeSpan span = DateTime.UtcNow - LastUsed;
                if (span.TotalSeconds < 60) return "just now";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
                if (span.TotalDays < 30) return $"{(int)span.TotalDays}d";
                return $"{(int)(span.TotalDays / 30)}mo";
            }
        }
    }
}
