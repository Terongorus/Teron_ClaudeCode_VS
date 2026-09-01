using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// The pieces of real user input that WPF does not let a caller construct.
    /// <para>
    /// This is the heart of TEST-1. The backlog's acceptance criterion was "driven live on our side,
    /// or proven genuinely unreachable", and out-of-process was measured to be the latter: the
    /// <c>IDropTarget</c> that <c>RegisterDragDrop</c> stashes in the <c>OleDropTargetInterface</c>
    /// window property is a raw pointer into the owning process, and dereferencing it from another
    /// process is an access violation (measured 2026-08-31 - the probe process died with
    /// 0xC0000005). A second idea died with it: that property is present on <em>every</em> WPF
    /// HwndSource, including one whose content has <c>AllowDrop="False"</c>, so its presence is not
    /// evidence that anything accepts drops. Synthetic mouse input would work and is banned here -
    /// it steals the pointer from whoever is using the machine.
    /// </para>
    /// <para>
    /// So the drop is raised in-process instead, on the real control, through the real routed
    /// event, which means the XAML wiring is under test and not just the handler body.
    /// <see cref="DragEventArgs"/> has no public constructor; the internal one is found by shape
    /// rather than by a hard-coded signature so a future WPF servicing change surfaces as a clear
    /// error here instead of a mystery.
    /// </para>
    /// </summary>
    internal static class WpfInput
    {
        private static readonly ConstructorInfo s_dragEventArgsCtor = FindDragEventArgsConstructor();

        private static ConstructorInfo FindDragEventArgsConstructor()
        {
            ConstructorInfo[] candidates = typeof(DragEventArgs)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);

            ConstructorInfo? match = candidates.FirstOrDefault(c =>
            {
                ParameterInfo[] p = c.GetParameters();
                return p.Length == 5
                    && p[0].ParameterType == typeof(IDataObject)
                    && p[1].ParameterType == typeof(DragDropKeyStates)
                    && p[2].ParameterType == typeof(DragDropEffects)
                    && p[3].ParameterType == typeof(DependencyObject)
                    && p[4].ParameterType == typeof(Point);
            });

            if (match == null)
            {
                string shapes = string.Join("; ", candidates.Select(c =>
                    string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name))));

                throw new InvalidOperationException(
                    "DragEventArgs no longer has the expected internal constructor " +
                    "(IDataObject, DragDropKeyStates, DragDropEffects, DependencyObject, Point). " +
                    "Found: " + shapes);
            }

            return match;
        }

        /// <summary>Builds a real <see cref="DragEventArgs"/> for one of the drag routed events.</summary>
        public static DragEventArgs DragArgs(RoutedEvent routedEvent, IDataObject data, UIElement target)
        {
            var args = (DragEventArgs)s_dragEventArgsCtor.Invoke(new object[]
            {
                data,
                DragDropKeyStates.LeftMouseButton,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.None,
                target,
                new Point(10, 10),
            });

            args.RoutedEvent = routedEvent;
            args.Source = target;
            return args;
        }

        /// <summary>Raises one drag routed event on <paramref name="target"/> and hands back the args it saw.</summary>
        public static DragEventArgs RaiseDrag(UIElement target, RoutedEvent routedEvent, IDataObject data)
        {
            DragEventArgs args = DragArgs(routedEvent, data, target);
            target.RaiseEvent(args);
            return args;
        }

        /// <summary>
        /// Presses a button the way an accessibility client does, via its automation peer. Phase I
        /// established why that matters: a control only reachable by mouse is a control a keyboard
        /// or screen-reader user cannot use, so the test should go through the same door they do.
        /// </summary>
        public static void InvokeByPeer(System.Windows.Controls.Primitives.ButtonBase button)
        {
            var peer = (ButtonAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(button);
            var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
            invoke.Invoke();

            // WPF's IInvokeProvider.Invoke does not raise Click inline - it posts it to the
            // dispatcher, so a caller that asserted immediately would see the state from before
            // the press. Draining at SystemIdle lets everything queued above it run first.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
        }

        /// <summary>Every descendant of <paramref name="root"/> of type <typeparamref name="T"/>, in visual-tree order.</summary>
        public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);

                if (child is T typed)
                    yield return typed;

                foreach (T nested in Descendants<T>(child))
                    yield return nested;
            }
        }
    }
}
