#!/bin/bash
# 逐类测试计数：扫描目录下各项目的 count.trx，按测试类聚合 case 数。
# 生成 TRX：dotnet test <proj> -c Release --no-build --nologo \
#   --logger "trx;LogFileName=count.trx" --results-directory "<root>/<proj>"
# 用法：./scripts/classify-trx.sh <root>   （root 下每个嵌套 <Project>/TestResults/count.trx）
set -euo pipefail
root="${1:?usage: classify-trx.sh <root-with-count.trx-subdirs>}"
for dir in "$root"/*/; do
  proj_dir=$(basename "$dir")
  # count.trx 位于 <root>/<Project>/TestResults/count.trx
  f="$dir/TestResults/count.trx"
  [ -f "$f" ] || { echo "MISSING TRX: $proj_dir"; continue; }
  echo "##### $proj_dir"
  # theory 展开的 testName 带 "(a: 1, b: 2)" 后缀，剥离括号段后取类全名的倒数第二段。
  grep -oP '(?<=testName=")[^"]+' "$f" | sed 's/ (.*//;s/(.*//' \
    | awk -F. '{print $(NF-1)}' | sort | uniq -c | sort -rn
done
