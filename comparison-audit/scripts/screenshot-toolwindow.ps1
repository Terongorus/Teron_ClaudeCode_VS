# NEGATIVE RESULT - kept so nobody re-derives it. This approach DOES NOT WORK on Visual Studio 18.
#
# The problem it was written to solve: PrintWindow(vsMainFrameHwnd, ...) returns a blank bitmap
# whenever the VS window is occluded by another application. DWM has no redirection surface to
# hand back, and the call still reports success. That is unavoidable for this harness, because the
# standing constraint is that it must never call SetForegroundWindow or steal focus from whatever
# the user is doing. WPF Popups are unaffected - they are their own top-level always-on-top HWNDs -
# which is why picker/popup screenshots keep working while full-frame ones come back empty.
#
# The idea was that a docked tool window would be a child HWND with its own client area that
# PrintWindow could render independently. On VS 18 it is not: the shell is WPF end to end, and
# walking up the UIA tree from any element inside the chat control finds no intermediate HWND -
# the first and only NativeWindowHandle is the 1920x1080 main frame itself.
#
# Measured on 2026-08-29 against the Exp instance, all four PrintWindow flag values, sampling a
# grid of pixels from each result:
#     flag=0 ok=True  distinct colours=3
#     flag=1 ok=True  distinct colours=7
#     flag=2 ok=True  distinct colours=4   (PW_RENDERFULLCONTENT)
#     flag=3 ok=True  distinct colours=7
# i.e. every flag "succeeds" and every flag returns an essentially blank image.
#
# WHAT TO DO INSTEAD for a surface that lives inside the frame (transcript, permission cards,
# attachment chips): assert on the rendered text through UIA. For acceptance criteria phrased as
# "the card states the full path" or "the collapsed row shows the failure count", an exact string
# assertion is stronger evidence than a screenshot anyway. See phase-c-verify.ps1 and the
# live-turn probes for that pattern. Screenshots remain viable for anything in a Popup.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$AnchorAutomationId = 'InputBox'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Tw {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-lib.ps1')

$root = Get-MainWindowByPid -ProcessId $ProcessId
$anchor = Find-ByAutomationId -Root $root -AutomationId $AnchorAutomationId -TimeoutMs 5000
if (-not $anchor) { throw "anchor '$AnchorAutomationId' not found" }

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$node = $anchor
$handles = @()
for ($i = 0; $i -lt 25 -and $null -ne $node; $i++) {
    $h = [IntPtr]$node.Current.NativeWindowHandle
    if ($h -ne [IntPtr]::Zero -and $handles -notcontains $h) { $handles += $h }
    $node = $walker.GetParent($node)
}
"HWNDs found on the ancestor chain: $($handles.Count)"
foreach ($h in $handles) {
    $r = New-Object Win32Tw+RECT
    [void][Win32Tw]::GetWindowRect($h, [ref]$r)
    "  $h  $($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
}
if ($handles.Count -le 1) {
    Write-Warning 'Only the main frame owns an HWND - see the header. This capture will be blank.'
}

$target = $handles[0]
$r = New-Object Win32Tw+RECT
[void][Win32Tw]::GetWindowRect($target, [ref]$r)
$w = $r.Right - $r.Left
$h2 = $r.Bottom - $r.Top

$bmp = New-Object System.Drawing.Bitmap($w, $h2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win32Tw]::PrintWindow($target, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"PrintWindow returned $ok -> $OutFile (expect a blank image on VS 18; see header)"
