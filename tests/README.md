# `tests\` —— Ray雷达的注入式自测装置（4 个）

> 建立 **2026-09-19**，扩充于 **2026-09-20**。共同特点：**不加热 CPU、不读真实传感器、不改用户设置**，
> 把「合成输入」喂进**真实的** Ray雷达代码，观察行为是否符合设计。编译产物（`*.exe` / `*.log`）**跑完即删**，只留源码。

| 装置 | 验什么 | 要管理员？ | 会不会动系统 |
|---|---|---|---|
| `SimMain.cs` | **温升报警**（液冷/漏液 vs 游戏尖峰） | 否 | 否（只读设置、不显示浮窗） |
| `SimEntry.cs` | **手机入口 开/关**（`LanSentinel.LanIp/OpenEntry/CloseEntry/StateText`） | 是 | **会真的改 `netsh portproxy`**（跑完自动恢复成「开启」） |
| `SimAlert.cs` | **拦截弹窗结构**（按钮文字/个数/回车默认键） | 否 | 否（只构造窗体，不显示、不点击） |
| `SimKill.cs` | **切断连接**（`SetTcpEntry` 能否断开已建立的 TCP） | 是 | 只切断自己刚建的那条测试连接 |

## 通用编译方法

```powershell
$ws  = 'C:\Tool\RayRadar\tests'
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"   # .NET Framework 4.x 自带，C# 5
# 把 /main: 换成上表对应的类名即可（SimMain / SimEntry / SimAlert / SimKill）
& $csc /nologo /target:exe /main:SimEntry /codepage:65001 /out:"$ws\sim-entry.exe" `
  'C:\Tool\RayRadar\RayRadar.cs' "$ws\SimEntry.cs" `
  '/reference:C:\Tool\RayRadar\lib\LibreHardwareMonitorLib.dll' `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll
```

- **必须带 `/codepage:65001`**：源码是 UTF-8，不带的话中文提示会乱码（老 csc 默认按 ANSI 解码）。
- 不要嵌 `/resource:` 那 27 个 DLL —— 这些装置都不碰 LibreHardwareMonitor；只有需要真实温度时才嵌。
- 自定义 `Main` 必须用 `/main:` 指定（`RayRadar.cs` 自带入口）。

## 各装置用法与实测结论

### `SimMain`（温升报警，2026-09-19）
```powershell
& "$ws\sim.exe" ramp     # 持续升温（液冷故障）→ 预期 42 秒左右弹窗
& "$ws\sim.exe" spike    # 瞬时 +20°C 后走平（游戏启动）→ 预期静默不报
```
实测：`ramp` 第 **42.1 秒**弹窗（「40 秒内累计上升 16°C」）；`spike` **静默不报** ⇒ 防误报有效。退出码 `0` = 符合预期。

### `SimEntry`（手机入口开/关，2026-09-20）
```powershell
sim-entry.exe          # 全部：关→开→幂等→恢复开启 + 白名单匹配自测（需管理员）
sim-entry.exe scan     # 只跑白名单匹配（入口已开着即可，**不需要管理员**）
```
实测：内网 IP 探测 `192.168.31.101` ✓；关→转发消失 ✓；开→恢复 ✓；重复关→「无需关闭」✓；
白名单匹配 A) 只放本机 MAC → 命中 **0** ✓ B) 只放本机 IP → **0** ✓ C) 清空 → **1** ✓（IP/MAC 两条路径都有效）。

### `SimAlert`（弹窗结构，2026-09-20）
只构造窗体并打印控件，不显示窗口：入口拦截弹窗 = 大字「⚠ 陌生设备接入」+ 按钮「重开手机入口」「保持关闭」，
**回车默认键 = 保持关闭**；温度报警弹窗仍只有 1 个「知道了」。

### `SimKill`（切断连接，2026-09-20）
连一条到 `入口:3081` 的 TCP → 调 `LanSentinel.KillConnections(ip, 3081)` → 再通信应报
「远程主机强迫关闭了一个现有的连接」✓（`SetTcpEntry` + `MIB_TCP_STATE_DELETE_TCB`，需管理员）。

## 相关文档

- 报警机制与模拟器原理：`C:/Users/Ray/Documents/DSH常用/docs/rayradar.md` §5、§12
- 入口哨兵（含全部实测记录）：`C:/Users/Ray/Documents/DSH常用/docs/rayradar.md` §13
- 仓库侧规则（C# 5 / UTF-8 BOM / 发布流程）：`C:\Tool\RayRadar\AGENTS.md`
