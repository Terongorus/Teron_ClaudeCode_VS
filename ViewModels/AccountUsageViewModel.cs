using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TeronClaudeCodeVS.Protocol;

namespace TeronClaudeCodeVS.ViewModels
{
    public sealed class AccountUsageViewModel : ObservableObject
    {
        // ── Loading state ─────────────────────────────────────────────────────

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

        private string? _loadError;
        public string? LoadError { get => _loadError; private set => SetField(ref _loadError, value); }

        private bool _hasLoaded;
        public bool HasLoaded { get => _hasLoaded; private set => SetField(ref _hasLoaded, value); }

        // ── Account ───────────────────────────────────────────────────────────

        private string _email = "";
        public string Email { get => _email; private set => SetField(ref _email, value); }

        private string _authMethod = "";
        public string AuthMethod { get => _authMethod; private set => SetField(ref _authMethod, value); }

        private string? _organization;
        public string? Organization { get => _organization; private set => SetField(ref _organization, value); }

        private string _plan = "";
        public string Plan { get => _plan; private set => SetField(ref _plan, value); }

        private bool _hasAccountInfo;
        public bool HasAccountInfo { get => _hasAccountInfo; private set => SetField(ref _hasAccountInfo, value); }

        // ── Session rate limit ────────────────────────────────────────────────

        private double _sessionPercent;
        public double SessionPercent { get => _sessionPercent; private set => SetField(ref _sessionPercent, value); }

        private string _sessionLabel = "Session (5hr)";
        public string SessionLabel { get => _sessionLabel; private set => SetField(ref _sessionLabel, value); }

        private string _sessionPercentLabel = "—";
        public string SessionPercentLabel { get => _sessionPercentLabel; private set => SetField(ref _sessionPercentLabel, value); }

        private string _sessionResetLabel = "";
        public string SessionResetLabel { get => _sessionResetLabel; private set => SetField(ref _sessionResetLabel, value); }

        // ── Weekly rate limit ─────────────────────────────────────────────────

        private double _weeklyPercent;
        public double WeeklyPercent { get => _weeklyPercent; private set => SetField(ref _weeklyPercent, value); }

        private string _weeklyLabel = "Weekly (7 day)";
        public string WeeklyLabel { get => _weeklyLabel; private set => SetField(ref _weeklyLabel, value); }

        private string _weeklyPercentLabel = "—";
        public string WeeklyPercentLabel { get => _weeklyPercentLabel; private set => SetField(ref _weeklyPercentLabel, value); }

        private string _weeklyResetLabel = "";
        public string WeeklyResetLabel { get => _weeklyResetLabel; private set => SetField(ref _weeklyResetLabel, value); }

        private bool _hasRateLimitData;
        public bool HasRateLimitData { get => _hasRateLimitData; private set => SetField(ref _hasRateLimitData, value); }

        // ── Refresh ───────────────────────────────────────────────────────────

        public async Task RefreshAsync(string claudePath)
        {
            if (IsLoading) return;

            IsLoading = true;
            LoadError = null;
            HasLoaded = false;

            try
            {
                await LoadAllAsync(claudePath);
                HasLoaded = true;
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadAllAsync(string claudePath)
        {
            // `claude auth status` always prints JSON directly - no `--output-format` flag exists
            // for it (confirmed live: passing one is a hard CLI error). There is also no standalone
            // "usage" subcommand at all (confirmed live: `claude usage` gets swallowed as a chat
            // prompt instead of being recognized as a command) - real-time rate-limit percentages
            // are only ever obtained from the `rate_limit_event` messages a live session emits;
            // see RateLimitUpdated/UpdateRateLimit below, fed from ChatSessionViewModel.
            string jsonOut = await RunCommandAsync(claudePath, "auth status");
            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                try { ParseAccountJson(JObject.Parse(jsonOut)); }
                catch { }
            }
        }

        /// <summary>
        /// Real `claude auth status` schema, confirmed live (2026-08-26) - flat, not nested under
        /// an "account" key: {"loggedIn":true,"authMethod":"claude.ai","email":"...",
        /// "orgName":"...","subscriptionType":"pro",...}.
        /// </summary>
        private void ParseAccountJson(JObject obj)
        {
            Email = obj["email"]?.ToString() ?? "";
            AuthMethod = FormatAuthMethod(obj["authMethod"]?.ToString() ?? "");
            Organization = obj["orgName"]?.ToString();
            Plan = FormatPlan(obj["subscriptionType"]?.ToString() ?? "");

            HasAccountInfo = !string.IsNullOrEmpty(Email);
        }

        /// <summary>
        /// Applies a live `rate_limit_event` from the active session's own stream. Called by
        /// ChatSessionViewModel whenever one arrives, so the panel reflects real usage from
        /// whatever activity has actually happened - there's no other source for this data.
        /// </summary>
        public void UpdateRateLimit(RateLimitEvent e)
        {
            if (e.SessionUtilization.HasValue)
            {
                double pct = Math.Min(e.SessionUtilization.Value * 100.0, 100);
                SessionPercent = pct;
                SessionPercentLabel = $"{pct:0}%";
                SessionResetLabel = FormatResetLabel(e.SessionResetsAt);
                HasRateLimitData = true;
            }

            if (e.WeeklyUtilization.HasValue)
            {
                double pct = Math.Min(e.WeeklyUtilization.Value * 100.0, 100);
                WeeklyPercent = pct;
                WeeklyPercentLabel = $"{pct:0}%";
                WeeklyResetLabel = FormatResetLabel(e.WeeklyResetsAt);
                HasRateLimitData = true;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatPlan(string raw) =>
            string.IsNullOrEmpty(raw) ? "" : raw.ToLowerInvariant() switch
            {
                "free" => "Free",
                "pro" or "claude_pro" => "Claude Pro",
                "team" or "claude_team" => "Claude Team",
                "enterprise" => "Enterprise",
                _ => raw
            };

        private static string FormatAuthMethod(string raw) =>
            string.IsNullOrEmpty(raw) ? "" : raw.ToLowerInvariant() switch
            {
                "claude.ai" or "claude_ai" or "oauth" or "browser" => "Claude AI",
                "api_key" or "apikey" => "API Key",
                _ => raw
            };

        private static string FormatResetLabel(long? unixSeconds)
        {
            if (!unixSeconds.HasValue) return "";
            var remaining = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? $"Resets in {FormatTimeSpan(remaining)}" : "";
        }

        private static string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d";
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h";
            return $"{(int)ts.TotalMinutes}m";
        }

        private static async Task<string> RunCommandAsync(string claudePath, string args, int timeoutMs = 8000)
        {
            try
            {
                string fileName = claudePath;
                string fullArgs = args;

                string ext = Path.GetExtension(claudePath);
                if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    fullArgs = $"/c \"{claudePath}\" {args}";
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = fullArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };

                using Process process = Process.Start(psi);
                if (process == null) return "";

                var readTask = process.StandardOutput.ReadToEndAsync();
                var done = await Task.WhenAny(readTask, Task.Delay(timeoutMs));

                if (!process.HasExited)
                {
                    try { process.Kill(); } catch { }
                }

                return done == readTask ? await readTask : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
