# restart-eplan.ps1
# Gracefully restart EPLAN Electric P8 and auto-dismiss the modal license picker.
# Pure ASCII on purpose: PowerShell 5.1 parses a no-BOM file as ANSI, so non-ASCII
# comments/strings get mis-decoded and break tokenization.
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\restart-eplan.ps1
#
# Verified facts (EPLAN 2.9.4 test box):
#  * Close with WM_CLOSE (same as clicking X). If a save prompt appears, do NOT click
#    anything blindly; exit code 2 lets the caller inspect.
#  * The license dialog is a modal #32770 owned by the new process. The P8 Professional
#    row is selected by default; PostMessage WM_COMMAND IDOK(1) confirms it (the OK
#    button is BCG owner-drawn and not UIA-invokable).
#  * Do NOT delete ShadowCopyAssemblies: EPLAN re-shadow-copies from the registered
#    source DLL during restart.
#  * The caller must ensure the English IME is active before sending SendInput keys.
param(
    [string]$Exe     = 'C:\Program Files\EPLAN\Platform\2.9.4\Bin\Eplan.exe',
    [string]$Variant = 'Electric P8',
    [int]$LicenseWaitSec = 80
)
$ErrorActionPreference = 'Continue'
Add-Type -Name EpR -Namespace Ep -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
'@

# 1) Gracefully stop the current main EPLAN process (largest working set).
$cur = Get-Process EPLAN -ErrorAction SilentlyContinue |
       Sort-Object WorkingSet64 -Descending | Select-Object -First 1
if ($cur) {
    $hw = $cur.MainWindowHandle
    [void][Ep.EpR]::PostMessage($hw, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
    Write-Output "WM_CLOSE -> PID $($cur.Id) hwnd=$($hw.ToInt64())"
    for ($i=0; $i -lt 40 -and -not $cur.HasExited; $i++) { Start-Sleep -Milliseconds 500; $cur.Refresh() }
    Write-Output "old_exited=$($cur.HasExited)"
    if (-not $cur.HasExited) {
        Write-Output "ABORT: EPLAN did not exit within 20s (save dialog?). Inspect before retrying."
        exit 2
    }
} else {
    Write-Output "no_running_eplan"
}
Start-Sleep -Seconds 2

# 2) Start EPLAN. ShadowCopyAssemblies is refreshed by EPLAN itself on restart.
$np = Start-Process -FilePath $Exe -ArgumentList "/Variant:`"$Variant`"" -PassThru
Write-Output "STARTED PID=$($np.Id)"

# 3) Dismiss the modal license dialog (visible #32770 owned by the new process).
$handled = 0
$script:found = [IntPtr]::Zero
for ($i=0; $i -lt $LicenseWaitSec -and $handled -eq 0; $i++) {
    Start-Sleep -Milliseconds 1000
    $script:found = [IntPtr]::Zero
    $cb = [Ep.EpR+EnumWindowsProc]{
        param($h,$l)
        $wp = 0
        [void][Ep.EpR]::GetWindowThreadProcessId($h,[ref]$wp)
        if ($wp -eq $np.Id -and [Ep.EpR]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Ep.EpR]::GetClassNameW($h,$sb,256)
            if ($sb.ToString() -eq '#32770') { $script:found = $h; return $false }
        }
        return $true
    }
    [void][Ep.EpR]::EnumWindows($cb,[IntPtr]::Zero)
    if ($script:found -ne [IntPtr]::Zero) {
        $ok = [Ep.EpR]::PostMessage($script:found, 0x0111, [IntPtr]1, [IntPtr]::Zero)  # WM_COMMAND IDOK
        Write-Output "LICENSE dlg=$($script:found.ToInt64()) idok_sent=$ok at_${i}s"
        $handled = 1
    }
}
Write-Output "license_handled=$handled newpid=$($np.Id)"
if ($handled -eq 0) { exit 3 }
