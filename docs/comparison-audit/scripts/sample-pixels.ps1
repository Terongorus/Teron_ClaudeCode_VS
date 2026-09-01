# Samples the on-screen colour of named UIA elements, background-safe (PrintWindow only - no
# CopyFromScreen, so it works while the window is occluded or the user is in another app).
#
# Used by the Phase B (ST-4) check: the accent must be byte-identical across VS themes while the
# surfaces around it re-derive. Comparing two screenshots by eye cannot prove "identical", so the
# pixels get read.
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string[]]$AutomationIds,
    [string]$Label = ""
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PwSample {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$proc = Get-Process -Id $ProcessId
$hwnd = $proc.MainWindowHandle
$wr = New-Object PwSample+RECT
[PwSample]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$w = $wr.Right - $wr.Left; $h = $wr.Bottom - $wr.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# flag 2 = PW_RENDERFULLCONTENT, required for WPF/DirectComposition surfaces.
[PwSample]::PrintWindow($hwnd, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
foreach ($id in $AutomationIds) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { "{0,-20} NOT FOUND" -f $id; continue }
    $r = $el.Current.BoundingRectangle
    $x = [int](($r.Left + $r.Width / 2) - $wr.Left)
    $y = [int](($r.Top  + $r.Height / 2) - $wr.Top)
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $w -or $y -ge $h) { "{0,-20} OFFSCREEN" -f $id; continue }
    $c = $bmp.GetPixel($x, $y)
    "{0,-20} #{1:X2}{2:X2}{3:X2}  at ({4},{5})  [{6}]" -f $id, $c.R, $c.G, $c.B, $x, $y, $Label
}
$bmp.Dispose()
