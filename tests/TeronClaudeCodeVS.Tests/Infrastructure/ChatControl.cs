using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.ViewModels;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// A real <see cref="ClaudeCodeChatControl"/>, laid out, with no window and no Visual Studio.
    /// <para>
    /// Nothing is stubbed. The control's own constructor runs, its XAML is parsed, its bindings are
    /// live and its data templates are applied - the chips this returns are the ones a user would
    /// see. Two things are deliberately absent: no window is ever shown (measured layout is enough
    /// to realise the item containers, and showing one would put pixels on the user's screen), and
    /// <c>Loaded</c> never fires, which is what keeps <c>ClaudeCodePackage.Instance</c> - null out
    /// here - from being consulted.
    /// </para>
    /// </summary>
    internal sealed class ChatControl
    {
        public ClaudeCodeChatControl Control { get; }

        public ChatSessionViewModel Vm { get; }

        private ChatControl(ClaudeCodeChatControl control)
        {
            Control = control;
            Vm = (ChatSessionViewModel)control.DataContext;
        }

        /// <summary>Must be called on an STA thread - see <see cref="Sta"/>.</summary>
        public static ChatControl Create(double width = 900, double height = 900)
        {
            var control = new ClaudeCodeChatControl();
            var harness = new ChatControl(control);
            harness.Relayout(width, height);
            return harness;
        }

        /// <summary>
        /// Forces a full layout pass. Item containers for a newly added chip do not exist until
        /// this runs, so any assertion about what is on screen has to come after it.
        /// </summary>
        public void Relayout(double width = 900, double height = 900)
        {
            Control.Measure(new Size(width, height));
            Control.Arrange(new Rect(0, 0, width, height));
            Control.UpdateLayout();
        }
    }

    /// <summary>Real files and real bitmaps for the drop tests, cleaned up with the test class.</summary>
    internal sealed class ScratchFiles : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "teron-claude-tests", Guid.NewGuid().ToString("N"));

        public ScratchFiles() => Directory.CreateDirectory(_root);

        public string WriteText(string name, string content)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, content);
            return path;
        }

        public string WriteBytes(string name, byte[] content)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        /// <summary>Writes a genuine PNG of the given size - decoded for real by the drop path.</summary>
        public string WritePng(string name, int pixelWidth, int pixelHeight)
            => WriteBytes(name, EncodePng(SolidBitmap(pixelWidth, pixelHeight)));

        public static BitmapSource SolidBitmap(int pixelWidth, int pixelHeight, byte red = 0xC1)
        {
            int stride = pixelWidth * 4;
            var pixels = new byte[stride * pixelHeight];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 0] = 0x3A;   // B
                pixels[i + 1] = 0x5E;   // G
                pixels[i + 2] = red;    // R
                pixels[i + 3] = 0xFF;   // A
            }

            var bitmap = BitmapSource.Create(
                pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        public static byte[] EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a still-open handle is not a test failure */ }
        }
    }
}
