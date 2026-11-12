# 构建 Windows x64 独立运行包；所有输出限制在本仓库 artifacts 目录。
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
# 从项目读取版本号，将当前提交明确写入二进制和签名清单。
$project = Join-Path $repoRoot 'src\LanShare.App\LanShare.App.csproj'
$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
$commit = git -C $repoRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw '无法读取 Git 提交号。' }
$public = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $repoRoot 'src\LanShare.App\Assets\update-publisher.cer'))
$signer = Get-Item -LiteralPath ("Cert:\CurrentUser\My\" + $public.Thumbprint) -ErrorAction SilentlyContinue
if (-not $signer -or -not $signer.HasPrivateKey) { throw '当前用户证书库缺少本项目的发布私钥。' }
$outputDir = Join-Path $repoRoot "artifacts\SimpleLanShare-$version-win-x64"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
# 离线模式仅使用本机缓存和空本地源，跳过联网漏洞元数据查询；不改变正常构建。
$restoreOptions = @()
if ($Offline) {
    $offlineFeed = Join-Path $repoRoot 'artifacts\offline-feed'
    New-Item -ItemType Directory -Force -Path $offlineFeed | Out-Null
    $restoreOptions = @('--source', $offlineFeed, '-p:NuGetAudit=false')
}
# 禁用 PDB 输出，分发目录仅保留程序和使用说明。
& dotnet publish $project -p:SourceRevisionId=$commit -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $outputDir @restoreOptions
if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $outputDir '使用说明.md') -Force
# 签名覆盖两个有效载荷的哈希、目标平台、版本和 Git 提交。仅公钥随程序分发。
$exe = Join-Path $outputDir 'SimpleLanShare.exe'
$guide = Join-Path $outputDir '使用说明.md'
if ((Get-Item $exe).VersionInfo.ProductVersion -ne "$version+$commit") { throw '二进制版本与发布信息不匹配。' }
$release = [ordered]@{Product='SimpleLanShare'; Platform='win-x64'; Version=$version; Commit=$commit; ExeSha256=(Get-FileHash $exe).Hash; GuideSha256=(Get-FileHash $guide).Hash}
$bytes = [Text.Encoding]::UTF8.GetBytes(($release | ConvertTo-Json -Compress))
$manifest = Join-Path $outputDir 'update.json'
$signature = Join-Path $outputDir 'update.sig'
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($signer)
try {
    [IO.File]::WriteAllBytes($manifest, $bytes)
    [IO.File]::WriteAllBytes($signature, $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
} finally { $rsa.Dispose() }
$archivePath = Join-Path $repoRoot "artifacts\SimpleLanShare-$version-win-x64.zip"
Compress-Archive -LiteralPath $exe, $guide, $manifest, $signature -DestinationPath $archivePath -Force
Get-FileHash -LiteralPath $exe, $archivePath | Format-Table -AutoSize
