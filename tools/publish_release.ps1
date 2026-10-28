# 发布新版到 GitHub Release 并更新 pluginmaster.json
#
# 用法（在仓库根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/publish_release.ps1 -Version 0.3.1
#
# 做的事：
#   1. 从 csproj 读版本（或 -Version 覆盖）
#   2. dotnet build -c Release，生成 latest.zip
#   3. 在 GitHub 创建/复用 tag v<版本> 的 Release，上传 KeyboundBrawler.zip
#   4. 更新 pluginmaster.json 的 AssemblyVersion / DownloadLink / LastUpdate
#   5. git commit + push
#
# 前提：Windows 凭据管理器里有 GitHub token（git:https://github.com）

param(
    [string]$Version = "",
    [string]$Repo = "SiGeoMa/ff14-keybound-brawler",
    [string]$Proxy = "http://127.0.0.1:7897",
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Net.Http

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CredReader {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct CREDENTIAL {
    public int Flags; public int Type; public string TargetName; public string Comment;
    public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public IntPtr Attributes;
    public string TargetAlias; public string UserName;
  }
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
  [DllImport("advapi32.dll")]
  public static extern void CredFree(IntPtr credential);
  public static string Read(string target) {
    IntPtr ptr;
    if (!CredRead(target, 1, 0, out ptr)) return null;
    try {
      var cred = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
      var bytes = new byte[cred.CredentialBlobSize];
      Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
      return System.Text.Encoding.Unicode.GetString(bytes);
    } finally { CredFree(ptr); }
  }
}
"@

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "dalamud-plugin\KeyboundBrawler"
$csproj = Join-Path $projectDir "KeyboundBrawler.csproj"

if (-not $Version) {
    $m = Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>'
    if (-not $m) { throw "csproj 里没有 Version" }
    $Version = $m.Matches[0].Groups[1].Value.TrimEnd('.0')
    $parts = $Version.Split('.')
    $Version = ($parts + @('0', '0', '0', '0'))[0..3] -join '.'
} else {
    $parts = $Version.Split('.')
    $Version = ($parts + @('0', '0', '0', '0'))[0..3] -join '.'
}

Write-Host "版本: $Version"
dotnet build $csproj -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

$zip = Join-Path $projectDir "bin\Release\KeyboundBrawler\latest.zip"
if (-not (Test-Path $zip)) { throw "找不到 $zip" }

$token = [CredReader]::Read("git:https://github.com")
if (-not $token) { throw "没读到 GitHub token（git:https://github.com）" }

$headers = @{ Authorization = "token $token"; "User-Agent" = "dsh"; Accept = "application/vnd.github+json" }
$tag = "v$Version"
$assetName = "KeyboundBrawler.zip"

# 1) 创建/复用 release
$body = @{
    tag_name   = $tag
    name       = $tag
    body       = "KeyboundBrawler $tag"
    draft      = $false
    prerelease = $false
} | ConvertTo-Json

try {
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers -Proxy $Proxy -Body $body -ContentType "application/json" -TimeoutSec 40
    Write-Host "Release 创建: $($release.html_url)"
} catch {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$tag" -Headers $headers -Proxy $Proxy -TimeoutSec 30
    Write-Host "复用 Release: $($release.html_url)"
}

# 2) 上传 zip（同名 asset 先删）
$existing = $release.assets | Where-Object { $_.name -eq $assetName }
if ($existing) {
    Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$Repo/releases/assets/$($existing.id)" -Headers $headers -Proxy $Proxy | Out-Null
}

$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false   # uploads.github.com 走代理会失败，直连
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromMinutes(3)
$client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue("token", $token)
$client.DefaultRequestHeaders.UserAgent.ParseAdd("dsh")

$bytes = [System.IO.File]::ReadAllBytes($zip)
$content = New-Object System.Net.Http.ByteArrayContent((,$bytes))
$content.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue("application/zip")
$resp = $client.PostAsync("https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$assetName", $content).GetAwaiter().GetResult()
if (-not $resp.IsSuccessStatusCode) {
    throw "上传失败: $([int]$resp.StatusCode) $($resp.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
}

$assetUrl = "https://github.com/$Repo/releases/download/$tag/$assetName"
Write-Host "Asset: $assetUrl"

# 3) 更新 pluginmaster.json
$manifestPath = Join-Path $repoRoot "pluginmaster.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$entry = $manifest[0]
$entry.AssemblyVersion = $Version
$entry.DownloadLink = $assetUrl
$entry.DownloadLinkInstall = $assetUrl
$entry.DownloadLinkUpdate = $assetUrl
$entry.LastUpdate = [DateTimeOffset]::Now.ToUnixTimeSeconds()
$json = "[" + ($entry | ConvertTo-Json -Depth 6) + "]"
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "pluginmaster.json 已更新"

# 4) 提交推送
if (-not $NoPush) {
    Push-Location $repoRoot
    git add pluginmaster.json
    git -c user.name="SiGeoMa" -c user.email="kailakuffelw2k@gmail.com" commit -m "release: $tag" 2>&1 | Out-Null
    $pushUrl = "https://x-access-token:$token@github.com/$Repo.git"
    git push $pushUrl main 2>&1 | Select-Object -Last 2
    Pop-Location
    Write-Host "已推送"
}

Write-Host ""
Write-Host "完成。Dalamud 第三方仓库地址：https://raw.githubusercontent.com/$Repo/main/pluginmaster.json" -ForegroundColor Green
