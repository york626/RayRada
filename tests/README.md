# `tests\` —— Ray雷达的注入式自测装置（8 个）

> 建立 **2026-09-19**，扩充于 **2026-09-20** / **2026-09-24** / **2026-09-25**。共同特点：**不加热 CPU、不读真实传感器、不改用户设置**，
> 把「合成输入」喂进**真实的** Ray雷达代码，观察行为是否符合设计。编译产物（`*.exe` / `*.log`）**跑完即删**，只留源码。

| 装置 | 验什么 | 要管理员？ | 会不会动系统 |
|---|---|---|---|
| `SimMain.cs` | **温升报警**（液冷/漏液 vs 游戏尖峰） | 否 | 否（只读设置、不显示浮窗） |
| `SimEntry.cs` | **手机入口 开/关**（`LanSentinel.LanIp/OpenEntry/CloseEntry/StateText`） | 是 | **会真的改 `netsh portproxy`**（跑完自动恢复成「开启」） |
| `SimAlert.cs` | **拦截弹窗结构**（按钮文字/个数/回车默认键） | 否 | 否（只构造窗体，不显示、不点击） |
| `SimKill.cs` | **切断连接**（`SetTcpEntry` 能否断开已建立的 TCP） | 是 | 只切断自己刚建的那条测试连接 |
| `SimCalc.cs` | **竞彩计算器服务器**（`CalcServer` 起停与状态） | 否 | **会真的起停 8000 端口上的 node**（跑完自动停） |
| `SimEtw.cs` | **按应用流量采集（ETW）**：起会话、收事件、与网卡计数器对账、按事件号分项、每 PID 名字解析 | **是**（起 ETW 会话必须提权） | 否（数据写到 `%TEMP%`，不碰真实数据） |
| `SimApp.cs` | **按应用流量数据层**：聚合 / 排行 / Top-N 合并 / 时间窗过滤 / 逐日序列 / 原子写 / 裁剪（19 项） | 否 | 否（数据目录指向 `%TEMP%\rayradar-simapp`） |
| `SimShot.cs` | **流量统计窗口版面**：用合成数据画出窗口并存 PNG | 否 | 否（只显示窗口截图，不采集） |

## 通用编译方法

```powershell
$ws  = 'C:\Tool\RayRadar\tests'
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"   # .NET Framework 4.x 自带，C# 5
# 把 /main: 换成上表对应的类名即可（SimMain / SimEntry / SimAlert / SimKill / SimCalc）
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

### `SimCalc`（竞彩计算器服务器，2026-09-24）
```powershell
sim-calc.exe          # 状态 → 启动 → HTTP 取首页 → 目录填错的报错 → 停止
sim-calc.exe keep     # 跑完不停止（留给雷达接管）
```
实测**全部通过**：默认目录解析成 `我的文档\DSH常用\竞彩计算器` ✓；找到 `serve.mjs` 与 `node.exe` ✓；
启动后 8000 端口在监听 ✓；**HTTP 真取到首页 78,344 字符且含 `webapi.sporttery.cn`** ✓；
把 `CalcDir` 指到不存在的目录 → `Start()` 返回「找不到网页文件：…」且状态行同步说明 ✓；`Stop()` 后端口释放 ✓。
（不需要管理员。真机端到端另测：重启雷达后 **4 秒内**自动拉起服务器，`http://192.168.31.101:8000/` 返回 HTTP 200。）

### `SimEtw`（按应用流量采集，2026-09-25，**需管理员**）
```powershell
# 管理员窗口里跑；跑的时候自己下载点东西造流量，否则排行是空的
sim-etw.exe            # 20 秒
sim-etw.exe 40         # 40 秒
```
验的是 ETW 这条链路本身：会话能不能起（`StartTraceW`）、provider 能不能启用、**回调有没有事件**、
载荷里的 PID/字节数读得对不对、以及**与网卡计数器对账**。日志逐行落盘 `tests\etw-probe.log`（崩溃也留痕）。
实测（2026-09-25）：布局自检 8/28/400/424/448 ✓；一次 20 秒窗口内 **26,302 条事件 / 34.1MB**，
与网卡接收 36.0MB 相差 ~5%（差值 = 链路层头 + 纯 ACK，**纯 ACK 不产生 ETW 事件**是设计如此）。

### `SimApp`（按应用数据层，2026-09-25，免管理员）
```powershell
sim-app.exe           # 19 项断言
```
覆盖：同一天同一应用累加、按天落盘与重载、排行降序、`RankTop` 的「其他 N 项」合并、今天/30 天/1 年
三个时间窗的过滤、逐日序列（缺日补 0、升序）、原子写（不留 `.tmp`）、超期文件裁剪。数据目录指向
`%TEMP%\rayradar-simapp`，**不碰真实数据**。

### `SimShot`（流量窗口截图，2026-09-25，免管理员）
```powershell
$env:RAYRADAR_TRAFFIC_DATA  = "$env:TEMP\rr-synth-total.dat"    # 总量 TSV：日期\t下行\t上行
$env:RAYRADAR_APPTRAFFIC_DIR = "$env:TEMP\rr-synth-apps"         # 按应用：每天一个 .dat
sim-shot.exe out.png 0      # 0=按天明细页   1=按应用明细页
```
用合成数据把 `TrafficForm` 画出来存 PNG（`screenshots/radar-traffic*.png` 就是这么生成的），
用来核对版面而不必等真实数据。窗口是 `TopMost`，装置里已把它关掉，截图时不会挡住你。

## 相关文档

- 报警机制与模拟器原理：`C:/Users/Ray/Documents/DSH常用/docs/rayradar.md` §5、§12
- 入口哨兵（含全部实测记录）：`C:/Users/Ray/Documents/DSH常用/docs/rayradar.md` §13
- 仓库侧规则（C# 5 / UTF-8 BOM / 发布流程）：`C:\Tool\RayRadar\AGENTS.md`
