using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// TEST-1, and with it rows A1-A3 of the manual checklist.
    /// <para>
    /// These drive the composer's real paste and drop paths on the real control: the routed events
    /// the XAML subscribes to, carrying real <see cref="DataObject"/>s with real bitmaps and real
    /// files on disk, asserted against the chips the data templates actually produce. See
    /// <see cref="WpfInput"/> for why this is in-process rather than driven from outside.
    /// </para>
    /// <para>
    /// Rigor rule #6 applies throughout: every assertion that something is refused is paired with
    /// one showing the same path accepts what it should, so a handler that had simply stopped
    /// running could not pass.
    /// </para>
    /// </summary>
    public sealed class AttachmentTests : IDisposable
    {
        private readonly ScratchFiles _files = new ScratchFiles();

        public void Dispose() => _files.Dispose();

        // ─── Paste (A1) ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Pasting_an_image_stages_a_chip_and_swallows_the_paste()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                harness.Control.InputBox.Text = "before";

                var data = new DataObject();
                data.SetImage(ScratchFiles.SolidBitmap(1920, 1080));

                var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.Bitmap)
                {
                    RoutedEvent = DataObject.PastingEvent,
                };
                harness.Control.InputBox.RaiseEvent(args);

                PendingImageAttachment chip = Assert.Single(harness.Vm.PendingImages);
                Assert.Equal("Pasted image", chip.Name);
                Assert.Equal("1920×1080", chip.DimensionsText);
                Assert.NotEmpty(chip.Base64Png);

                // The image must not ALSO land in the textbox as pasted content.
                Assert.True(args.CommandCancelled);
                Assert.Equal("before", harness.Control.InputBox.Text);
            });
        }

        [Fact]
        public void Pasting_text_is_left_alone()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();

                var data = new DataObject();
                data.SetText("just some text");

                var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.UnicodeText)
                {
                    RoutedEvent = DataObject.PastingEvent,
                };
                harness.Control.InputBox.RaiseEvent(args);

                // The positive control for the test above: the handler ran, and declined to act.
                Assert.Empty(harness.Vm.PendingImages);
                Assert.False(args.CommandCancelled);
            });
        }

        [Fact]
        public void Pasted_image_dimensions_are_the_full_resolution_not_the_chip_size()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();

                var data = new DataObject();
                data.SetImage(ScratchFiles.SolidBitmap(37, 141));

                harness.Control.InputBox.RaiseEvent(
                    new DataObjectPastingEventArgs(data, false, DataFormats.Bitmap) { RoutedEvent = DataObject.PastingEvent });

                // 34px is the chip's display size; a chip reporting 34x34 would mean the label is
                // describing the thumbnail rather than what actually gets sent.
                Assert.Equal("37×141", Assert.Single(harness.Vm.PendingImages).DimensionsText);
            });
        }

        // ─── Drop (A2) ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Dropping_an_image_file_keeps_its_real_name()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string path = _files.WritePng("screenshot.png", 640, 480);

                Drop(harness, FileDrop(path));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingImages.Count == 1), "no image chip appeared");

                PendingImageAttachment chip = harness.Vm.PendingImages[0];

                // UX-9's actual requirement: a dropped file is named, unlike a clipboard paste.
                Assert.Equal("screenshot.png", chip.Name);
                Assert.Equal("640×480", chip.DimensionsText);
            });
        }

        [Fact]
        public void Dropping_a_code_file_stages_its_text()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string path = _files.WriteText("Program.cs", "class Program { }");

                Drop(harness, FileDrop(path));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingFiles.Count == 1), "no file chip appeared");

                PendingFileAttachment chip = harness.Vm.PendingFiles[0];
                Assert.Equal("Program.cs", chip.Title);
                Assert.False(chip.IsPdf);
                Assert.Equal("class Program { }", chip.Content);
                Assert.Equal("\U0001F4C4", chip.Icon);
            });
        }

        [Fact]
        public void Dropping_a_pdf_is_staged_as_bytes_with_its_own_glyph()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                byte[] bytes = { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 };   // "%PDF-1.7"
                string path = _files.WriteBytes("spec.pdf", bytes);

                Drop(harness, FileDrop(path));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingFiles.Count == 1), "no file chip appeared");

                PendingFileAttachment chip = harness.Vm.PendingFiles[0];
                Assert.True(chip.IsPdf);
                Assert.Equal(Convert.ToBase64String(bytes), chip.Content);
                Assert.Equal("\U0001F4D5", chip.Icon);
                Assert.NotEqual(chip.Icon, new PendingFileAttachment("x.cs", false, "").Icon);
            });
        }

        [Fact]
        public void An_unsupported_file_in_a_multi_file_drop_is_skipped_quietly()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string good = _files.WriteText("notes.md", "# notes");
                string unsupported = _files.WriteBytes("tool.exe", new byte[] { 0x4D, 0x5A });
                string missing = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".cs");

                Drop(harness, FileDrop(unsupported, missing, good));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingFiles.Count == 1), "the supported file never staged");

                // Skipped silently, as baseline's own webview does for a mixed drop - and the one
                // good file still arrives, which is what proves the skip was selective rather than
                // the whole drop being abandoned at the first bad entry.
                Assert.Equal("notes.md", harness.Vm.PendingFiles[0].Title);
                Assert.Empty(harness.Vm.PendingImages);
            });
        }

        [Fact]
        public void Dropping_a_raw_bitmap_stages_it_as_an_image()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();

                var data = new DataObject();
                data.SetImage(ScratchFiles.SolidBitmap(64, 64));

                Drop(harness, data);
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingImages.Count == 1), "no image chip appeared");
                Assert.Equal("64×64", harness.Vm.PendingImages[0].DimensionsText);
            });
        }

        [Fact]
        public void Drag_feedback_offers_copy_only_for_data_we_accept()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string path = _files.WriteText("readme.md", "hi");

                DragEventArgs droppable = WpfInput.RaiseDrag(
                    harness.Control.InputAreaBorder, UIElement.PreviewDragOverEvent, FileDrop(path));
                Assert.Equal(DragDropEffects.Copy, droppable.Effects);

                var text = new DataObject();
                text.SetText("not a file");

                DragEventArgs rejected = WpfInput.RaiseDrag(
                    harness.Control.InputAreaBorder, UIElement.PreviewDragOverEvent, text);
                Assert.Equal(DragDropEffects.None, rejected.Effects);
            });
        }

        [Fact]
        public void Drag_enter_highlights_the_composer_and_leaving_clears_it()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string path = _files.WriteText("readme.md", "hi");

                object? resting = harness.Control.InputAreaBorder.ReadLocalValue(Border.BorderBrushProperty);

                WpfInput.RaiseDrag(harness.Control.InputAreaBorder, UIElement.DragEnterEvent, FileDrop(path));
                object? highlighted = harness.Control.InputAreaBorder.ReadLocalValue(Border.BorderBrushProperty);
                Assert.NotEqual(resting, highlighted);
                Assert.Equal(harness.Control.FindResource("ClaudeAccentBrush"), highlighted);

                WpfInput.RaiseDrag(harness.Control.InputAreaBorder, UIElement.DragLeaveEvent, FileDrop(path));
                Assert.Equal(
                    DependencyProperty.UnsetValue,
                    harness.Control.InputAreaBorder.ReadLocalValue(Border.BorderBrushProperty));
            });
        }

        // ─── The chips as rendered, and removing one (A3) ────────────────────────────────────────

        [Fact]
        public void A_staged_file_actually_renders_a_chip_showing_its_name()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string path = _files.WriteText("appsettings.json", "{}");

                Drop(harness, FileDrop(path));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingFiles.Count == 1), "no file chip appeared");

                harness.Relayout();

                // Not "the collection has an item" - the visual tree really contains a TextBlock
                // showing the name, which means the ItemsControl, its template and its bindings all
                // did their work.
                Assert.Contains(
                    WpfInput.Descendants<TextBlock>(harness.Control),
                    t => t.Text == "appsettings.json");
            });
        }

        [Fact]
        public void The_close_glyph_on_a_chip_removes_that_chip_and_only_that_chip()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();
                string first = _files.WriteText("first.md", "one");
                string second = _files.WriteText("second.md", "two");

                Drop(harness, FileDrop(first, second));
                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingFiles.Count == 2), "both files did not stage");

                harness.Relayout();

                PendingFileAttachment target = harness.Vm.PendingFiles.Single(f => f.Title == "first.md");

                Button close = WpfInput.Descendants<Button>(harness.Control)
                    .Single(b => ReferenceEquals(b.Tag, target));

                WpfInput.InvokeByPeer(close);

                PendingFileAttachment survivor = Assert.Single(harness.Vm.PendingFiles);
                Assert.Equal("second.md", survivor.Title);
            });
        }

        [Fact]
        public void Removing_the_last_chip_hides_the_chip_strip()
        {
            Sta.Run(() =>
            {
                var harness = ChatControl.Create();

                var data = new DataObject();
                data.SetImage(ScratchFiles.SolidBitmap(20, 20));
                Drop(harness, data);

                Assert.True(Sta.PumpUntil(() => harness.Vm.PendingImages.Count == 1), "no image chip appeared");
                Assert.True(harness.Vm.HasPendingImages);

                harness.Relayout();
                Button close = WpfInput.Descendants<Button>(harness.Control)
                    .Single(b => ReferenceEquals(b.Tag, harness.Vm.PendingImages[0]));

                WpfInput.InvokeByPeer(close);

                Assert.Empty(harness.Vm.PendingImages);
                Assert.False(harness.Vm.HasPendingImages);
            });
        }

        // ─── helpers ────────────────────────────────────────────────────────────────────────────

        private static DataObject FileDrop(params string[] paths) => new DataObject(DataFormats.FileDrop, paths);

        private static void Drop(ChatControl harness, IDataObject data) =>
            WpfInput.RaiseDrag(harness.Control.InputAreaBorder, UIElement.DropEvent, data);
    }
}
