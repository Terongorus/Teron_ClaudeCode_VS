using Newtonsoft.Json;
using System;

namespace TeronClaudeCodeVS.ViewModels
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

        /// <summary>
        /// True once the user has renamed this row here, in which case the title the CLI generates
        /// for the same session never overwrites it again. Persisted, because the rename has to
        /// survive the restart that a refresh would otherwise undo.
        /// </summary>
        [JsonProperty("userTitle")]
        public bool HasUserTitle { get; set; }

        /// <summary>
        /// Size and write time of the transcript the last time its title was read, as
        /// "&lt;length&gt;:&lt;ticks&gt;". A session whose transcript has not changed since cannot
        /// have a new title, so the refresh skips reading it at all - which is what keeps opening
        /// history off a 45 MB file cheap on every open but the first.
        /// </summary>
        [JsonProperty("titleStamp")]
        public string TitleStamp { get; set; } = "";

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
