# Minimal UI Automation helper library. Dot-source, then use Get-MainWindow / Find-ByAutomationId /
# Find-ByName / Invoke-UiaClick / Set-UiaValue / Wait-Uia.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindowByPid {
    param([Parameter(Mandatory=$true)][int]$ProcessId, [int]$TimeoutMs = 20000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        try {
            $p = Get-Process -Id $ProcessId -ErrorAction Stop
            if ($p.MainWindowHandle -ne 0) {
                return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
            }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for main window of PID $ProcessId"
}

function Find-ByAutomationId {
    param(
        [Parameter(Mandatory=$true)]$Root,
        [Parameter(Mandatory=$true)][string]$AutomationId,
        [int]$TimeoutMs = 5000
    )
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $el = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Find-ByName {
    param(
        [Parameter(Mandatory=$true)]$Root,
        [Parameter(Mandatory=$true)][string]$Name,
        [string]$ControlType = "",
        [int]$TimeoutMs = 5000,
        [switch]$Contains
    )
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        if ($Contains) {
            $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($el in $all) {
                try {
                    if ($el.Current.Name -and $el.Current.Name.Contains($Name)) {
                        if (-not $ControlType -or $el.Current.ControlType.ProgrammaticName -like "*$ControlType*") { return $el }
                    }
                } catch {}
            }
        } else {
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
            $el = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
            if ($el) { return $el }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-UiaClick {
    # Purely programmatic - never moves the physical cursor or requires the window to be
    # foreground/visible. Tries the patterns a WPF Button/ToggleButton/ListBoxItem actually
    # implements, then falls back to the accessibility "default action" (the same underlying
    # action a real double-click would trigger), which still requires no physical input.
    param([Parameter(Mandatory=$true)]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        $pattern.Toggle()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
        return
    }
    if ($Element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -or
        $Element.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane) {
        # Not itself invocable (e.g. a label TextBlock inside a CheckBox) - walk up to the
        # nearest ancestor that IS a real checkbox/radio/button control and retry on that.
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $ancestor = $walker.GetParent($Element)
        $hops = 0
        while ($ancestor -and $hops -lt 5) {
            $ct = $ancestor.Current.ControlType
            if ($ct -eq [System.Windows.Automation.ControlType]::CheckBox -or
                $ct -eq [System.Windows.Automation.ControlType]::RadioButton -or
                $ct -eq [System.Windows.Automation.ControlType]::Button) {
                Invoke-UiaClick -Element $ancestor
                return
            }
            $ancestor = $walker.GetParent($ancestor)
            $hops++
        }
    }
    try {
        if ($Element.TryGetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern, [ref]$pattern)) {
            $pattern.DoDefaultAction()
            return
        }
    } catch { }
    throw "Element '$($Element.Current.Name)' [$($Element.Current.ControlType.ProgrammaticName)] supports no invocable UIA pattern (Invoke/Toggle/SelectionItem/LegacyIAccessible) - cannot click it without physical mouse simulation."
}

function Set-UiaValue {
    param([Parameter(Mandatory=$true)]$Element, [Parameter(Mandatory=$true)][string]$Value)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        $pattern.SetValue($Value)
        return
    }
    throw "Element does not support ValuePattern"
}

Add-Type -ErrorAction SilentlyContinue @"
using System;
using System.Runtime.InteropServices;
public class WmInputSender {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint WM_CHAR = 0x0102;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
}
"@

function Send-WmChar {
    # Sends a single character directly to a window's message queue via WM_CHAR - NOT physical
    # SendInput/mouse_event. Delivered straight to that HWND regardless of OS-level foreground
    # focus, so it never steals input from the user and never touches other windows. Needed
    # because WPF TextBox's ValuePattern.SetValue does not reliably reposition the caret, which
    # breaks any caret-relative detection logic (e.g. "@" mention triggers, "/" at start-of-line)
    # that depends on TextChangedEventArgs reflecting a real, incremental keystroke.
    param([Parameter(Mandatory=$true)]$MainWindowElement, [Parameter(Mandatory=$true)][char]$Char)
    $hwnd = [IntPtr]$MainWindowElement.Current.NativeWindowHandle
    [WmInputSender]::SendMessage($hwnd, [WmInputSender]::WM_CHAR, [IntPtr]([int]$Char), [IntPtr]::Zero) | Out-Null
}

function Send-WmClick {
    # Sends WM_LBUTTONDOWN/UP directly to a window at the client coordinates of a target
    # AutomationElement - not physical mouse_event/SendInput, so the real cursor never moves and
    # no OS-level focus is stolen. Needed for controls whose "choose/activate" behavior is wired
    # to a mouse routed event specifically (e.g. PreviewMouseLeftButtonUp on a ListBoxItem) rather
    # than a UIA-invocable pattern - SelectionItemPattern.Select() only sets IsSelected, it does
    # not raise that routed event, so click-driven "choose this item" handlers never fire from it.
    param([Parameter(Mandatory=$true)]$MainWindowElement, [Parameter(Mandatory=$true)]$TargetElement)
    $hwnd = [IntPtr]$MainWindowElement.Current.NativeWindowHandle
    $winRect = New-Object WmInputSender+RECT
    [WmInputSender]::GetWindowRect($hwnd, [ref]$winRect) | Out-Null
    $rect = $TargetElement.Current.BoundingRectangle
    $clientX = [int](($rect.Left + $rect.Width / 2) - $winRect.Left)
    $clientY = [int](($rect.Top + $rect.Height / 2) - $winRect.Top)
    $lParam = [IntPtr]((($clientY -band 0xFFFF) -shl 16) -bor ($clientX -band 0xFFFF))
    [WmInputSender]::SendMessage($hwnd, [WmInputSender]::WM_LBUTTONDOWN, [IntPtr]1, $lParam) | Out-Null
    Start-Sleep -Milliseconds 50
    [WmInputSender]::SendMessage($hwnd, [WmInputSender]::WM_LBUTTONUP, [IntPtr]0, $lParam) | Out-Null
}

function Get-UiaDoubleClick {
    # Purely programmatic double-click equivalent via LegacyIAccessiblePattern.DoDefaultAction()
    # (the accessibility "default action" - Enter/double-click semantics) - no cursor movement,
    # no foreground/visibility requirement. Falls back to SelectionItemPattern.Select() (selects
    # but does not "open"; fine for rows whose open action is driven by selection alone).
    param([Parameter(Mandatory=$true)]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.LegacyIAccessiblePattern]::Pattern, [ref]$pattern)) {
        $pattern.DoDefaultAction()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
        return
    }
    throw "Element '$($Element.Current.Name)' supports neither LegacyIAccessiblePattern nor SelectionItemPattern - cannot double-click it without physical mouse simulation."
}

