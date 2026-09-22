#!/usr/bin/env python3
"""合并各测试项目生成的 cobertura 报告,输出按程序集的行覆盖率摘要。

用法: ./scripts/coverage-summary.py [tests 根目录] [--min 百分比]

--min N:合计行覆盖率低于 N 时以退出码 1 失败(CI 门禁)。基线数字与
tests/TESTING.md 的统计同步演进。

注意:不同 testhost 生成的报告里 filename 前缀写法不一致(绝对路径、
"src/<项目>/..."、"<项目>/..."、裸文件名都出现过),必须按程序集名归一化,
否则同一行会被重复计入分母,覆盖率被系统性压低。
"""

import argparse
import collections
import glob
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description="合并各测试项目的 cobertura 报告,输出行覆盖率摘要")
parser.add_argument("root", nargs="?", default="tests", help="tests 根目录 (默认 tests)")
parser.add_argument("--min", type=float, default=None, help="合计行覆盖率门禁,低于该值退出码 1")
opts = parser.parse_args()
root_dir: str = opts.root
min_covered: float | None = opts.min
files = glob.glob(f"{root_dir}/*/TestResults/*/coverage.cobertura.xml")
if not files:
    sys.exit(f"未找到覆盖率报告: {root_dir}/*/TestResults/*/coverage.cobertura.xml")


def normalize(asm: str, filename: str) -> str:
    """剥掉程序集目录之前的一切前缀,只留项目内相对路径。"""
    marker = asm + "/"
    return filename.split(marker, 1)[1] if marker in filename else filename


# 同一源码行会被多个测试项目的报告覆盖,取最大命中数即可(执行过即为 1)。
hits_by_line: dict[tuple[str, str, int], int] = {}
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

per_asm = collections.defaultdict(lambda: [0, 0])
for (asm, _, _), hits in hits_by_line.items():
    per_asm[asm][1] += 1
    if hits > 0:
        per_asm[asm][0] += 1

total_covered = total_lines = 0
print(f"{'程序集':<38}{'行覆盖':>8}")
for asm in sorted(per_asm):
    covered, total = per_asm[asm]
    total_covered += covered
    total_lines += total
    print(f"{asm:<38}{100 * covered / total:>7.1f}%")
total_pct = 100 * total_covered / total_lines
print(f"{'合计':<38}{total_pct:>7.1f}%  ({total_covered}/{total_lines})")

if min_covered is not None and total_pct < min_covered:
    sys.exit(f"覆盖率门禁失败: 合计 {total_pct:.1f}% < 门禁 {min_covered}% (基线见 tests/TESTING.md)")
