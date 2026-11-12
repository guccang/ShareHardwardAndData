# 构建 Windows x64 独立运行包；所有输出限制在本仓库 artifacts 目录。
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $repoRoot 'artifacts\SimpleLanShare-0.6.1-win-x64'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
# 离线模式仅使用本机缓存和空本地源，跳过联网漏洞元数据查询；不改变正常构建。
$restoreOptions = @()
if ($Offline) {
    $offlineFeed = Join-Path $repoRoot 'artifacts\offline-feed'
    New-Item -ItemType Directory -Force -Path $offlineFeed | Out-Null
    $restoreOptions = @('--source', $offlineFeed, '-p:NuGetAudit=false')
}
# 禁用 PDB 输出，分发目录仅保留程序和使用说明。
& dotnet publish (Join-Path $repoRoot 'src\LanShare.App\LanShare.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $outputDir @restoreOptions
if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $outputDir '使用说明.md') -Force
# ZIP 只包括显式列出的交付文件，排除之前生成的调试信息。
$archivePath = Join-Path $repoRoot 'artifacts\SimpleLanShare-0.6.1-win-x64.zip'
Compress-Archive -LiteralPath (Join-Path $outputDir 'SimpleLanShare.exe'), (Join-Path $outputDir '使用说明.md') -DestinationPath $archivePath -Force
Get-FileHash -LiteralPath (Join-Path $outputDir 'SimpleLanShare.exe'), $archivePath -Algorithm SHA256 | Format-Table -AutoSize
