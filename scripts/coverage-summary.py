#!/usr/bin/env python3
"""合并各测试项目生成的 cobertura 报告,输出按程序集的行/分支覆盖率摘要。

用法: ./scripts/coverage-summary.py [tests 根目录] [--min 百分比] [--min-branch 百分比]

--min N:合计行覆盖率低于 N 时以退出码 1 失败(CI 门禁)。基线数字与
tests/TESTING.md 的统计同步演进。
--min-branch N:合计分支覆盖率低于 N 时以退出码 1 失败。分支覆盖按
condition-coverage 统计(分支行内每个布尔子条件的命中度),行覆盖 100%
不蕴含分支覆盖 100%——一行执行过不等于它的每个条件结果都被取到。

注意:不同 testhost 生成的报告里 filename 前缀写法不一致(绝对路径、
"src/<项目>/..."、"<项目>/..."、裸文件名都出现过),必须按程序集名归一化,
否则同一行会被重复计入分母,覆盖率被系统性压低。
"""

import argparse
import collections
import glob
import re
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description="合并各测试项目的 cobertura 报告,输出行/分支覆盖率摘要")
parser.add_argument("root", nargs="?", default="tests", help="tests 根目录 (默认 tests)")
parser.add_argument("--min", type=float, default=None, help="合计行覆盖率门禁,低于该值退出码 1")
parser.add_argument("--min-branch", type=float, default=None, help="合计分支覆盖率门禁,低于该值退出码 1")
opts = parser.parse_args()
root_dir: str = opts.root
min_covered: float | None = opts.min
min_branch: float | None = opts.min_branch
files = glob.glob(f"{root_dir}/*/TestResults/*/coverage.cobertura.xml")
if not files:
    sys.exit(f"未找到覆盖率报告: {root_dir}/*/TestResults/*/coverage.cobertura.xml")


def normalize(asm: str, filename: str) -> str:
    """剥掉程序集目录之前的一切前缀,只留项目内相对路径。"""
    marker = asm + "/"
    return filename.split(marker, 1)[1] if marker in filename else filename


# 同一源码行会被多个测试项目的报告覆盖,取最大命中数即可(执行过即为 1)。
hits_by_line: dict[tuple[str, str, int], int] = {}
# 分支条件:condition-coverage 形如 "50% (1/2)"。同一行的多次观察取已覆盖
# 条件数的最大值(并集语义,与行覆盖的 max hits 一致);分母取对应观察的
# 总数(同一行编译产物固定,不同报告间本应一致,取 max 是防御)。
condition_re = re.compile(r"\((\d+)/(\d+)\)")
branch_by_line: dict[tuple[str, str, int], tuple[int, int]] = {}
for path in files:
    for package in ET.parse(path).getroot().iter("package"):
        asm = package.get("name")
        for cls in package.iter("class"):
            filename = normalize(asm, cls.get("filename") or "")
            # 分支行照常计入行覆盖：其 hits 就是整行命中数，含分支行的 100%
            # 口径更严，也是基线 15882 的统计口径。曾有的 `== "true"` 过滤
            # 是死代码——coverlet 实际写 branch="True"/"False"（Pascal 大小写）。
            for line in cls.iter("line"):
                key = (asm, filename, int(line.get("number")))
                hits_by_line[key] = max(hits_by_line.get(key, 0), int(line.get("hits")))
                if (line.get("branch") or "").lower() == "true":
                    m = condition_re.search(line.get("condition-coverage") or "")
                    if m:
                        covered, total = int(m.group(1)), int(m.group(2))
                        prev = branch_by_line.get(key)
                        if prev is None or (covered, total) > prev:
                            branch_by_line[key] = (covered, total)

per_asm = collections.defaultdict(lambda: [0, 0])
for (asm, _, _), hits in hits_by_line.items():
    per_asm[asm][1] += 1
    if hits > 0:
        per_asm[asm][0] += 1

branch_per_asm = collections.defaultdict(lambda: [0, 0])
for (asm, _, _), (covered, total) in branch_by_line.items():
    branch_per_asm[asm][1] += total
    branch_per_asm[asm][0] += covered

total_covered = total_lines = 0
total_branch_covered = total_branches = 0
header = f"{'程序集':<38}{'行覆盖':>16}{'分支覆盖':>16}"
print(header)
for asm in sorted(per_asm):
    covered, total = per_asm[asm]
    total_covered += covered
    total_lines += total
    row = f"{asm:<38}{100 * covered / total:>9.1f}% ({covered}/{total})"
    if asm in branch_per_asm:
        b_covered, b_total = branch_per_asm[asm]
        total_branch_covered += b_covered
        total_branches += b_total
        row += f"{100 * b_covered / b_total:>9.1f}% ({b_covered}/{b_total})"
    print(row)
total_pct = 100 * total_covered / total_lines
summary = f"{'合计':<38}{total_pct:>9.1f}% ({total_covered}/{total_lines})"
if total_branches:
    total_branch_pct = 100 * total_branch_covered / total_branches
    summary += f"{total_branch_pct:>9.1f}% ({total_branch_covered}/{total_branches})"
print(summary)

if min_covered is not None and total_pct < min_covered:
    sys.exit(f"行覆盖率门禁失败: 合计 {total_pct:.1f}% < 门禁 {min_covered}% (基线见 tests/TESTING.md)")
if min_branch is not None and total_branches and total_branch_pct < min_branch:
    sys.exit(f"分支覆盖率门禁失败: 合计 {total_branch_pct:.1f}% < 门禁 {min_branch}% (基线见 tests/TESTING.md)")
