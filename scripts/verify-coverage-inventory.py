#!/usr/bin/env python3
"""覆盖率分母完整性机械校验:程序集清单与排除登记三向比对。

行/分支双 100% 的前提是分母完整:新程序集漏收、或悄悄挂 [ExcludeFromCodeCoverage]
未在册,数字再漂亮也是假象。本脚本三向比对,任一双向差额即退出码 1:
  1. sln 可收集项目(*.Tests 后缀除外) ⇄ TESTING.md「收集程序集」清单
  2. src/tools 全部 [ExcludeFromCodeCoverage] 注解点 ⇄ TESTING.md「排除登记」表
  3. --reports 指向覆盖率报告目录时,报告实际收集的程序集 ⇄ 「收集程序集」清单

用法: python3 scripts/verify-coverage-inventory.py [sln] [TESTING.md] [--reports DIR]
CI(format job)以默认参数跑静态两向;覆盖率轮追加 --reports 跑第三向。
"""

import argparse
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description="覆盖率分母完整性校验")
parser.add_argument("sln", nargs="?", default="Vapor.sln")
parser.add_argument("testing_md", nargs="?", default="tests/TESTING.md")
parser.add_argument("--reports", default=None, help="覆盖率报告根目录(含 */TestResults/*/coverage.cobertura.xml)")
opts = parser.parse_args()

failures: list[str] = []


def add(why: str, extra: list[str]) -> None:
    for item in extra:
        failures.append(f"{why}: {item}")


# ---------- TESTING.md 侧:锚点围栏内解析 ----------

md = open(opts.testing_md, encoding="utf-8").read()


def fenced(section: str) -> str:
    m = re.search(
        rf"<!-- verify-coverage-inventory:{section} -->\n(.*?)<!-- /verify-coverage-inventory:{section} -->",
        md, re.S)
    if not m:
        sys.exit(f"FAIL: {opts.testing_md} 缺少 verify-coverage-inventory:{section} 锚点围栏(不得删围栏绕过校验)")
    return m.group(1)


listed_asms = {line[2:].strip()
               for line in fenced("asms").splitlines()
               if line.startswith("- ")}

listed_exclusions: set[tuple[str, str]] = set()
for line in fenced("exclusions").splitlines():
    cells = [c.strip() for c in line.split("|")]
    if len(cells) >= 4 and cells[1] and cells[1] != "文件" and not set(cells[1]) <= {"-"}:
        listed_exclusions.add((cells[1], cells[2]))

if not listed_asms:
    sys.exit("FAIL: 收集程序集清单为空")
if not listed_exclusions:
    sys.exit("FAIL: 排除登记表为空")

# ---------- 第 1 向:sln 可收集项目 ⇄ 在册清单 ----------

sln_projects = re.findall(r'^Project\([^\n]*?\)\s*=\s*"([^"]+)",\s*"[^"]+\.csproj"', open(opts.sln, encoding="utf-8").read(), re.M)
collectible = {name for name in sln_projects if not name.endswith(".Tests")}
if not collectible:
    sys.exit(f"FAIL: {opts.sln} 未解析出任何项目")
add("sln 有项目未入收集清单", sorted(collectible - listed_asms))
add("收集清单有 sln 不存在的项目", sorted(listed_asms - collectible))

# ---------- 第 2 向:源码注解点 ⇄ 排除登记 ----------

# 注解行(strip 后精确等于,排除 /// 与 // 注释里的提及);符号取其后
# 5 行内第一条类型或方法声明的名字。
TYPE_RE = re.compile(r"\b(?:class|struct|record|enum)\s+(\w+)")
METHOD_RE = re.compile(r"(?:private|protected|internal|public)[^\n({;=]*?\b(\w+)\s*\(")

code_annotations: set[tuple[str, str]] = set()
for base in ("src", "tools"):
    for path in glob.glob(f"{base}/**/*.cs", recursive=True):
        if f"{os.sep}obj{os.sep}" in path or f"{os.sep}bin{os.sep}" in path:
            continue
        lines = open(path, encoding="utf-8").read().splitlines()
        for i, line in enumerate(lines):
            if line.strip() != "[ExcludeFromCodeCoverage]":
                continue
            symbol = None
            for follow in lines[i + 1:i + 6]:
                m = TYPE_RE.search(follow) or METHOD_RE.search(follow)
                if m:
                    symbol = m.group(1)
                    break
            if symbol is None:
                sys.exit(f"FAIL: {path}:{i + 1} 注解后 5 行内未解析出符号(登记表与解析器都需同步)")
            code_annotations.add((path.replace(os.sep, "/"), symbol))

add("源码有注解未入排除登记", sorted(f"{f} :: {s}" for f, s in code_annotations - listed_exclusions))
add("排除登记有源码不存在的注解", sorted(f"{f} :: {s}" for f, s in listed_exclusions - code_annotations))

# ---------- 第 3 向(可选):报告实际收集程序集 ⇄ 在册清单 ----------

reports_note = ""
if opts.reports:
    report_asms: set[str] = set()
    for path in glob.glob(f"{opts.reports}/*/TestResults/*/coverage.cobertura.xml"):
        for pkg in ET.parse(path).getroot().iter("package"):
            name = pkg.get("name") or ""
            if name:
                report_asms.add(name.split(",")[0])
    if not report_asms:
        sys.exit(f"FAIL: {opts.reports} 未解析出任何覆盖率报告/程序集")
    add("报告未收集的在册程序集(分母漏收)", sorted(listed_asms - report_asms))
    add("报告有但不在册的程序集", sorted(report_asms - listed_asms))
    reports_note = f";报告程序集 {len(report_asms)} 一致"

# ---------- 判读 ----------

if failures:
    print(f"FAIL: 覆盖率分母完整性校验 {len(failures)} 处不一致")
    for item in failures:
        print(f"  - {item}")
    sys.exit(1)
print(f"OK: sln 可收集项目 {len(collectible)}、在册程序集 {len(listed_asms)}、"
      f"注解点 {len(code_annotations)}、排除登记 {len(listed_exclusions)} 全部一致{reports_note}")
