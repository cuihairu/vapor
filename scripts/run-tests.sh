#!/bin/bash
# Vapor 测试运行脚本
# 用于运行测试并生成覆盖率报告

set -euo pipefail

# Allow running testhost with only an older-than-TFM runtime installed.
: "${DOTNET_ROLL_FORWARD:=Major}"
export DOTNET_ROLL_FORWARD

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# 默认值
COVERAGE=false
COVERAGE_SERIAL=false
VERBOSE=false
FILTER=""

# 解析命令行参数
while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--coverage)
            COVERAGE=true
            shift
            ;;
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        -f|--filter)
            FILTER="$2"
            shift 2
            ;;
        -h|--help)
            echo "用法: $0 [选项]"
            echo ""
            echo "选项:"
            echo "  -c, --coverage    生成代码覆盖率报告"
            echo "  -v, --verbose     详细输出"
            echo "  -f, --filter      测试过滤器 (例如: FullyQualifiedName~ActionRegistryTests)"
            echo "  -h, --help        显示帮助信息"
            echo ""
            echo "示例:"
            echo "  $0                           # 运行所有测试"
            echo "  $0 -c                        # 运行测试并生成覆盖率报告"
            echo "  $0 -f PingActionTests        # 只运行 PingActionTests"
            exit 0
            ;;
        *)
            echo -e "${RED}未知参数: $1${NC}"
            echo "使用 -h 或 --help 查看帮助"
            exit 1
            ;;
    esac
done

# 检查 dotnet 是否安装
if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}错误: dotnet 未安装或不在 PATH 中${NC}"
    echo "请安装 .NET SDK: https://dotnet.microsoft.com/download"
    exit 1
fi

echo -e "${GREEN}Vapor 测试运行器${NC}"
echo "======================================"

# 构建测试命令（整个解决方案：单测 + 集成 + 性能 + E2E）
TEST_CMD="dotnet test Vapor.sln --configuration Release --nologo"

if [ "$VERBOSE" = true ]; then
    TEST_CMD="$TEST_CMD --verbosity normal"
else
    TEST_CMD="$TEST_CMD --verbosity minimal"
fi

# 全量覆盖率（无过滤器）走串行收集脚本：一次性 `dotnet test Vapor.sln --collect`
# 会间歇性静默产出坏报告（空报告 / 全零报告），串行 + 逐报告校验 + 重试才可信
# （见 collect-coverage-serial.sh 头注与 tests/TESTING.md 2026-09-22 可靠性轮）。
# 带过滤器的运行只跑匹配子集，覆盖率仅作现场排查参考，保留单次收集路径
# （校验器「全零即坏」的语义不适用于子集运行——未匹配项目本就零命中）。
if [ "$COVERAGE" = true ] && [ -z "$FILTER" ]; then
    COVERAGE_SERIAL=true
elif [ "$COVERAGE" = true ]; then
    # tests/coverlet.runsettings 排除源生成器产物（obj/**/*.g.cs）；
    # 结构性集成壳在源码里挂 [ExcludeFromCodeCoverage]（见 tests/TESTING.md）。
    TEST_CMD="$TEST_CMD --collect 'XPlat Code Coverage' --settings tests/coverlet.runsettings"
fi

if [ -n "$FILTER" ]; then
    TEST_CMD="$TEST_CMD --filter \"$FILTER\""
fi

# 限制 testhost 并行度：E2E（真实子进程）与 CP 调度器等真实时钟测试在满核
# 并行下会因资源竞争偶发超时（实测复现过 flake），限流后稳定。注意 `--` 之后
# 的 token 全部作为 runsettings 内联参数解析，必须放在所有 dotnet test 选项之后。
TEST_CMD="$TEST_CMD -- RunConfiguration.MaxCpuCount=2"

# 清理历史残留的覆盖率报告，避免旧文件混入本次合并结果。
# 必须在运行测试之前执行——测试运行结束后这些路径上的文件就是本次的结果。
if [ "$COVERAGE" = true ]; then
    find . -path "*/TestResults/*/coverage.cobertura.xml" -delete 2> /dev/null || true
fi

# 执行测试
if [ "$COVERAGE_SERIAL" = true ]; then
    # 串行收集脚本以 --no-build 跑 Release DLL——先显式构建，既保证新鲜度
    # （陈旧 DLL 会让「验证通过」与改动无关），也保留 dotnet test 原有的隐式构建体验。
    echo -e "${YELLOW}构建 Release...${NC}"
    if ! dotnet build Vapor.sln --configuration Release --nologo -v q; then
        echo -e "${RED}构建失败${NC}"
        exit 1
    fi

    echo -e "${YELLOW}串行收集覆盖率（逐报告校验 + 重试）...${NC}"
    set +e
    ./scripts/collect-coverage-serial.sh -- RunConfiguration.MaxCpuCount=2
    TEST_EXIT_CODE=$?
    set -e
else
    echo -e "${YELLOW}运行测试...${NC}"
    set +e
    eval $TEST_CMD
    TEST_EXIT_CODE=$?
    set -e
fi

if [ $TEST_EXIT_CODE -eq 0 ]; then
    echo -e "${GREEN}测试通过!${NC}"
else
    echo -e "${RED}测试失败${NC}"
fi

if [ "$COVERAGE" = true ] && [ $TEST_EXIT_CODE -eq 0 ]; then
    echo ""
    echo -e "${YELLOW}处理覆盖率报告...${NC}"

    # 串行收集轮：先用 coverage-summary.py 给出行级汇总（与 CI 门禁同一套
    # 口径），失败不阻塞——文本摘要只是本地的便利设施。
    if [ "$COVERAGE_SERIAL" = true ] && command -v python3 &> /dev/null; then
        python3 scripts/coverage-summary.py TestResults/coverage-serial || true
        echo ""
    fi

    # 查找覆盖率文件（coverlet.collector 每个测试项目生成一份 cobertura 报告）
    COVERAGE_FILES=$(find . -path "*/TestResults/*/coverage.cobertura.xml" | tr '\n' ' ')

    if [ -n "$COVERAGE_FILES" ]; then
        echo -e "${GREEN}覆盖率报告已生成:${NC}"
        for f in $COVERAGE_FILES; do
            echo "  $f"
        done

        # 尝试显示摘要（如果 reportgenerator 可用）
        if command -v reportgenerator &> /dev/null; then
            REPORT_DIR="./TestResults/coveragereport"
            reportgenerator -reports:"$(echo $COVERAGE_FILES | tr ' ' ';')" -targetdir:"$REPORT_DIR" &> /dev/null
            echo -e "${GREEN}HTML 报告: $REPORT_DIR/index.html${NC}"
        fi
    else
        echo -e "${YELLOW}未找到覆盖率文件${NC}"
    fi
fi

exit $TEST_EXIT_CODE

