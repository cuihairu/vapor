#!/bin/bash
# SteamControl 测试运行脚本
# 用于运行测试并生成覆盖率报告

set -e

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# 默认值
COVERAGE=false
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

echo -e "${GREEN}SteamControl 测试运行器${NC}"
echo "======================================"

# 构建测试命令
TEST_CMD="dotnet test tests/SteamControl.Steam.Core.Tests/SteamControl.Steam.Core.Tests.csproj --configuration Release --nologo"

if [ "$VERBOSE" = true ]; then
    TEST_CMD="$TEST_CMD --verbosity normal"
else
    TEST_CMD="$TEST_CMD --verbosity minimal"
fi

if [ "$COVERAGE" = true ]; then
    TEST_CMD="$TEST_CMD --collect:\"XPlat Code Coverage\" --results-directory ./TestResults"
fi

if [ -n "$FILTER" ]; then
    TEST_CMD="$TEST_CMD --filter \"$FILTER\""
fi

# 执行测试
echo -e "${YELLOW}运行测试...${NC}"
eval $TEST_CMD
TEST_EXIT_CODE=$?

if [ $TEST_EXIT_CODE -eq 0 ]; then
    echo -e "${GREEN}测试通过!${NC}"
else
    echo -e "${RED}测试失败${NC}"
fi

# 处理覆盖率报告
if [ "$COVERAGE" = true ] && [ $TEST_EXIT_CODE -eq 0 ]; then
    echo ""
    echo -e "${YELLOW}处理覆盖率报告...${NC}"

    # 查找覆盖率文件
    COVERAGE_FILE=$(find ./TestResults -name "coverage.opencover.xml" | head -n 1)

    if [ -n "$COVERAGE_FILE" ]; then
        echo -e "${GREEN}覆盖率报告已生成: $COVERAGE_FILE${NC}"

        # 尝试显示摘要（如果 reportgenerator 可用）
        if command -v reportgenerator &> /dev/null; then
            REPORT_DIR="./TestResults/coveragereport"
            reportgenerator -reports:"$COVERAGE_FILE" -targetdir:"$REPORT_DIR" &> /dev/null
            echo -e "${GREEN}HTML 报告: $REPORT_DIR/index.html${NC}"
        fi
    else
        echo -e "${YELLOW}未找到覆盖率文件${NC}"
    fi
fi

exit $TEST_EXIT_CODE
