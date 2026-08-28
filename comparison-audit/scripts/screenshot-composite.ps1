# Composite screenshot: captures a main window PLUS any WPF Popup windows it currently owns
# (Popups are separate top-level HWNDs - not children - so a single PrintWindow(mainHwnd) call
# never includes them) and pastes each popup at its correct screen-relative position over the
# main window's bitmap. Still fully background-safe: PrintWindow only, no SetForegroundWindow,
# no CopyFromScreen.
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [string]$MainWindowTitleContains = "",
    [Parameter(Mandatory=$true)][string]$OutFile
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class Win32Composite {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public const uint PW_RENDERFULLCONTENT = 0x2;
    public const uint GW_OWNER = 4;

    public static List<IntPtr> FindTopLevelWindowsForProcess(uint pid) {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, lParam) => {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid == pid && IsWindowVisible(hWnd)) {
                RECT r;
                GetWindowRect(hWnd, out r);
                if ((r.Right - r.Left) > 0 && (r.Bottom - r.Top) > 0) {
                    result.Add(hWnd);
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string GetTitle(IntPtr hWnd) {
        int len = GetWindowTextLength(hWnd);
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr hWnd) {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

function Capture-Hwnd {
    param([IntPtr]$Hwnd)
    $rect = New-Object Win32Composite+RECT
    [Win32Composite]::GetWindowRect($Hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { return $null }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [Win32Composite]::PrintWindow($Hwnd, $hdc, [Win32Composite]::PW_RENDERFULLCONTENT)
    $g.ReleaseHdc($hdc)
    if (-not $ok) {
        $hdc2 = $g.GetHdc()
        [Win32Composite]::PrintWindow($Hwnd, $hdc2, 0) | Out-Null
        $g.ReleaseHdc($hdc2)
    }
    $g.Dispose()
    return @{ Bitmap = $bmp; Rect = $rect }
}

$procs = Get-Process -Id $ProcessId
$mainWin = [Win32Composite]::FindTopLevelWindowsForProcess([uint32]$ProcessId) |
    Where-Object { [Win32Composite]::GetTitle($_) -like "*$MainWindowTitleContains*" -and [Win32Composite]::GetTitle($_) -ne "" } |
    Select-Object -First 1

if (-not $mainWin) { Write-Error "Main window not found for PID $ProcessId (title contains '$MainWindowTitleContains')"; exit 1 }

$mainCapture = Capture-Hwnd -Hwnd $mainWin
if (-not $mainCapture) { Write-Error "Failed to capture main window"; exit 1 }

$mainBmp = $mainCapture.Bitmap
$mainRect = $mainCapture.Rect
$gComposite = [System.Drawing.Graphics]::FromImage($mainBmp)

# Find candidate popup/overlay windows: same process, visible, NOT the main window itself,
# with no title (WPF Popups are borderless/titleless), positioned within or overlapping the
# main window's screen bounds.
$allWindows = [Win32Composite]::FindTopLevelWindowsForProcess([uint32]$ProcessId)
$popupCount = 0
foreach ($hw in $allWindows) {
    if ($hw -eq $mainWin) { continue }
    $title = [Win32Composite]::GetTitle($hw)
    $class = [Win32Composite]::GetClass($hw)
    if ($title -ne "") { continue }  # WPF Popups/dropdowns are unnamed
    $popupCapture = Capture-Hwnd -Hwnd $hw
    if (-not $popupCapture) { continue }
    $pr = $popupCapture.Rect
    # Skip degenerate/offscreen or huge (likely unrelated ghost) windows.
    $pw = $pr.Right - $pr.Left
    $ph = $pr.Bottom - $pr.Top
    if ($pw -lt 5 -or $ph -lt 5 -or $pw -gt 2000 -or $ph -gt 2000) { $popupCapture.Bitmap.Dispose(); continue }
    $relX = $pr.Left - $mainRect.Left
    $relY = $pr.Top - $mainRect.Top
    $gComposite.DrawImage($popupCapture.Bitmap, $relX, $relY)
    $popupCapture.Bitmap.Dispose()
    $popupCount++
}
$gComposite.Dispose()

$mainBmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$mainBmp.Dispose()
Write-Output "Saved: $OutFile (composited $popupCount overlay window(s))"
