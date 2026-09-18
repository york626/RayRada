# build.ps1 -- 从源码编译出单文件 RayRadar.exe
# 依赖：Windows 自带的 .NET Framework 4.x（csc.exe），无需 Visual Studio。
# 编译前请先确保 lib\ 里有依赖 DLL：运行 .\获取依赖.ps1 可自动下载。
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:SystemRoot\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 csc.exe（需要 .NET Framework 4.x）" }

$dlls = @(Get-ChildItem "$dir\lib" -File -Filter *.dll -ErrorAction SilentlyContinue)
if ($dlls.Count -eq 0) { throw "lib\ 里没有依赖 DLL，请先运行 .\获取依赖.ps1" }
if (-not (Test-Path "$dir\lib\LibreHardwareMonitorLib.dll")) { throw "lib\LibreHardwareMonitorLib.dll 缺失，请重新运行 .\获取依赖.ps1" }

Get-Process RayRadar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
if (Get-Process RayRadar -ErrorAction SilentlyContinue) {
  # 兜底 1：taskkill（对同用户的提权进程通常有效）
  # 注意：原生命令往 stderr 写内容，在 $ErrorActionPreference='Stop' 下会变成终止性错误，所以这里临时放开
  $prevEap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
  & taskkill.exe /F /IM RayRadar.exe | Out-Null
  $ErrorActionPreference = $prevEap
  Start-Sleep -Seconds 2
}
if (Get-Process RayRadar -ErrorAction SilentlyContinue) {
  # 兜底 2：用计划任务结束它
  & schtasks.exe /End /TN RayRadar 2>$null | Out-Null
  Start-Sleep -Seconds 2
}
if (Get-Process RayRadar -ErrorAction SilentlyContinue) {
  throw "RayRadar 仍在运行，无法覆盖输出文件。请在浮窗上右键 →『退出 Ray雷达』，或先执行： schtasks /End /TN RayRadar"
}

# 把 lib 下的依赖 DLL 嵌进 exe；运行时由 AssemblyResolve 从自身资源里加载。
# 注意：PawnIO（GPL-2.0）不打包进 exe，改为首次使用时从官方地址下载 —— 见 THIRD-PARTY-NOTICES.md
$res = @()
foreach ($d in $dlls) { $res += "/resource:$($d.FullName),$($d.Name)" }

$cscArgs = @(
  '/nologo', '/target:winexe', '/optimize+',
  "/win32icon:$dir\RayRadar.ico",
  "/win32manifest:$dir\RayRadar.manifest",
  "/out:$dir\RayRadar.exe",
  "/reference:$dir\lib\LibreHardwareMonitorLib.dll",
  '/reference:System.dll', '/reference:System.Drawing.dll',
  '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll'
) + $res + @("$dir\RayRadar.cs")

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
$f = Get-Item "$dir\RayRadar.exe"
"编译成功：$($f.FullName)  $([math]::Round($f.Length / 1MB, 2))MB"
