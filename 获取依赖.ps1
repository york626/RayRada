# 获取依赖.ps1 -- 下载 LibreHardwareMonitor 官方发布包，把里面用到的 DLL 解压到 lib\
# 用法：powershell -ExecutionPolicy Bypass -File .\获取依赖.ps1
# 说明：RayRadar 只用到这些 DLL（不修改其源码），编译时会被嵌进单文件 exe。
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$lib = Join-Path $dir 'lib'
New-Item -ItemType Directory -Force -Path $lib | Out-Null

Write-Host '查询 LibreHardwareMonitor 最新发布包…'
$apiDirect = 'https://api.github.com/repos/LibreHardwareMonitor/LibreHardwareMonitor/releases/latest'
$apiMirror = 'https://gh-proxy.com/' + $apiDirect
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$rel = $null
foreach ($u in @($apiDirect, $apiMirror)) {
  try { $rel = Invoke-RestMethod -Uri $u -Headers @{ 'User-Agent' = 'RayRada' } -TimeoutSec 25; break } catch { }
}
if (-not $rel) { throw '无法访问 GitHub API。可手动下载 LibreHardwareMonitor 发布包，把其中的 DLL 放进 lib\ 即可。' }

$asset = $rel.assets | Where-Object { $_.name -match 'net472.*\.zip$' } | Select-Object -First 1
if (-not $asset) { $asset = $rel.assets | Where-Object { $_.name -match '\.zip$' } | Select-Object -First 1 }
if (-not $asset) { throw "发布包 $($rel.tag_name) 里没有 zip 资产" }
Write-Host "选中：$($asset.name)（$([math]::Round($asset.size/1MB,1))MB，版本 $($rel.tag_name)）"

$zip = Join-Path $env:TEMP $asset.name
$tmp = Join-Path $env:TEMP ('lhm_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$urls = @($asset.browser_download_url, 'https://gh-proxy.com/' + $asset.browser_download_url)
$ok = $false
foreach ($u in $urls) {
  try {
    Write-Host "下载：$u"
    Invoke-WebRequest -Uri $u -OutFile $zip -Headers @{ 'User-Agent' = 'RayRada' } -TimeoutSec 300
    $ok = $true; break
  } catch { Write-Host "  失败：$($_.Exception.Message)" }
}
if (-not $ok) { throw '下载失败。请手动下载该发布包并把 DLL 放进 lib\。' }

Write-Host '解压 DLL…'
Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
$n = 0
Get-ChildItem $tmp -Recurse -File -Filter *.dll | ForEach-Object {
  Copy-Item $_.FullName (Join-Path $lib $_.Name) -Force
  $n++
}
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Write-Host "完成：$n 个 DLL 已放入 $lib"
Write-Host '现在可以运行 .\编译.cmd 重新编译。'
