# 把 KeyboundBrawler 注册进 Dalamud 开发插件列表（复制 + 改配置）
#
# 用法（游戏必须完全退出！）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/register_devplugin.ps1
#
# 做的事：
#   1. 检查游戏进程，运行中则直接退出（避免 Dalamud 退出时覆盖配置）
#   2. Release 编译（可选 -Build）
#   3. 复制插件到 C:\Users\12841\Desktop\ff14本地插件\KeyboundBrawler\
#   4. 调 register_dalamud_config.py 写 dalamudConfig.json（自动备份）

param(
    [switch]$Build,
    [string]$InstallBase = "C:\Users\12841\Desktop\ff14本地插件"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "dalamud-plugin\KeyboundBrawler"

$game = Get-Process -Name ffxiv_dx11, ffxiv -ErrorAction SilentlyContinue
if ($game) {
    Write-Host "游戏正在运行（PID $($game.Id -join ', ')）。" -ForegroundColor Yellow
    Write-Host "请先完全退出游戏再运行本脚本，否则 Dalamud 退出时会覆盖配置。"
    exit 1
}

if ($Build) {
    Write-Host "Release 编译中..."
    dotnet build (Join-Path $projectDir "KeyboundBrawler.csproj") -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "编译失败" }
}

$releaseDir = Join-Path $projectDir "bin\Release"
$dll = Join-Path $releaseDir "KeyboundBrawler.dll"
if (-not (Test-Path $dll)) {
    throw "找不到 $dll，先加 -Build 参数或手动 dotnet build -c Release"
}

$target = Join-Path $InstallBase "KeyboundBrawler"
Write-Host "复制插件到 $target"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $releaseDir "KeyboundBrawler.dll") $target -Force
Copy-Item (Join-Path $releaseDir "KeyboundBrawler.json") $target -Force
$dataTarget = Join-Path $target "data"
Remove-Item $dataTarget -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $releaseDir "data") $dataTarget -Recurse -Force

$python = "python"
$script = Join-Path $PSScriptRoot "register_dalamud_config.py"
& $python $script (Join-Path $target "KeyboundBrawler.dll")
if ($LASTEXITCODE -ne 0) { throw "注册配置失败（检查 python 是否可用）" }

Write-Host ""
Write-Host "完成。启动游戏后插件会自动加载（StartOnBoot=true）。" -ForegroundColor Green
