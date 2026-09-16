# check-kbd-focus.ps1
# Read-only precondition check before sending keys to a desktop app:
# prints the REAL keyboard-focus window class (GetGUIThreadInfo, not just the
# foreground window) and the foreground thread keyboard-layout language id.
#
# Optional assertions (all provided ones must match, else exit code 1):
#   -ExpectPid <int>        foreground window must belong to this process id
#   -ExpectFocusClass <s>   focus window class must contain this substring
#                            (e.g. AfxFrameOrView for the EPLAN graphic view)
#   -ExpectLangHex <hex>    thread HKL low word must equal this, e.g. 0409
#
# PURE ASCII: Windows PowerShell 5.1 parses a no-BOM file as ANSI.
#
# Example:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File check-kbd-focus.ps1 `
#     -ExpectPid 25904 -ExpectFocusClass AfxFrameOrView -ExpectLangHex 0409
param(
    [int]$ExpectPid = 0,
    [string]$ExpectFocusClass = "",
    [string]$ExpectLangHex = ""
)
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class ChkKbd {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, out GTI i);
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)]
  public struct GTI { public int cbSize; public uint flags; public IntPtr hwndActive; public IntPtr hwndFocus;
                     public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize;
                     public IntPtr hwndCaret; public RECT rc; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
'@
function Cls($h) {
  if ($h -eq [IntPtr]::Zero) { return "<null>" }
  $sb = New-Object System.Text.StringBuilder 256
  [void][ChkKbd]::GetClassNameW($h, $sb, 256)
  return $sb.ToString()
}

$fg = [ChkKbd]::GetForegroundWindow()
$wp = 0
$tid = [ChkKbd]::GetWindowThreadProcessId($fg, [ref]$wp)
$gi = New-Object ChkKbd+GTI
$gi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($gi)
[void][ChkKbd]::GetGUIThreadInfo($tid, [ref]$gi)
$hkl = [ChkKbd]::GetKeyboardLayout($tid)
$lang = $hkl.ToInt64() -band 0xFFFF
$focusClass = Cls $gi.hwndFocus

Write-Output ("FG pid={0} tid={1} fgClass={2}" -f $wp, $tid, (Cls $fg))
Write-Output ("FOCUS hwnd={0} class={1}" -f $gi.hwndFocus.ToInt64(), $focusClass)
Write-Output ("HKL lang=0x{0:X4}  (0x0409=US English, 0x0804=Chinese)" -f $lang)

$ok = $true
if ($ExpectPid -ne 0 -and [int]$wp -ne $ExpectPid) { Write-Output ("FAIL: foreground pid {0} != expected {1}" -f $wp, $ExpectPid); $ok = $false }
if ($ExpectFocusClass -ne "" -and $focusClass.IndexOf($ExpectFocusClass, [StringComparison]::Ordinal) -lt 0) {
  Write-Output ("FAIL: focus class '{0}' does not contain '{1}'" -f $focusClass, $ExpectFocusClass); $ok = $false
}
if ($ExpectLangHex -ne "") {
  $want = [Convert]::ToInt32($ExpectLangHex, 16)
  if ($lang -ne $want) { Write-Output ("FAIL: HKL lang 0x{0:X4} != expected 0x{1:X4}" -f $lang, $want); $ok = $false }
}
if ($ok) { Write-Output "PRECONDITIONS_OK"; exit 0 } else { Write-Output "PRECONDITIONS_FAIL"; exit 1 }
