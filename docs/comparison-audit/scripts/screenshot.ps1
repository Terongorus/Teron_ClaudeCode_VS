# Background-safe screenshot helper: captures a window's contents via PrintWindow
# (PW_RENDERFULLCONTENT), not CopyFromScreen. Does NOT call SetForegroundWindow, so it
# never steals focus and is unaffected by whatever window is actually on top / whatever
# the user is doing at the time.
# Usage: powershell -File screenshot.ps1 -ProcessName devenv -OutFile out.png
#        powershell -File screenshot.ps1 -Hwnd 123456 -OutFile out.png
param(
    [string]$ProcessName,
    [long]$Hwnd = 0,
    [Parameter(Mandatory=$true)][string]$OutFile,
    [string]$WindowTitleContains = ""
)

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Capture {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    public const uint PW_CLIENTONLY = 0x1;
    public const uint PW_RENDERFULLCONTENT = 0x2;
}
"@

function Get-TargetWindow {
    param([string]$ProcName, [string]$TitleContains)
    $procs = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
    if ($TitleContains) {
        $procs = $procs | Where-Object { $_.MainWindowTitle -like "*$TitleContains*" }
    }
    return $procs | Select-Object -First 1
}

$targetHwnd = [IntPtr]::Zero
if ($Hwnd -ne 0) {
    $targetHwnd = [IntPtr]$Hwnd
} elseif ($ProcessName) {
    $p = Get-TargetWindow -ProcName $ProcessName -TitleContains $WindowTitleContains
    if (-not $p) { Write-Error "No window found for process '$ProcessName' (title filter: '$WindowTitleContains')"; exit 1 }
    $targetHwnd = $p.MainWindowHandle
} else {
    Write-Error "Must specify -ProcessName or -Hwnd"; exit 1
}

# Only unminimize if needed (required for PrintWindow to have real content to render) -
# this does NOT bring the window to the foreground / above other windows, it only
# changes it from minimized to normal/restored state.
if ([Win32Capture]::IsIconic($targetHwnd)) {
    [Win32Capture]::ShowWindow($targetHwnd, 9) | Out-Null  # SW_RESTORE
    Start-Sleep -Milliseconds 300
}

$rect = New-Object Win32Capture+RECT
[Win32Capture]::GetWindowRect($targetHwnd, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

if ($width -le 0 -or $height -le 0) { Write-Error "Invalid window bounds: $width x $height"; exit 1 }

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$ok = [Win32Capture]::PrintWindow($targetHwnd, $hdc, [Win32Capture]::PW_RENDERFULLCONTENT)
$graphics.ReleaseHdc($hdc)

if (-not $ok) {
    # Fallback for windows that don't support PW_RENDERFULLCONTENT: try flags=0 (client+nonclient
    # via the older GDI path). Still no SetForegroundWindow / CopyFromScreen involved.
    $hdc2 = $graphics.GetHdc()
    [Win32Capture]::PrintWindow($targetHwnd, $hdc2, 0) | Out-Null
    $graphics.ReleaseHdc($hdc2)
}

$bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
Write-Output "Saved: $OutFile ($width x $height)"
