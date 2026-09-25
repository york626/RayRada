# AGENTS.md — 在 RayRada 仓库作业的 AI 守则

> 面向**任何**在此仓库工作的 AI（DSH / Codex / Trae / OpenCode…）。人类读者看 [README.md](README.md)。
> 建立：**2026-09-19**。本文件只写"必须遵守什么"，历史叙事看 [CHANGELOG.md](CHANGELOG.md)。

## 1. 这是什么

Windows 桌面硬件监控浮窗（C# WinForms，**单文件 exe**）。源码集中在 `RayRadar.cs`；编译用 `build.ps1` / `编译.cmd`（系统自带 `csc.exe`，无需 Visual Studio）。
温度来自 **LibreHardwareMonitor**（MPL-2.0，`lib\` 下的 DLL，**不入库**）+ **PawnIO** 驱动（GPL-2.0，**不打包、运行时从官方地址下载**）。

## 2. 硬约束（违反就会出事）

- **编译器是 .NET Framework 4.x 的 `csc.exe`（C# 5）**：**不能**用字符串插值 `$"…"`、`?.`、`nameof`、`out var`、表达式体成员、`ValueTuple` 等新语法。
- **含中文的 `.ps1` 必须存成 UTF-8 带 BOM**：无 BOM 时 Windows PowerShell 5.1 按 GBK 解码，中文变乱码并抛 `Unexpected token` / 未闭合字符串（本仓库踩过）。
- **解释器优先 `pwsh.exe`（PowerShell 7）**，找不到才退回 `powershell.exe`；不要硬编码 `powershell.exe`。
- **不要提交** `lib\`、`*.exe`、`PawnIO_setup.exe`、`*.bak`（`.gitignore` 已排除）；`lib\` 由 `获取依赖.ps1` 生成。
- **PawnIO（GPL-2.0）不得随仓库 / Release 分发**：它由程序在首次使用时从官方发布页下载。改动这条前先读 `THIRD-PARTY-NOTICES.md`。
- **exe 是唯一交付物**：不要再往桌面或其他目录放交付包；交付渠道只有本仓库与 GitHub Release。

## 3. 改动流程（改完必须走完）

1. 改 `RayRadar.cs` → 跑 `.\编译.cmd`（脚本会先结束正在运行的实例；普通权限杀不掉提权进程时自动改用 `schtasks /End /TN RayRadar`）。
2. **同步更新 `CHANGELOG.md`**：顶部加 `## vX.Y — YYYY-MM-DD` 段——GitHub Release 的说明就是取这一段。
3. `git add -A; git commit -m "vX.Y: …"`。
4. 发版：`C:\Users\Ray\.dsh\tools\publish-rayrada.ps1 -Tag vX.Y`
   （本机 `github.com` 被 hosts 拦，脚本走 REST API 推进远端；不带 `-TokenFile` 会用 DPAPI 存好的 token）。
   ⚠️ **必须在仓库目录内运行**（`cd C:\Tool\RayRadar` 再跑）：脚本会逐文件比对「`git hash-object <文件>` vs 提交里的 blob」，
   从仓库外运行时 git 会对含 CRLF 的文件套用转换 ⇒ 校验必失败（报「文件与提交不一致」）。同理：**`RayRadar.cs` 保持 LF 换行**，
   用 PowerShell 做替换时别写进 `` `r`n ``（2026-09-25 踩过：混进 11 处 CRLF，amend 提交才修好）。
5. ⚠️ **发布后把本地 ref 对齐到远端 SHA**：脚本经 API 建的 commit 会**去掉消息结尾换行**，SHA 与本机 `git commit` 的**差一个字节**
   （脚本打印的「SHA 不同（不影响使用）」是错的——不对齐时，下次提交的 parent 在远端不存在，`POST /git/commits` 会 **422**）。
   做法：去掉本地 commit 对象末尾 `0x0A` → `git hash-object -t commit -w` → `git update-ref refs/heads/main <远端SHA>`；
   完整命令见工作区 `docs/rayradar.md` §10（2026-09-19 实测可用）。
6. **只改文档、不改 exe 的提交**（例如本文件）：用 **`-SkipRelease`** —— 它只推 main，不建 tag / Release（这一步仍要照第 5 条对齐 ref）。
7. 版本号：`AssemblyVersion` / `AssemblyFileVersion` 一直是 `4.2.0.0`（历史遗留、未随版本更新）。**版本以 commit 消息 / `CHANGELOG.md` / Release tag 为准**。

## 4. 行为约束（用户明确要求过的，别改回去）

