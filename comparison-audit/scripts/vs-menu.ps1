# Background-safe Visual Studio menu driving.
#
# Walks the real menu bar with UI Automation and invokes items by name, expanding submenus via
# ExpandCollapsePattern. No SetForegroundWindow, no physical mouse - so this keeps working while
# the user is in another application, and never steals their focus.
#
# Usage:
#   . .\uia-lib.ps1 ; . .\vs-menu.ps1
#   Invoke-VsMenuPath -ProcessId 1234 -Path @('View','Other Windows','Claude Code')

function Get-VsMenuBar {
    param([Parameter(Mandatory=$true)]$Root, [int]$TimeoutMs = 20000)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuBar)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $bars = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
        foreach ($b in $bars) {
            # The application menu bar is the one that actually contains File/Edit/View.
            $c = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'View')
            if ($b.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)) { return $b }
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Could not find the Visual Studio menu bar (no MenuBar containing a "View" item).'
}

function Expand-UiaMenuItem {
    param([Parameter(Mandatory=$true)]$Element)
    $p = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) {
        if ($p.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $p.Expand()
        }
        return $true
    }
    return $false
}

function Find-MenuChild {
    param([Parameter(Mandatory=$true)]$Parent, [Parameter(Mandatory=$true)][string]$Name, [int]$TimeoutMs = 6000)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        # Menu popups are hosted out-of-tree once expanded, so search descendants, not children.
        $all = $Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::MenuItem)))
        foreach ($e in $all) {
            try {
                $n = $e.Current.Name
                if ($n -eq $Name -or $n -replace '&','' -eq $Name) { return $e }
            } catch {}
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-VsMenuPath {
    # $Path: ordered menu item names, e.g. @('View','Other Windows','Claude Code').
    param([Parameter(Mandatory=$true)][int]$ProcessId, [Parameter(Mandatory=$true)][string[]]$Path)
    $root = Get-MainWindowByPid -ProcessId $ProcessId
    $bar  = Get-VsMenuBar -Root $root
    $node = $bar
    for ($i = 0; $i -lt $Path.Count; $i++) {
        $name = $Path[$i]
        # Top level comes from the bar; deeper levels come from the desktop root, because an
        # expanded VS menu popup is its own top-level window and is no longer a descendant of
        # the item that opened it.
        $searchRoot = if ($i -eq 0) { $bar } else { [System.Windows.Automation.AutomationElement]::RootElement }
        $item = Find-MenuChild -Parent $searchRoot -Name $name
        if (-not $item) { throw "Menu item '$name' not found (path so far: $($Path[0..$i] -join ' > '))" }
        if ($i -lt $Path.Count - 1) {
            if (-not (Expand-UiaMenuItem -Element $item)) {
                throw "Menu item '$name' cannot be expanded."
            }
            Start-Sleep -Milliseconds 400
        } else {
            Invoke-UiaClick -Element $item
        }
        $node = $item
    }
}
