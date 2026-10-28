# 把 KeyboundBrawler 安装 / 更新到 Dalamud devPlugins
#
# 用法（在仓库根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install_dalamud_devplugin.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install_dalamud_devplugin.ps1 -Build   # 先 Release 编译再安装
#
# 说明：安装到 devPlugins 后需要重启游戏（或 /xlplugins → Dev Tools → Scan Dev Plugins）才生效。

param(
    [switch]$Build,
    [string]$DalamudRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "dalamud-plugin\KeyboundBrawler"

if (-not $DalamudRoot) {
    $candidates = @(
        (Join-Path $env:APPDATA "XIVLauncherCN"),
        (Join-Path $env:APPDATA "XIVLauncher")
    ) | Where-Object { Test-Path (Join-Path $_ "addon\Hooks\dev") }

    # 注意：管道可能只返回一个字符串，必须用 @() 包一层，否则下标 [0] 取到的是首字符
    $candidates = @($candidates)
    if ($candidates.Count -eq 0) {
        throw "找不到 Dalamud 安装目录（XIVLauncherCN / XIVLauncher 都没有 addon\Hooks\dev）"
    }

    $DalamudRoot = $candidates[0]
}

$devPlugins = Join-Path $DalamudRoot "devPlugins\KeyboundBrawler"
Write-Host "Dalamud 根目录: $DalamudRoot"
Write-Host "安装目标:      $devPlugins"

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

New-Item -ItemType Directory -Force $devPlugins | Out-Null
Copy-Item $dll (Join-Path $devPlugins "KeyboundBrawler.dll") -Force
Copy-Item (Join-Path $releaseDir "KeyboundBrawler.json") (Join-Path $devPlugins "KeyboundBrawler.json") -Force

$dataTarget = Join-Path $devPlugins "data"
Remove-Item $dataTarget -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $releaseDir "data") $dataTarget -Recurse -Force

Write-Host ""
Write-Host "已安装到 $devPlugins" -ForegroundColor Green
Write-Host "下一步：重启游戏，或游戏内 /xlplugins → Dev Tools → Scan Dev Plugins，然后启用 KeyboundBrawler。"
Write-Host "游戏内输入 /kbb 打开窗口。"
