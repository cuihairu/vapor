# Vapor 测试运行脚本 (PowerShell)
# 用于运行测试并生成覆盖率报告

param(
    [switch]$Coverage,
    [switch]$Verbose,
    [string]$Filter = "",
    [switch]$Help
)

# Allow running testhost when the installed runtime is older than the TFM.
if (-not $env:DOTNET_ROLL_FORWARD) {
    $env:DOTNET_ROLL_FORWARD = "Major"
}

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

Write-Success "Vapor 测试运行器"
Write-Output "======================================"

# 构建测试命令（整个解决方案：单测 + 集成 + 性能 + E2E）
$testCmd = "dotnet test Vapor.sln --configuration Release --nologo"

if ($Verbose) {
    $testCmd += " --verbosity normal"
} else {
    $testCmd += " --verbosity minimal"
}

# 全量覆盖率（无过滤器）走下方串行收集循环：一次性 `dotnet test Vapor.sln --collect`
# 会间歇性静默产出坏报告（空报告 / 全零报告），串行 + 逐报告校验 + 重试才可信
# （见 tests/TESTING.md 2026-09-22 可靠性轮；Linux 侧委托 collect-coverage-serial.sh，
# 此处原生移植，Windows 不依赖 bash/python）。
# 带过滤器的运行只跑匹配子集，覆盖率仅作现场排查参考，保留单次收集路径
# （必须带 runsettings，否则测试程序集分母会把覆盖率拉低——见 tests/TESTING.md）。
$serialCoverage = $false
if ($Coverage) {
    if ($Filter -eq "") {
        $serialCoverage = $true
    } else {
        $testCmd += " --collect 'XPlat Code Coverage' --settings tests/coverlet.runsettings"
    }
}

if ($Filter -ne "") {
    $testCmd += " --filter '$Filter'"
}

# 限制 testhost 并行度：E2E（真实子进程）与 CP 调度器等真实时钟测试在满核
# 并行下会因资源竞争偶发超时（实测复现过 flake），限流后稳定。注意 `--` 之后
# 的 token 全部作为 runsettings 内联参数解析，必须放在所有 dotnet test 选项之后。
$testCmd += " -- RunConfiguration.MaxCpuCount=2"

# 清理历史残留的覆盖率报告，避免旧文件混入本次合并结果。
# 必须在运行测试之前执行——测试运行结束后这些路径上的文件就是本次的结果。
if ($Coverage) {
    Get-ChildItem -Path . -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "TestResults" } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# 校验单份 cobertura 报告：能解析且至少一行 hits>0 即有效（分支行照常计入，
