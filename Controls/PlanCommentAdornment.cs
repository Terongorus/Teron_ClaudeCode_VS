using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TeronClaudeCodeVS.ViewModels;

namespace TeronClaudeCodeVS.Controls
{
    /// <summary>
    /// MEF entry point for the plan-preview "select text to add comment" feature (see docs/Phase 4
    /// and the plan's item 4). This repo had no existing MEF editor-extensibility component before
    /// this - the manifest previously declared only a VsPackage asset, not a MefComponent asset (now
    /// added in source.extension.vsixmanifest). Confirmed live 2026-08-27 that this listener does
    /// get instantiated by VS (Debugger.Log trace appeared with a fresh Debug build/symbols loaded);
    /// Debugger.Log is used instead of Debug.WriteLine/Trace.WriteLine because those are
    /// [Conditional("DEBUG")]/[Conditional("TRACE")] and get compiled out entirely if the deployed
    /// DLL happens to be a Release build - Debugger.Log always executes.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class PlanCommentTextViewListener : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(PlanCommentAdornmentManager.LayerName)]
        [Order(After = PredefinedAdornmentLayers.Selection)]
        [Order(After = PredefinedAdornmentLayers.Text)]
#pragma warning disable CS0649 // never assigned - MEF fills this in via the [Export] attribute alone
        public AdornmentLayerDefinition? PlanCommentAdornmentLayerDefinition;
#pragma warning restore CS0649

        public void TextViewCreated(IWpfTextView textView)
        {
            Debugger.Log(0, "TeronClaudeCodeVS",
                "[TeronClaudeCodeVS] PlanCommentTextViewListener.TextViewCreated fired - MEF editor component is active.\n");

            string? filePath = TryGetFilePath(textView);
            if (string.IsNullOrEmpty(filePath))
                return;

            // Attaches unconditionally for every document view of this file; the manager itself
            // checks PlanCommentRegistry on every selection change so it only shows UI while this
            // file path is a currently-pending plan approval (the file can stay open in VS after
            // the card resolves, at which point the affordance should stop appearing).
            new PlanCommentAdornmentManager(textView, filePath!);
        }

