using System;
using System.Threading.Tasks;

namespace Antigravity_CLI_GUI.Utilities
{
    public static class TypingAnimator
    {
        private const int DelayMs = 12; // typing speed

        public static async Task AnimateAsync(ChatMessage msg)
        {
            while (msg.Pending.Length > 0)
            {
                // Take 1 character
                msg.Text += msg.Pending[0];
                msg.Pending = msg.Pending.Substring(1);

                await Task.Delay(DelayMs);
            }
        }
    }
}