- **浮窗交互**：单击网速块 = 流量统计窗口；单击主温度块 = 展开/收起温度浮层；**双击不触发任何操作**（v4.6 起，避免误触）；**右键** = 打开设置窗口。
- **报警**：绝对阈值 + 温升报警（**20 秒窗 + 20 秒复测的"两次确认"**，v4.9 起）。**不要**为了"少报"而调高默认温升阈值（15 °C）——液冷故障/漏液就靠它发现。
- 默认阈值：CPU 90 / 显卡 85 / 显卡热点 95 / 主板 65 / 硬盘 75 / 内存 60 °C；温升 15 °C/20 秒。
- 温升报警有四层防误报：启动后 180 秒预热、当前温度须 ≥ 45 °C、采样间隔 > 8 秒清空历史、20 秒复测。**改动它们要同步更 `CHANGELOG.md` 并说明理由。**
- 用户设置在 `%APPDATA%\RayRadar\settings.ini`：程序启动时读、变更时才写。**测试程序不要调 `Settings.Save()`**，以免覆盖用户设置。
- **入口哨兵（v4.10 起）**：只在本机存在 **3081 端口转发**时才工作；白名单之外的设备一连上 ⇒ 报警 + **删掉转发** + **切断它已建立的 TCP**。
  三条**别改回去**：① 扫描必须在**后台线程**（默认 500 毫秒）——放回界面线程会让浮窗卡住（v4.14 之前就是这样，用户实测反馈过）；
  ② 拦截弹窗必须**非模态**（`Show()` 而不是 `ShowDialog()`，且同时只留一个）——模态窗会把浮窗冻住；
  ③ 弹窗**只有「重开手机入口」和「保持关闭」两个按钮**，用户明确要求**不要**"顺手加白名单"按钮（白名单只在设置窗口里手动改）。

## 5. 测试装置（不加热 CPU 也能验报警）

把 `RayRadar.cs` 与模拟器源码一起编成**控制台**程序（`csc /target:exe /main:SimMain`，引用同 `build.ps1`）：

- `RadarForm` / `TempProvider` 用 `FormatterServices.GetUninitializedObject` 造 ⇒ **跳过构造函数**（不注册计划任务、不读传感器、不显示浮窗）；
- 反射注入 `st`（`Settings.Load()`，只读）、`temp`（`Ready=true` + 手写 `Cpu`）、`cpuHist`、`lastAlarm`；
- 每 3 秒（与真实 `tempTimer` 同间隔）改一次 `temp.Cpu` 并反射调用 `CheckAlarm()`。

模拟器源码与用法：**`tests\`**（**仓库内**，含 `README.md` 与各装置的编译命令；2026-09-20 从工作区 `_rayradar测试\` 迁入）。
**2026-09-19 实测**：持续升温 → 第 42.1 秒弹窗；瞬时尖峰 → 静默不报。

**自测装置共 8 个，都在 `tests\`**（清单与逐个用法见 `tests\README.md`）：

- **报警/入口类**：`SimMain.cs`（温升报警，免管理员）、`SimEntry.cs`（手机入口开/关，**需管理员**，会真的增删 `netsh portproxy`、跑完自动恢复成「开启」）、
  `SimAlert.cs`（拦截弹窗的按钮结构）、`SimKill.cs`（`SetTcpEntry` 切断连接，**需管理员**）。
- **竞彩计算器 / 服务器类**：`SimCalc.cs`（`CalcServer` 起停与状态）。
- **v4.17 按应用流量统计**（2026-09-25 新增）：`SimEtw.cs`（**采集端到端，需管理员**：起 ETW 会话、收事件、与网卡计数器对账、按事件号分项、每 PID 的名字解析）、
  `SimApp.cs`（`AppTraffic` 数据层 19 项断言：聚合/排行/Top-N/时间窗/逐日序列/原子写/裁剪）、`SimShot.cs`（用合成数据把流量窗口画成 PNG 核对版面）。

编译一律加 `/codepage:65001`（否则中文字面量乱码）；除需要真实温度的装置外**不要**嵌 `/resource:` 那 27 个 DLL；编译产物（`*.exe` / `*.log`）不入库。
⚠️ **跑 ETW 相关装置必须提权**（`StartTrace` 要管理员）；不想每次点 UAC 的话，可临时建一个 `/RL HIGHEST` 的计划任务当入口（**用完删掉**，2026-09-25 是这么迭代的）。
⚠️ `SimShot` 与 `SimApp` 用环境变量注入合成数据：`RAYRADAR_TRAFFIC_DATA`（总量 TSV）、`RAYRADAR_APPTRAFFIC_DIR`（按应用目录）—— **不碰真实数据**。

## 6. 深入文档（本机）

Ray雷达专题（部署模型、温度源、报警机制、发布通道的坑、界面/流量统计设计）：`C:\Users\Ray\Documents\DSH常用\docs\rayradar.md`。
