using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCodeCLIGUI.ViewModels
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
            // Try JSON-formatted auth status first.
            string jsonOut = await RunCommandAsync(claudePath, "auth status --output-format json");
            if (!string.IsNullOrWhiteSpace(jsonOut))
            {
                try
                {
                    ParseAccountJson(JObject.Parse(jsonOut));
                    goto checkUsage;
                }
                catch { }
            }

            // Fallback: parse plain-text output.
            string textOut = await RunCommandAsync(claudePath, "auth status");
            ParseAccountText(textOut);

        checkUsage:
            // Try to get rate-limit usage percentages.
            string usageOut = await RunCommandAsync(claudePath, "usage --output-format json");
            if (!string.IsNullOrWhiteSpace(usageOut))
            {
                try { ParseUsageJson(JObject.Parse(usageOut)); }
                catch { }
            }
        }

        private void ParseAccountJson(JObject obj)
        {
            var account = obj["account"] as JObject ?? obj;

            Email = account["email"]?.ToString()
                    ?? account["emailAddress"]?.ToString()
                    ?? obj["email"]?.ToString()
                    ?? "";

            AuthMethod = FormatAuthMethod(
                account["authType"]?.ToString()
                ?? account["auth_type"]?.ToString()
                ?? obj["authType"]?.ToString()
                ?? "");

            Organization = account["organization"]?.ToString()
                            ?? obj["organization"]?.ToString();

            Plan = FormatPlan(
                account["plan"]?.ToString()
                ?? obj["plan"]?.ToString()
                ?? "");

            HasAccountInfo = !string.IsNullOrEmpty(Email);

            // Some CLI versions bundle usage in the same JSON.
            var bundledUsage = (obj["usage"] ?? obj["rateLimits"]) as JObject;
            if (bundledUsage != null)
                ParseUsageJson(bundledUsage);
        }

        private void ParseAccountText(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return;

            foreach (var line in output.Split('\n'))
            {
                string t = line.Trim();
                if (TryExtractColon(t, "email", out string? email))
                    Email = email!;
                else if (TryExtractColon(t, "plan", out string? plan))
                    Plan = FormatPlan(plan!);
                else if (TryExtractColon(t, "auth", out string? auth))
                    AuthMethod = FormatAuthMethod(auth!);
                else if (TryExtractColon(t, "org", out string? org))
                    Organization = org;
            }

            HasAccountInfo = !string.IsNullOrEmpty(Email);
        }

        private void ParseUsageJson(JObject obj)
        {
            var session = obj["session"] ?? obj["sessionUsage"] ?? obj["hourly"];
            var weekly = obj["weekly"] ?? obj["weeklyUsage"];

            if (session != null)
            {
                double pct = session["percent"]?.Value<double>()
                             ?? session["percentage"]?.Value<double>()
                             ?? -1;
                if (pct >= 0)
                {
                    SessionPercent = Math.Min(pct, 100);
                    SessionPercentLabel = $"{pct:0}%";
                    SessionLabel = session["label"]?.ToString() ?? "Session (5hr)";
                    SessionResetLabel = FormatResetLabel(
                        session["resetsIn"]?.ToString() ?? session["resetAt"]?.ToString() ?? "");
                    HasRateLimitData = true;
                }
            }

            if (weekly != null)
            {
                double pct = weekly["percent"]?.Value<double>()
                             ?? weekly["percentage"]?.Value<double>()
                             ?? -1;
                if (pct >= 0)
                {
                    WeeklyPercent = Math.Min(pct, 100);
                    WeeklyPercentLabel = $"{pct:0}%";
                    WeeklyLabel = weekly["label"]?.ToString() ?? "Weekly (7 day)";
                    WeeklyResetLabel = FormatResetLabel(
                        weekly["resetsIn"]?.ToString() ?? weekly["resetAt"]?.ToString() ?? "");
                    HasRateLimitData = true;
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool TryExtractColon(string line, string keyPrefix, out string? value)
        {
            if (!line.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            { value = null; return false; }

            int colon = line.IndexOf(':');
            if (colon < 0)
            { value = null; return false; }

            value = line.Substring(colon + 1).Trim();
            return !string.IsNullOrEmpty(value);
        }

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
                "claude_ai" or "oauth" or "browser" => "Claude AI",
                "api_key" or "apikey" => "API Key",
                _ => raw
            };

        private static string FormatResetLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("Reset", StringComparison.OrdinalIgnoreCase)) return raw;
            if (TimeSpan.TryParse(raw, out var ts))
                return $"Resets in {FormatTimeSpan(ts)}";
            if (DateTime.TryParse(raw, out var dt))
            {
                var remaining = dt - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    return $"Resets in {FormatTimeSpan(remaining)}";
            }
            return raw;
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

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = fullArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                };

                using var process = Process.Start(psi);
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
