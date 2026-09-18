# 第三方组件与许可证

RayRada（Ray雷达）自身源码以 [MIT](LICENSE) 发布。发布出来的 `RayRadar.exe` 里**内嵌**了下列第三方组件（均为**原样、未修改**），
它们的许可证与版权归各自作者所有。

## 1. LibreHardwareMonitor —— MPL-2.0

- 组件：`LibreHardwareMonitorLib.dll` 及其运行所需的依赖 DLL（见下表）
- 版本：0.9.6（net472 构建）
- 项目：https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- 许可证：Mozilla Public License 2.0（https://www.mozilla.org/MPL/2.0/）
- 说明：MPL-2.0 允许把本组件以可执行形式随更大作品一起分发，条件是**不修改其源码**（本项目未做任何修改）、
  保留许可证声明，并说明源码可从上游获取。本项目的源码获取地址：上述 GitHub 仓库。

`LibreHardwareMonitor` 发布包内附带的依赖组件（各自遵循其自身许可证，通常为 MIT / Apache-2.0）：

| DLL | DLL |
|---|---|
| Aga.Controls.dll | Microsoft.Win32.TaskScheduler.dll |
| BlackSharp.Core.dll | OxyPlot.dll |
| DiskInfoToolkit.dll | OxyPlot.WindowsForms.dll |
| HidSharp.dll | RAMSPDToolkit-NDD.dll |
| Microsoft.Bcl.AsyncInterfaces.dll | System.Buffers.dll |
| Microsoft.Bcl.HashCode.dll | System.CodeDom.dll |
| System.Collections.Immutable.dll | System.Formats.Nrbf.dll |
| System.IO.Pipelines.dll | System.Memory.dll |
| System.Numerics.Vectors.dll | System.Reflection.Metadata.dll |
| System.Resources.Extensions.dll | System.Runtime.CompilerServices.Unsafe.dll |
| System.Security.AccessControl.dll | System.Security.Principal.Windows.dll |
| System.Text.Encodings.Web.dll | System.Text.Json.dll |
| System.Threading.AccessControl.dll | System.Threading.Tasks.Extensions.dll |

## 2. PawnIO —— GPL-2.0（**未随本项目分发**）

- 项目：https://github.com/namazso/PawnIO （驱动）、https://github.com/namazso/PawnIO.Setup （官方安装包）
- 许可证：GNU General Public License v2.0
- 作用：为 LibreHardwareMonitor 提供读取 CPU / 主板 / 硬盘 / 内存温度所需的内核级访问
- **重要**：由于 GPL-2.0 对二进制再分发有源码提供义务，本项目的仓库与 Release **不包含** PawnIO 的任何文件。
  程序在首次需要时会**从官方发布页下载**安装包：
  `https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe`
  （也支持使用用户自行放在 exe 同目录下的 `PawnIO_setup.exe`，便于离线部署）。
  是否安装、是否继续使用该驱动，由使用者自行决定并自行承担；相关权利义务以 PawnIO 项目自身的许可证为准。

## 3. 运行时

- 需要 **.NET Framework 4.x**（Windows 10 / 11 默认自带）。本项目不分发 .NET 运行时。