function Get-DocumentTexts {
    # Reads the text of every FlowDocument rendered by the process.
    #
    # FOUND THE HARD WAY IN PHASE D, and it invalidates an assumption the earlier scripts were
    # built on: enumerating elements and reading .Current.Name does NOT see markdown content.
    # Everything the MarkdownViewer renders - assistant replies, thinking blocks, tool output,
    # the /btw answer - lives in a FlowDocument, which UIA exposes as a ControlType.Document
    # supporting TextPattern, with an empty Name. So a Name-only sweep can report "the card
    # rendered" while being structurally blind to whether it rendered anything *in* it.
    #
    # Use this whenever the assertion is about model-produced or markdown-rendered text. Name
    # enumeration remains correct for chrome: labels, buttons, menu rows, status lines.
    param([Parameter(Mandatory=$true)][int]$ProcessId, [int]$MaxChars = 4000)

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

    $seen = @{}
    foreach ($e in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($e.Current.ControlType -ne [System.Windows.Automation.ControlType]::Document) { continue }
        $pattern = $null
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) { continue }
        try { $text = $pattern.DocumentRange.GetText($MaxChars) } catch { continue }
        if (-not $text -or -not $text.Trim()) { continue }
        # WPF hosts each document twice in the tree (viewer + document); de-duplicate.
        $key = $text.Trim()
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $key
    }
}
