$ErrorActionPreference = 'Stop'
Add-Type -Namespace W -Name M -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern int GetMenuStringW(IntPtr menu, uint id, System.Text.StringBuilder buf, int max, uint flags);
[DllImport("user32.dll")] public static extern uint GetMenuState(IntPtr menu, uint id, uint flags);
'@
$WM_TRAY = 0x401
$WM_RBUTTONUP = 0x205
$MN_GETHMENU = 0x1E1
$WM_CANCELMODE = 0x1F
$MF_BYPOSITION = 0x400

$tray = [W.M]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, 'AsusFanStudioTrayWnd', 'AsusFanStudioTray')
if ($tray -eq [IntPtr]::Zero) { 'tray host window not found'; exit 2 }
"tray hwnd = $tray"
[W.M]::PostMessageW($tray, $WM_TRAY, [IntPtr]1, [IntPtr]$WM_RBUTTONUP) | Out-Null
Start-Sleep -Milliseconds 900

$menuWnd = [W.M]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, '#32768', $null)
if ($menuWnd -eq [IntPtr]::Zero) { 'popup menu window not found (menu did not open)'; exit 3 }
"popup hwnd = $menuWnd"
$hMenu = [W.M]::SendMessageW($menuWnd, $MN_GETHMENU, [IntPtr]::Zero, [IntPtr]::Zero)
$n = [W.M]::GetMenuItemCount($hMenu)
"items = $n"
for ($i = 0; $i -lt $n; $i++) {
    $sb = New-Object System.Text.StringBuilder 256
    [W.M]::GetMenuStringW($hMenu, $i, $sb, 256, $MF_BYPOSITION) | Out-Null
    $st = [W.M]::GetMenuState($hMenu, $i, $MF_BYPOSITION)
    $flag = if ($st -band 0x3) { 'DISABLED/GRAYED' } elseif ($st -band 0x800) { 'separator' } else { 'clickable' }
    ("  [{0}] state=0x{1:X4} {2,-14} {3}" -f $i, $st, $flag, $sb.ToString())
}
[W.M]::PostMessageW($menuWnd, $WM_CANCELMODE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
'menu dismissed'
