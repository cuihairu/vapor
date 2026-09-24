#!/bin/bash
# 逐类测试计数：扫描目录下各项目的 count.trx，按测试类聚合 case 数。
# 生成 TRX：dotnet test <proj> -c Release --no-build --nologo \
#   --logger "trx;LogFileName=count.trx" --results-directory "<root>/<proj>"
# 用法：./scripts/classify-trx.sh <root>   （root 下每个子目录含一个 count.trx）
set -euo pipefail
root="${1:?usage: classify-trx.sh <root-with-count.trx-subdirs>}"
for dir in "$root"/*/; do
  name=$(basename "$dir")
  f="$dir/count.trx"
  [ -f "$f" ] || { echo "MISSING TRX: $name"; continue; }
  echo "##### $name"
  # theory 展开的 testName 带 "(a: 1, b: 2)" 后缀，剥离括号段后取类全名的倒数第二段。
  grep -oP '(?<=testName=")[^"]+' "$f" | sed 's/ (.*//;s/(.*//' \
    | awk -F. '{print $(NF-1)}' | sort | uniq -c | sort -rn
done
