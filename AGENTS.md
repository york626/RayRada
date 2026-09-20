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
5. ⚠️ **发布后把本地 ref 对齐到远端 SHA**：脚本经 API 建的 commit 会**去掉消息结尾换行**，SHA 与本机 `git commit` 的**差一个字节**
   （脚本打印的「SHA 不同（不影响使用）」是错的——不对齐时，下次提交的 parent 在远端不存在，`POST /git/commits` 会 **422**）。
   做法：去掉本地 commit 对象末尾 `0x0A` → `git hash-object -t commit -w` → `git update-ref refs/heads/main <远端SHA>`；
   完整命令见工作区 `docs/rayradar.md` §10（2026-09-19 实测可用）。
6. **只改文档、不改 exe 的提交**（例如本文件）：用 **`-SkipRelease`** —— 它只推 main，不建 tag / Release（这一步仍要照第 5 条对齐 ref）。
6. 版本号：`AssemblyVersion` / `AssemblyFileVersion` 一直是 `4.2.0.0`（历史遗留、未随版本更新）。**版本以 commit 消息 / `CHANGELOG.md` / Release tag 为准**。

## 4. 行为约束（用户明确要求过的，别改回去）

- **浮窗交互**：单击网速块 = 流量统计窗口；单击主温度块 = 展开/收起温度浮层；**双击不触发任何操作**（v4.6 起，避免误触）；**右键** = 打开设置窗口。
- **报警**：绝对阈值 + 温升报警（**20 秒窗 + 20 秒复测的"两次确认"**，v4.9 起）。**不要**为了"少报"而调高默认温升阈值（15 °C）——液冷故障/漏液就靠它发现。
- 默认阈值：CPU 90 / 显卡 85 / 显卡热点 95 / 主板 65 / 硬盘 75 / 内存 60 °C；温升 15 °C/20 秒。
- 温升报警有四层防误报：启动后 180 秒预热、当前温度须 ≥ 45 °C、采样间隔 > 8 秒清空历史、20 秒复测。**改动它们要同步更 `CHANGELOG.md` 并说明理由。**
- 用户设置在 `%APPDATA%\RayRadar\settings.ini`：程序启动时读、变更时才写。**测试程序不要调 `Settings.Save()`**，以免覆盖用户设置。

## 5. 测试装置（不加热 CPU 也能验报警）

把 `RayRadar.cs` 与模拟器源码一起编成**控制台**程序（`csc /target:exe /main:SimMain`，引用同 `build.ps1`）：

- `RadarForm` / `TempProvider` 用 `FormatterServices.GetUninitializedObject` 造 ⇒ **跳过构造函数**（不注册计划任务、不读传感器、不显示浮窗）；
- 反射注入 `st`（`Settings.Load()`，只读）、`temp`（`Ready=true` + 手写 `Cpu`）、`cpuHist`、`lastAlarm`；
- 每 3 秒（与真实 `tempTimer` 同间隔）改一次 `temp.Cpu` 并反射调用 `CheckAlarm()`。

模拟器源码与用法：`C:\Users\Ray\Documents\DSH常用\_rayradar测试\`（含 `README.md` 与编译命令）。
**2026-09-19 实测**：持续升温 → 第 42.1 秒弹窗；瞬时尖峰 → 静默不报。

**入口哨兵的自测装置（v4.11 起）**：同目录 `SimEntry.cs` —— 直接调 `LanSentinel.LanIp()` / `OpenEntry()` / `CloseEntry()` / `StateText()`，
验证「手机入口」的开/关逻辑（不显示浮窗、不读传感器）。⚠️ 它会**真的**增删 `netsh portproxy`，所以**必须用管理员运行**，
跑完会自动恢复成「开启」并把过程写进同目录 `sim-entry.log`。编译命令见文件头（记得 `/codepage:65001`，否则中文字面量乱码）。

## 6. 深入文档（本机）

Ray雷达专题（部署模型、温度源、报警机制、发布通道的坑、界面/流量统计设计）：`C:\Users\Ray\Documents\DSH常用\docs\rayradar.md`。
