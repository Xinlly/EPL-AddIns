# monitor-focus.ps1
# Sample the REAL keyboard-focus window class and thread input language of a
# target process for a few seconds, logging only on change. Use this to observe
# what happens to focus while you drive the UI with another tool (cua-driver etc.):
# running a foreground PowerShell would itself steal foreground, so run this in the
# background and operate the app during the window.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File monitor-focus.ps1 `
#     -Pid 25904 -Seconds 20 -OutFile focus_samples.txt
#
# PURE ASCII (PS 5.1 parses a no-BOM file as ANSI).
param(
    [Parameter(Mandatory=$true)][int]$Pid,
    [int]$Seconds = 20,
    [string]$OutFile = "focus_samples.txt"
)
$ErrorActionPreference = "Continue"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class MonFoc {
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
  [void][MonFoc]::GetClassNameW($h, $sb, 256)
  return $sb.ToString()
}

$sw = New-Object System.IO.StreamWriter($OutFile, $false, [System.Text.Encoding]::UTF8)
$sw.AutoFlush = $true
$last = ""
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
  $fg = [MonFoc]::GetForegroundWindow(); $wp = 0
  $tid = [MonFoc]::GetWindowThreadProcessId($fg, [ref]$wp)
  if ([int]$wp -eq $Pid) {
    $gi = New-Object MonFoc+GTI
    $gi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($gi)
    [void][MonFoc]::GetGUIThreadInfo($tid, [ref]$gi)
    $hkl = [MonFoc]::GetKeyboardLayout($tid)
    $line = ("t={0:HH:mm:ss.fff} fg={1} focus={2}({3}) HKL=0x{4:X4}" -f `
      (Get-Date), $fg.ToInt64(), $gi.hwndFocus.ToInt64(), (Cls $gi.hwndFocus), ($hkl.ToInt64() -band 0xFFFF))
    if ($line -ne $last) { $sw.WriteLine($line); $last = $line }
  } else {
    if ($last -ne "NOTFG") { $sw.WriteLine(("t={0:HH:mm:ss.fff} foreground is OTHER pid={1}" -f (Get-Date), $wp)); $last = "NOTFG" }
  }
  Start-Sleep -Milliseconds 200
}
$sw.Close()
