$ErrorActionPreference = 'Stop'
$inst = 'C:\Users\Reona\AppData\Local\ProArtConsole\ProArtConsole.exe'
$dev  = 'D:\Dev\ProArt-Console\app\bin\Release\net10.0-windows10.0.19041.0\win-x64\ProArtConsole.exe'
$prefix = $args[0]
$timed = $args | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Win32 -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
[DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int n);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Save-Window([IntPtr]$h, [string]$path) {
    $r = [Win32.U+RECT]::new()
    [Win32.U]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    if ($w -lt 10 -or $ht -lt 10) { "窗口尺寸异常，跳过 $path"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Win32.U]::PrintWindow($h, $hdc, 2) | Out-Null        # PW_RENDERFULLCONTENT：合成窗口才拍得到
    $g.ReleaseHdc($hdc); $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "window -> $path ($w x $ht)"
}

function Save-Shot([string]$path) {    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "shot -> $path"
}

function Front([IntPtr]$h, [int]$x, [int]$y, [int]$w, [int]$ht) {
    # 不抢前台、不置顶：把窗口挪到屏幕外，PrintWindow 拍的是窗口自己的位图，看不见也拍得到
    [Win32.U]::SetWindowPos($h, [IntPtr]::Zero, $x, $y, $w, $ht, 0x0010) | Out-Null   # NOACTIVATE
}

function Untop([IntPtr]$h) { }

[Win32.U]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null   # PER_MONITOR_AWARE_V2：坐标按物理像素算
$running = Get-Process ProArtConsole -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $inst }
if ($running) { "暂停正式版 PID=" + ($running.Id -join ','); $running | Stop-Process -Force; Start-Sleep -Seconds 2 }
$devProc = $null
try {
    if ($timed) { $devProc = Start-Process -FilePath $dev -ArgumentList @('--timed', $timed) -PassThru }
    else { $devProc = Start-Process -FilePath $dev -PassThru }
    "开发版 PID=" + $devProc.Id

    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 500
        $devProc.Refresh()
        $h = $devProc.MainWindowHandle
    }
    if ($h -eq [IntPtr]::Zero) { "拿不到主窗口句柄"; exit 3 }
    "hwnd = $h"
    # 一拿到句柄就挪出屏幕，桌面1上基本看不到它，也不抢焦点
    Front $h 6000 0 2300 1560
    Start-Sleep -Seconds 6          # 等第一轮轮询把读数填上
    for ($k = 0; $k -lt 6; $k++) {
        [Win32.U]::ShowWindowAsync($h, 9) | Out-Null
        Start-Sleep -Milliseconds 400
        if (-not [Win32.U]::IsIconic($h)) { break }
    }
    Front $h 6000 0 2300 1560
    Start-Sleep -Seconds 2
    $r = [Win32.U+RECT]::new()
    [Win32.U]::GetWindowRect($h, [ref]$r) | Out-Null
    "iconic=" + [Win32.U]::IsIconic($h) + " rect=" + $r.Left + "," + $r.Top + " " + ($r.Right - $r.Left) + "x" + ($r.Bottom - $r.Top)
    Save-Window $h ($prefix + '.png')

    # 导出 UIA 元素树核对文本，再把所有可滚动容器滚到底补一张图
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $el = [System.Windows.Automation.AutomationElement]::FromHandle($h)
    $found = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $sb = New-Object System.Text.StringBuilder
    $scrollers = @()
    foreach ($e in $found) {
        $t = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $n = ($e.Current.Name -replace '\r?\n', ' / ')
        [void]$sb.AppendLine(("{0,-13} {1}" -f $t, $n))
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$p)) { $scrollers += $p }
    }
    [System.IO.File]::WriteAllText(($prefix + '-tree.txt'), $sb.ToString(), [System.Text.Encoding]::UTF8)
    "tree -> $($prefix)-tree.txt（$($found.Count) 元素 / $($scrollers.Count) 个可滚动）"
    1..10 | ForEach-Object {
        foreach ($s in $scrollers) { if ($s.VerticallyScrollable) { $s.Scroll([System.Windows.Automation.ScrollAmount]::LargeDecrement) } }
        Start-Sleep -Milliseconds 200
    }
    Start-Sleep -Seconds 1
    Save-Window $h ($prefix + '-bottom.png')

    if ($args -contains 'click30') {
        function Invoke-Named([string]$name) {
            $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $b = $all | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Current.Name -eq $name } | Select-Object -First 1
            if ($null -eq $b) { "找不到按钮 $name"; return }
            $p = $null
            if (-not $b.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { "按钮 $name 不支持 Invoke"; return }
            $p.Invoke()
            "已点击 $name"
        }
        function Text-Matching([string]$pattern) {
            $all = $el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            ($all | Where-Object { $_.Current.Name -match $pattern } | Select-Object -First 1).Current.Name
        }
        Invoke-Named '30s'
        Start-Sleep -Seconds 4
        "倒计时读数：" + (Text-Matching '临时全速剩余')
        "档位读数：" + (Text-Matching '全速|安静|高效')
        Save-Window $h 'D:\Dev\ProArt-Console\docs\ui-turbo.png'
        Invoke-Named '取消'
        Start-Sleep -Seconds 3
        "取消后：" + (Text-Matching '临时全速|保持')
    }
    Untop $h
}
finally {
    if ($devProc -and -not $devProc.HasExited) { Stop-Process -Id $devProc.Id -Force; "已停止开发版" }
    Start-Sleep -Seconds 2
    if (Test-Path $inst) { Start-Process $inst | Out-Null; "已恢复正式版" }
}
