# ProArt Studiobook 16（H7600ZM）性能控制台

一个风扇档位控制台：按 CPU / GPU 温度自动在 安静 / 高效 / 全速 三档之间切换，阈值全部可拖拽自定义。

免管理员、无驱动、无第三方运行时依赖（.NET 10 + Windows App SDK 自包含分发）。

![界面](docs/ui.png)

## 为什么没有风扇曲线

这台机器（H7600ZM，i7-12700H + RTX 3060 Laptop）的 EC 里**根本没有逐风扇 PWM / 曲线写入口**，BIOS 也没有。逐通道实测（`tools/ecprobe`）：

| 通道 | 结论 |
| --- | --- |
| `\\.\ATKACPI` → `0x00110013 / 14 / 19` | 可读 CPU/GPU 风扇转速、可读写档位（唯一可用的写入口） |
| 曲线 `0x00110024 / 0x00110025 / 0x00110032` | EC 不应答（`ERROR_INVALID_FUNCTION`，即表里不存在） |
| 迟滞 `0x00110034`、NewFanStatus `0x00110035` | 同上，含 1024 字节缓冲读也失败 |
| `AsusWinIO64.dll` + `AsusSAIO.sys` | 通道活着但被内部 "BatteryHealthyTable version" 门槛挡死，风扇 API 全返回哨兵值 |
| Creator Hub 的 Laptop 模块 | 反编译可见 `LoadFanCurve/SaveFanCurve` 与 8 点曲线 UI，但被 `MachineYear < 2025` 门控，且 RPC 枚举里只有读、没有任何写曲线的方法 |

所以能做、也是这个项目做的，就是**三档之间的自动切换 + 非对称进出阈值 + 临时全速定时**——把官方软件里那套"只有三个按钮"的东西变得可控。

## 功能

- **两条独立的区间编辑器**（CPU / GPU 各一条）：安静进入、安静退出、全速退出、全速进入四个把手，进出阈值分开，中间是"保持当前"的死区。
- **升降档防抖**：升档需条件持续 N 秒（默认 3s），降档需持续 M 秒（默认 15s），另有最小停留秒；相邻阈值强制 ≥1°C 间距，否则死区宽度为 0 会让档位在阈值上反复横跳。
- **GPU 参与条件**：独显温度达到"纳入阈值"（默认 60°C）才参与取 max，凉了或休眠时完全按 CPU 那条走，界面显示"GPU 休眠"而不是 0 RPM。
- **按硬件自适应**：用 PCI 厂商 ID（`VEN_10DE` / `VEN_1002`）判定有没有独显；没有独显就整块隐藏 GPU 的温度、功耗、转速、调节条和 GPU 相关阈值，只留 CPU 那一条。独显只是休眠（省电模式）时不隐藏，读数写"GPU 休眠"。
- **读数区**：CPU 温度 + 占用率、GPU 温度 + 实时功耗、双风扇转速、当前档位。
- **临时全速**：可定时（30s / 60s / 2min / 5min / 自定义），**取消或到期时立刻回落**，跳过降档确认与最小停留——点取消就是嫌吵，不该再等防抖。
- **本机配置卡**（底部）：机型、CPU（基频/线程）、内存、显卡、屏幕分辨率与缩放、磁盘型号、BIOS 版本、系统版本。
- 一键切档 + 全局热键 `Ctrl+Alt+1/2/3`、托盘常驻（Win11 下会自动把自己设为常驻图标）、关窗不退出。
- 数值设定分"保存 / 还原默认 / 放弃未保存改动"，改完不点保存不会写盘。
- 开机自启开关（HKCU Run）。

配置：`%APPDATA%\ProArtConsole\fanstudio.json`；异常记录：同目录 `crash.log`。

## 传感器来源（都不用驱动、不用管理员）

