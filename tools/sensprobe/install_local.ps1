$ErrorActionPreference = 'Stop'
$msi = if ($args[0]) { $args[0] } else { 'D:\Dev\ProArt-Console\dist\ProArtConsole-1.1.1.msi' }
$exe = "$env:LOCALAPPDATA\ProArtConsole\ProArtConsole.exe"

"安装前 DisplayVersion = " + ((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'ProArt*' }).DisplayVersion -join ',')

$run = Get-Process ProArtConsole -ErrorAction SilentlyContinue
if ($run) { "停止运行中的实例 PID=" + ($run.Id -join ','); $run | Stop-Process -Force; Start-Sleep -Seconds 2 }

$p = Start-Process msiexec.exe -ArgumentList '/i', $msi, '/qn', '/norestart' -PassThru -Wait
"msiexec 退出码 = " + $p.ExitCode
if ($p.ExitCode -ne 0) { exit $p.ExitCode }

"安装后 DisplayVersion = " + ((Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like 'ProArt*' }).DisplayVersion -join ',')
if (Test-Path $exe) { "已安装 exe FileVersion = " + (Get-Item $exe).VersionInfo.FileVersion }
Start-Process $exe | Out-Null
Start-Sleep -Seconds 6
$now = Get-Process ProArtConsole -ErrorAction SilentlyContinue
if ($now) { "新版已运行 PID=" + ($now.Id -join ',') } else { "启动后没找到进程"; exit 4 }
