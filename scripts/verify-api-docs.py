#!/usr/bin/env python3
"""api.md 端点清单一致性静态校验（零 .NET 依赖）。

背景：维护轮十六盘点实证 docs 面三维度（REST 端点 / env 清单 / 默认值）
当时全同步，但这份同步纯靠各轮自觉——「新增端点忘写 api.md」或「删端点
留僵尸文档」CI 不会红。本脚本把端点清单同步固化为机械守护，与
verify-testing-docs.py（测试计数）同型：新代码无测试即红 → 测试加了文档
没同步即红 → 新端点没写文档即红。

校验（双向集合比对，路由模板逐字比对，不剥 query、不归一占位符）：
  1. 代码侧：Program.cs 全部 Map(Get|Post|Put|Delete)("...") 字面量路由
     → (METHOD, path) 集合。
  2. 文档侧：api.md 全部 `METHOD /path` 形态 → (METHOD, path) 集合。
  3. 代码有文档无 → FAIL（新端点缺文档）。
  4. 文档有代码无 → FAIL（僵尸端点）。
  5. 两侧任一解析零条目 → FAIL（文件结构变化时显式失败，不静默绿）。

文档端点行只写路由路径——query 参数写在正文 Query 行（achievements 小节
先例），把 `?x=` 拼进端点标题会被判定漂移。

用法：python3 scripts/verify-api-docs.py [Program.cs] [api.md]
退出码：0 = ALL GREEN，1 = 有 FAIL（逐条打印）。
"""
import re
import sys

METHOD = {"Get": "GET", "Post": "POST", "Put": "PUT", "Delete": "DELETE"}


def parse_code(text):
    return {(METHOD[m.group(1)], m.group(2))
            for m in re.finditer(r'\bMap(Get|Post|Put|Delete)\(\s*"([^"]*)"', text)}


def parse_doc(text):
    return {(m.group(1), m.group(2))
            for m in re.finditer(r"`(GET|POST|PUT|DELETE) ([^`\n]+)`", text)}


def main():
    cs_path = sys.argv[1] if len(sys.argv) > 1 else "src/Vapor.ControlPlane/Program.cs"
    md_path = sys.argv[2] if len(sys.argv) > 2 else "docs/api.md"
    with open(cs_path, encoding="utf-8") as fh:
        code = parse_code(fh.read())
    with open(md_path, encoding="utf-8") as fh:
        doc = parse_doc(fh.read())

    failures = []
    if not code:
        failures.append(f"{cs_path}: 未解析到任何 Map(Get|Post|Put|Delete) 字面量路由")
    if not doc:
        failures.append(f"{md_path}: 未解析到任何 `METHOD /path` 端点行")
    for entry in sorted(code - doc):
        failures.append(f"代码有文档无: {entry[0]} {entry[1]}（新端点缺 api.md 条目）")
    for entry in sorted(doc - code):
        failures.append(f"文档有代码无: {entry[0]} {entry[1]}（僵尸端点，路由已不存在）")

    print(f"代码路由 {len(code)} 个，api.md 端点行 {len(doc)} 个")
    if failures:
        for f in failures:
            print(f"FAIL: {f}")
        sys.exit(1)
    print("ALL GREEN")


if __name__ == "__main__":
    main()