        private static string? TryGetFilePath(IWpfTextView textView) =>
            textView.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document)
                ? document.FilePath
                : null;
    }

    /// <summary>
    /// Per-view controller for the plan-comment affordance: shows a floating "Add Comment" button
    /// near a non-empty selection (only while PlanCommentRegistry confirms this view's file is a
    /// pending plan approval), and a small popup composer on click. Submitted comments are routed to
    /// the owning PlanApprovalViewModel purely through PlanCommentRegistry.SubmitComment - this
    /// class has no direct reference to any ChatSessionViewModel, keeping the MEF editor component
    /// decoupled from the chat ViewModel layer. A persistent highlight adornment marks each
    /// already-submitted comment's span so multiple simultaneous comments across the document stay
    /// visually distinguishable (confirmed live, 2026-08-27, that the real extension supports
    /// commenting on several separate passages before submitting feedback).
    ///
    /// Known simplification: the floating button and the persistent highlight are positioned from
    /// the selection's start/end character bounds on a single line. A selection or comment spanning
    /// multiple wrapped/logical lines will render its highlight as one (visually stretched)
    /// rectangle rather than one rectangle per line - acceptable for a first pass given item 4's own
    /// note that a fully polished preview is out of scope; needs live verification either way.
    /// </summary>
    internal sealed class PlanCommentAdornmentManager
    {
        public const string LayerName = "TeronClaudeCodePlanCommentLayer";

        private readonly IWpfTextView _view;
        private readonly IAdornmentLayer _layer;
        private readonly string _filePath;

        private Button? _addCommentButton;
        private SnapshotSpan? _pendingSelectionSpan;

        private Popup? _composerPopup;
        private TextBox? _composerTextBox;

        private readonly List<(SnapshotSpan Span, Border Highlight)> _commentHighlights = [];

        public PlanCommentAdornmentManager(IWpfTextView view, string filePath)
        {
            _view = view;
            _filePath = filePath;
            _layer = view.GetAdornmentLayer(LayerName);

            _view.Selection.SelectionChanged += OnSelectionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            _view.Selection.SelectionChanged -= OnSelectionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            CloseComposer();
        }

        private void OnSelectionChanged(object sender, EventArgs e)
        {
            RemoveAddCommentButton();

            if (_view.Selection.IsEmpty || !PlanCommentRegistry.IsActivePlanFile(_filePath))
                return;

            _pendingSelectionSpan = _view.Selection.SelectedSpans[0];
            ShowAddCommentButton(_pendingSelectionSpan.Value);
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (_addCommentButton != null && _pendingSelectionSpan.HasValue)
                PositionButton(_addCommentButton, _pendingSelectionSpan.Value);

            foreach (var entry in _commentHighlights)
                PositionHighlight(entry.Highlight, entry.Span);
        }

        private void ShowAddCommentButton(SnapshotSpan span)
        {
            _addCommentButton = new Button
            {
                Content = "💬 Add Comment",
                Padding = new Thickness(10, 5, 10, 5),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 14
            };
            ApplyToolWindowTheme(_addCommentButton);
            _addCommentButton.Click += (s, e) => ShowComposer(span);

            _layer.AddAdornment(AdornmentPositioningBehavior.OwnerControlled, null, null, _addCommentButton, null);
            PositionButton(_addCommentButton, span);
        }

        private void PositionButton(Button button, SnapshotSpan span)
        {
            try
            {
                // Anchored below the line rather than above it - anchoring above clips the button
                // off the top of the editor viewport when the selection is on (or near) the first
                // visible line, since there's no room to render above it (confirmed live, 2026-08-27).
                TextBounds bounds = _view.TextViewLines.GetCharacterBounds(span.End);
                Canvas.SetLeft(button, bounds.Right);
                Canvas.SetTop(button, bounds.Bottom + 4);
            }
            catch (Exception)
            {
                // The span's line may not currently be rendered (scrolled out of view) - leave the
                // button at its last known position rather than throwing out of a layout event.
            }
        }

        // Theme keys instead of hardcoded colors - this button/popup is built in code inside a MEF
        // editor component, so it has no access to the chat control's own XAML resource dictionary
        // (StaticResource brushes there are scoped to that UserControl's visual tree). VsBrushes'
        // theme keys work from anywhere in the process via SetResourceReference and auto-update on
        // theme change, matching the pattern already used in DiffViewer.xaml.cs.
        private static void ApplyToolWindowTheme(Border border)
        {
            border.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            border.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        }

        private static void ApplyToolWindowTheme(Control control)
        {
            control.SetResourceReference(Control.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
            control.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            control.SetResourceReference(Control.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        }

        private void RemoveAddCommentButton()
        {
            if (_addCommentButton == null) return;
            _layer.RemoveAdornment(_addCommentButton);
            _addCommentButton = null;
            _pendingSelectionSpan = null;
        }

        private void ShowComposer(SnapshotSpan span)
        {
            CloseComposer();

            _composerTextBox = new TextBox
            {
                Width = 340,
                Height = 100,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(1),
                FontSize = 14,
                Padding = new Thickness(6)
            };
            ApplyToolWindowTheme(_composerTextBox);

            Button addButton = new()
            {
                Content = "Add Comment", Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 5, 10, 5), FontSize = 13
            };
            Button cancelButton = new()
            {
                Content = "Cancel", Margin = new Thickness(0, 6, 8, 0),
                Padding = new Thickness(10, 5, 10, 5), FontSize = 13
            };
            ApplyToolWindowTheme(addButton);
            ApplyToolWindowTheme(cancelButton);
            addButton.Click += (s, e) => SubmitComment(span);
            cancelButton.Click += (s, e) => CloseComposer();

            StackPanel buttonRow = new()
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(addButton);

            StackPanel content = new() { Margin = new Thickness(10) };
            content.Children.Add(_composerTextBox);
            content.Children.Add(buttonRow);

            Border border = new()
            {
                BorderThickness = new Thickness(1),
                Child = content
            };
            ApplyToolWindowTheme(border);

            TextBounds bounds = _view.TextViewLines.GetCharacterBounds(span.End);
            Point screenPoint = _view.VisualElement.PointToScreen(new Point(bounds.Right, bounds.Bottom));

            _composerPopup = new Popup
            {
                Child = border,
                Placement = PlacementMode.Absolute,
                HorizontalOffset = screenPoint.X,
                VerticalOffset = screenPoint.Y,
                StaysOpen = true,
                IsOpen = true
            };

            _composerTextBox.Focus();
        }

        private void SubmitComment(SnapshotSpan span)
        {
            string commentText = _composerTextBox?.Text.Trim() ?? "";
            CloseComposer();
            if (commentText.Length == 0) return;

            string excerpt = span.GetText();
            AddCommentHighlight(span);
            RemoveAddCommentButton();
            _view.Selection.Clear();

            PlanCommentRegistry.SubmitComment(_filePath, excerpt, commentText);
        }

        private void CloseComposer()
        {
            _composerPopup?.IsOpen = false;
            _composerPopup = null;
            _composerTextBox = null;
        }

        private void AddCommentHighlight(SnapshotSpan span)
        {
            Border highlight = new()
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 255, 220, 60)),
                IsHitTestVisible = false
            };
            _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, highlight, null);
            _commentHighlights.Add((span, highlight));
            PositionHighlight(highlight, span);
        }

        private void PositionHighlight(Border highlight, SnapshotSpan span)
        {
            try
            {
                TextBounds startBounds = _view.TextViewLines.GetCharacterBounds(span.Start);
                SnapshotPoint endPoint = new(span.Snapshot,
                    Math.Max(span.Start.Position, span.End.Position - 1));
                TextBounds endBounds = _view.TextViewLines.GetCharacterBounds(endPoint);

                Canvas.SetLeft(highlight, startBounds.Left);
                Canvas.SetTop(highlight, startBounds.Top);
                highlight.Width = Math.Max(2, endBounds.Right - startBounds.Left);
                highlight.Height = startBounds.Height;
            }
            catch (Exception)
            {
                // Same rationale as PositionButton - the span's line may not be currently rendered.
            }
        }
    }
}