- CPU 温度：Performance Counter `Thermal Zone Information \_TZ.THRM`（开尔文，减 273.15）。`MSAcpi_ThermalZoneTemperature` 要管理员，没用。
- CPU 占用率：Performance Counter `Processor Information \_Total\% Processor Time`（首个样本恒为 0，丢掉它才读得到值）。
- GPU 温度与功耗：`nvml.dll`（`nvmlDeviceGetTemperature` / `nvmlDeviceGetPowerUsage`）。独显休眠时 NVML 返回 `NOT_SUPPORTED`，界面显示"GPU 休眠"。
- 风扇转速与当前档位：ATKACPI `DSTS`，值 = `(dword0 & 0xFFFF) * 100` RPM；档位 0/1/2/3 = 默认/安静/高效/全速。
- 硬件配置：注册表 `HARDWARE\DESCRIPTION\System\{BIOS, CentralProcessor\0}` + `SetupDiGetClassDevs`（显示适配器 / 磁盘类别）+ `GetPhysicallyInstalledSystemMemory`，不引 WMI 也不引第三方包。
- **EC 接口是争用的**：华硕后台服务轮询时同一时刻我们的 IOCTL 会整体失败，所以读数带退避重试，界面会显示"EC 被占用，稍后自动重试"。

### 为什么没有 CPU 封装功率

`tools/sensprobe` 把免权限的路子都试过了：

| 路子 | 结论 |
| --- | --- |
| ATKACPI `DSTS` 扫 `0x00110000-0x00110060`、`0x00120000-0x00120100`、`0x00090000-0x00090040` | 只有风扇 `13/14`、档位 `19`、电池限制 `120057` 等 10 个 ID 应答；8 线程满载 14 秒，没有任何通道随负载上升 |
| `AsusWinIO64.dll`（253 个导出） | 有 `Thermal_Read_CpuPackagePower`，但 `InitializeWinIo` 对第三方进程返回 0，读取一律 0 或哨兵值 `2147483647` |
| 性能计数器 / WMI | 没有功率类的计数器 |
| NVML | 只管独显，读不到 CPU |

也就是说 CPU 封装功率只剩 Ring0 读 MSR（RAPL）一条路，与本项目"免管理员、无驱动"的前提冲突，所以这一项干脆不做，读数区给占用率。

## 构建

需要 .NET 10 SDK。

```
cd app
dotnet build -c Release
dotnet publish -c Release -o publish
```

`app/ProArtConsole.csproj` 里的 `AddXamlResourcesToPublish` 目标不能删：`dotnet publish` 不会把 WinUI 的 MRT 资源索引 `ProArtConsole.pri` 放进发布清单，缺它会在 `Application.LoadComponent` 抛 `XamlParseException`，表现为启动几秒后 `Microsoft.UI.Xaml.dll` 报 `0xc000027b` 闪退。

## 打包成 MSI

用 WiX v3.14 的 `candle` / `light`（v7 需要 OSMF EULA 同意，这里没走）。

```
dotnet publish -c Release -o installer/payload
# 裁掉多余语言目录（只留 Assets / Microsoft.UI.Xaml / en-us / zh-CN）
python installer/gen_wix3.py
candle installer/product.wxs -arch x64 -out installer/product.wixobj
light installer/product.wixobj -out ProArtConsole.msi
```

每用户安装到 `%LOCALAPPDATA%\ProArtConsole`。两个必须过的校验：**ICE38**（装在用户配置文件里的组件，KeyPath 必须是 HKCU 注册表项而不是文件）、**ICE64**（这些目录要在 RemoveFile 表里有记录，否则卸载后残留）。

## 目录

```
app/        WinUI 3 控制台（主程序）
installer/  gen_wix3.py：WiX v3 的 wxs 生成器
tools/
  ecprobe/      ATKACPI 只读扫描器，用来确认 EC 到底应答哪些设备 ID
  sensprobe/    传感器来源探针：NVML 能读到什么、EC 有没有功率通道、WinIO 的 253 个导出能不能免权限调用
    ui_check.ps1        界面自检：把开发版窗口挪出屏幕（不抢前台）截图 + 导 UIA 元素树
    tray_menu_check.ps1 给托盘隐藏窗口投递右键消息，读回弹出菜单每一项的启用/勾选状态
  monitor/      WinForms 监控版：免权限传感器验证 + 三档同负载实测
  winioprobe/   AsusWinIO64.dll 探针（记录 WinIO 通道为何不可用）
```

## 风险说明

- 写档位走的是未公开的 ATKACPI `DEVS` 接口，与官方软件（Creator Hub / ASUS 服务）操作同一个 EC 寄存器，可能互相覆盖。
- 只在 H7600ZM 这一台机器上验证过；换型号请先用 `tools/ecprobe` 确认设备 ID 是否存在。
- 卸载本程序不影响系统；但请勿删除 `asussci2.inf_amd64_*` 驱动包，否则 EC 通道和热键会失效。
- 由Qwen3.8 Flash编写
