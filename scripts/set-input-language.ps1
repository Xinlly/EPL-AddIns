# set-input-language.ps1
# Switch the FOREGROUND window thread's input language by asking it politely:
# LoadKeyboardLayout + PostMessage(WM_INPUTLANGCHANGEREQUEST). LoadKeyboardLayout
# alone only affects the calling thread; the message is what actually switches the
# target app. Reads the layout back as hard proof. Restorable.
#
# Why this exists: a Chinese IME (HKL lang 0804) in composition state swallows
# letter keys/hotkeys (Ctrl+A, Ctrl+J). Force 0409 (US keyboard, passthrough)
# before sending keys; restore 0804 afterwards.
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File set-input-language.ps1 -Lang 0409
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File set-input-language.ps1 -Lang 0804
#
# Exit code 0 only if the read-back language matches. PURE ASCII (PS 5.1 ANSI parsing).
param(
    [ValidateSet("0409","0804")]
    [string]$Lang = "0409"
)
$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class SetLang {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint tid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr LoadKeyboardLayout(string klid, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
'@
$target = [Convert]::ToInt32($Lang, 16)
$fg = [SetLang]::GetForegroundWindow()
$wp = 0
$tid = [SetLang]::GetWindowThreadProcessId($fg, [ref]$wp)
$before = [SetLang]::GetKeyboardLayout($tid)
Write-Output ("FG pid={0} tid={1} beforeLang=0x{2:X4}" -f $wp, $tid, ($before.ToInt64() -band 0xFFFF))

# KLID is the 8-hex-digit form; low word is the language id (e.g. "00000409").
$klid = ("0000{0}" -f $Lang)
$hkl = [SetLang]::LoadKeyboardLayout($klid, 1)  # KLF_ACTIVATE
if ($hkl -eq [IntPtr]::Zero) { Write-Output ("FAIL: LoadKeyboardLayout {0} returned 0 (layout not installed)" -f $klid); exit 1 }

[void][SetLang]::PostMessage($fg, 0x0050, [IntPtr]::Zero, $hkl)  # WM_INPUTLANGCHANGEREQUEST
Start-Sleep -Milliseconds 500
$after = [SetLang]::GetKeyboardLayout($tid)
$afterLang = $after.ToInt64() -band 0xFFFF
Write-Output ("afterLang=0x{0:X4} target=0x{1:X4}" -f $afterLang, $target)
if ($afterLang -eq $target) { Write-Output ("INPUT_LANG_SET_OK " + $Lang); exit 0 }
Write-Output ("INPUT_LANG_SET_FAIL readback=0x{0:X4}" -f $afterLang); exit 1
