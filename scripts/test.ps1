# 在临时回环网络中运行核心测试，再运行不改写剪贴板的 Windows 专项自检。
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& dotnet run --project (Join-Path $repoRoot 'tests\LanShare.Tests\LanShare.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '网络集成测试失败。' }
& dotnet build (Join-Path $repoRoot 'src\LanShare.App\LanShare.App.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw '桌面应用构建失败。' }
$outputDir = Join-Path $repoRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$report = Join-Path $outputDir 'windows-self-test.txt'
# 预览窗口完全透明，不弹出到用户桌面，也不建立网络连接。
$appPath = Join-Path $repoRoot 'src\LanShare.App\bin\Release\net8.0-windows\SimpleLanShare.exe'
$testProcess = Start-Process -FilePath $appPath -ArgumentList '--self-test', ('"' + $report + '"') -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath $report
if ($testProcess.ExitCode -ne 0) { throw 'Windows 自检失败。' }
