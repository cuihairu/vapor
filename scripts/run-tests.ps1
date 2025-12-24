# SteamControl 测试运行脚本 (PowerShell)
# 用于运行测试并生成覆盖率报告

param(
    [switch]$Coverage,
    [switch]$Verbose,
    [string]$Filter = "",
    [switch]$Help
)

# 颜色输出函数
function Write-ColorOutput($ForegroundColor) {
    $fc = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = $ForegroundColor
    if ($args) {
        Write-Output $args
    }
    $host.UI.RawUI.ForegroundColor = $fc
}

function Write-Success { Write-ColorOutput Green @Args }
function Write-Error { Write-ColorOutput Red @Args }
function Write-Warning { Write-ColorOutput Yellow @Args }

# 显示帮助
if ($Help) {
    Write-Output "用法: .\run-tests.ps1 [选项]"
    Write-Output ""
    Write-Output "选项:"
    Write-Output "  -Coverage          生成代码覆盖率报告"
    Write-Output "  -Verbose           详细输出"
    Write-Output "  -Filter <string>   测试过滤器"
    Write-Output "  -Help              显示帮助信息"
    Write-Output ""
    Write-Output "示例:"
    Write-Output "  .\run-tests.ps1                # 运行所有测试"
    Write-Output "  .\run-tests.ps1 -Coverage      # 运行测试并生成覆盖率报告"
    Write-Output "  .\run-tests.ps1 -Filter PingActionTests"
    exit 0
}

# 检查 dotnet 是否安装
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "错误: dotnet 未安装或不在 PATH 中"
    Write-Output "请安装 .NET SDK: https://dotnet.microsoft.com/download"
    exit 1
}

Write-Success "SteamControl 测试运行器"
Write-Output "======================================"

# 构建测试命令
$testCmd = "dotnet test tests/SteamControl.Steam.Core.Tests/SteamControl.Steam.Core.Tests.csproj --configuration Release --nologo"

if ($Verbose) {
    $testCmd += " --verbosity normal"
} else {
    $testCmd += " --verbosity minimal"
}

if ($Coverage) {
    $testCmd += " --collect:'XPlat Code Coverage' --results-directory ./TestResults"
}

if ($Filter -ne "") {
    $testCmd += " --filter '$Filter'"
}

# 执行测试
Write-Warning "运行测试..."
$result = Invoke-Expression $testCmd
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    Write-Success "测试通过!"
} else {
    Write-Error "测试失败"
}

# 处理覆盖率报告
if ($Coverage -and $exitCode -eq 0) {
    Write-Output ""
    Write-Warning "处理覆盖率报告..."

    # 查找覆盖率文件
    $coverageFile = Get-ChildItem -Path .\TestResults -Filter "coverage.opencover.xml" -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($coverageFile) {
        Write-Success "覆盖率报告已生成: $($coverageFile.FullName)"

        # 尝试显示摘要（如果 reportgenerator 可用）
        if (Get-Command reportgenerator -ErrorAction SilentlyContinue) {
            $reportDir = ".\TestResults\coveragereport"
            reportgenerator -reports "$($coverageFile.FullName)" -targetdir $reportDir | Out-Null
            Write-Success "HTML 报告: $reportDir\index.html"
        }
    } else {
        Write-Warning "未找到覆盖率文件"
    }
}

exit $exitCode