# 与 coverage-summary.py 合并口径一致——coverlet 实际写 branch="True"，
# 大小写敏感的过滤在 sh/py 校验器里是死代码，而 PowerShell -ne 不区分大小写、
# 会真的排除分支行，故统一不过滤）。
# cobertura 报告带 DOCTYPE，[xml] 直接转换默认禁止 DTD 会抛异常，
# 必须走 XmlReader 并显式 DtdProcessing=Ignore。
# 「全零即坏」语义与 collect-coverage-serial.sh 的校验器一致（只适用于全量运行）。
function Test-CoverageReport {
    param([string]$Path)
    try {
        $settings = [System.Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
        $settings.XmlResolver = $null
        $doc = [System.Xml.XmlDocument]::new()
        $reader = [System.Xml.XmlReader]::Create($Path, $settings)
        try {
            $doc.Load($reader)
        } finally {
            $reader.Dispose()
        }
        foreach ($class in $doc.SelectNodes("//class")) {
            foreach ($line in $class.SelectNodes(".//line")) {
                if ([int]$line.GetAttribute("hits") -gt 0) {
                    return $true
                }
            }
        }
        return $false
    } catch {
        return $false
    }
}

# 逐项目串行收集覆盖率，逐报告校验，坏报告删除后重试（≤3 次）。
# 语义镜像 scripts/collect-coverage-serial.sh：
#   - 每项目独立 results-directory，attempt 日志落 $root/$name.attempt$attempt.log
#   - Vapor.E2E.Tests 免收集不校验（真实子进程继承 profiler 环境会毁掉收集会话，
#     报告结构性为空；见脚本头注）
#   - 每段注入 VAPOR_TEST_REDIS（有意让覆盖率轮顺带跑集成测试；结束后恢复原值）
#   - exit code 经 $script:SerialExitCode 传出，避免函数进度输出污染返回值管道
function Invoke-SerialCoverage {
    # 正斜杠路径：Windows 与 Linux pwsh 双平台通用（反斜杠在 Linux 是文件名字符）。
    $root = "TestResults/coverage-serial"
    if (Test-Path $root) {
        Remove-Item -Recurse -Force $root
    }
    New-Item -ItemType Directory -Path $root -Force | Out-Null

    $script:SerialExitCode = 0
    $oldRedis = $env:VAPOR_TEST_REDIS
    $env:VAPOR_TEST_REDIS = "localhost:6379"
    try {
        foreach ($proj in Get-ChildItem -Path "tests/*/*.Tests.csproj") {
            $name = $proj.BaseName
            $isE2E = $name -eq "Vapor.E2E.Tests"
            $attempt = 0
            while ($true) {
                $attempt++
                Write-Output "=== $name (attempt $attempt)"
                $dir = Join-Path $root "$name/TestResults"
                $log = Join-Path $root "$name.attempt$attempt.log"
                $dotnetArgs = @(
                    "test", $proj.FullName,
                    "--configuration", "Release",
                    "--no-build",
                    "--nologo",
                    "--settings", "tests/coverlet.runsettings",
                    "--results-directory", $dir
                )
                if (-not $isE2E) {
                    $dotnetArgs += @("--collect", "XPlat Code Coverage")
                }
                # `--` 之后的 token 全部作为 runsettings 内联参数解析，必须放最后。
                $dotnetArgs += @("--", "RunConfiguration.MaxCpuCount=2")
                & dotnet @dotnetArgs *> $log
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "PROJECT FAILED: $name (see $log)"
                    $script:SerialExitCode = 1
                    return
                }
                if ($isE2E) {
                    # 见头注：无报告预期，测试通过即唯一标准。
                    break
                }
                $valid = $false
                Get-ChildItem -Path $dir -Filter "coverage.cobertura.xml" -Recurse -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        if (Test-CoverageReport $_.FullName) {
                            $valid = $true
                        } else {
                            Remove-Item -Force $_.FullName -ErrorAction SilentlyContinue
                            Write-Output "  dropped corrupt coverage report: $($_.FullName)"
                        }
                    }
                if ($valid) {
                    break
                }
                if ($attempt -ge 3) {
                    Write-Error "NO VALID COVERAGE REPORT after $attempt attempts: $name"
                    $script:SerialExitCode = 1
                    return
                }
            }
        }
    } finally {
        if ($null -ne $oldRedis) {
            $env:VAPOR_TEST_REDIS = $oldRedis
        } else {
            Remove-Item Env:VAPOR_TEST_REDIS -ErrorAction SilentlyContinue
        }
    }
}

# 执行测试
if ($serialCoverage) {
    # 串行循环以 --no-build 跑 Release DLL——先显式构建，保证新鲜度
    # （陈旧 DLL 会让「验证通过」与改动无关），也保留隐式构建的体验。
    Write-Warning "构建 Release..."
    dotnet build Vapor.sln --configuration Release --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Error "构建失败"
        exit 1
    }

    Write-Warning "串行收集覆盖率（逐报告校验 + 重试）..."
    Invoke-SerialCoverage
    $exitCode = $script:SerialExitCode
} else {
    Write-Warning "运行测试..."
    $result = Invoke-Expression $testCmd
    $exitCode = $LASTEXITCODE
}

if ($exitCode -eq 0) {
    Write-Success "测试通过!"
} else {
    Write-Error "测试失败"
}

if ($Coverage -and $exitCode -eq 0) {
    Write-Output ""
    Write-Warning "处理覆盖率报告..."

    # 查找覆盖率文件（coverlet.collector 每个测试项目生成一份 cobertura 报告）
    $coverageFiles = Get-ChildItem -Path . -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "TestResults" }

    if ($coverageFiles) {
        Write-Success "覆盖率报告已生成:"
        foreach ($file in $coverageFiles) {
            Write-Output "  $($file.FullName)"
        }

        # 尝试显示摘要（如果 reportgenerator 可用）
        if (Get-Command reportgenerator -ErrorAction SilentlyContinue) {
            $reportDir = ".\TestResults\coveragereport"
            $reports = ($coverageFiles | ForEach-Object { $_.FullName }) -join ";"
            reportgenerator -reports $reports -targetdir $reportDir | Out-Null
            Write-Success "HTML 报告: $reportDir\index.html"
        }
    } else {
        Write-Warning "未找到覆盖率文件"
    }
}

exit $exitCode

