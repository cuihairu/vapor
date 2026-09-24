#!/usr/bin/env python3
"""TESTING.md 计数一致性静态校验（零 TRX 依赖）。

背景：类计数同步已三度遗漏（§33 漏动作表成就行、维护轮一漏 Steam.Core stale
计数、内部状态轮漏整块 +45），重构轮曾用一次性断言脚本兜底但未入库。本脚本
把那次断言固化为可重复执行的机械守护。

校验四层（全部为互相推导，不跑测试）：
  1. 附录（权威源）：每小节标题数字 == 该表类计数之和；小节内类名不重复。
  2. 统计表：每项目行计数 == 附录同项目小节和；合计行 == 各项目行之和。
  3. 目录树：每项目 (N tests) == 附录同项目和。
  4. 明细区：有表格的章节——标题数字 == 表和 == 统计表对应项目，类集合与
     附录逐项目全等（合并行 "A + B" 拆开、"(性能)" 后缀剥除）；prose 章节与
     插件体系父标题只校验标题数字 == 对应统计行之和。

用法：python3 scripts/verify-testing-docs.py [tests/TESTING.md]
退出码：0 = ALL GREEN，1 = 有 FAIL（逐条打印）。
"""
import re
import sys

# 明细区章节标题 → 统计表/附录项目名映射；None 表示 prose 章节（无表格可查）。
DETAIL_MAP = {
    "Steam.Core": ["Vapor.Steam.Core.Tests"],
    "ControlPlane": ["Vapor.ControlPlane.Tests"],
    "插件体系": [
        "Vapor.Plugins.Core.Tests",
        "Vapor.Plugins.MobileAuthenticator.Tests",
        "Vapor.Plugins.MarketWatch.Tests",
        "Vapor.Plugins.Monitoring.Tests",
    ],
    "Agent": ["Vapor.Agent.Tests"],
    "E2E": ["Vapor.E2E.Tests"],
    "Vapor.Protocol.Tests": ["Vapor.Protocol.Tests"],
    "Vapor.KeyRotation.Tests": ["Vapor.KeyRotation.Tests"],
}

PROSE_SECTIONS = {"插件体系", "Agent", "E2E"}


def fail_if(bad, messages, message):
    if bad:
        messages.append(message)
    return not bad


def parse_annex(text):
    """附录小节：{项目名: (标题数字, {类名: 计数})}。"""
    annex = {}
    pattern = re.compile(r"^### (Vapor\.[\w.]+Tests)（(\d+) 个测试）\s*$", re.M)
    matches = list(pattern.finditer(text))
    for i, m in enumerate(matches):
        name, declared = m.group(1), int(m.group(2))
        zone = text[m.end(): matches[i + 1].start() if i + 1 < len(matches) else len(text)]
        rows = re.findall(r"^\| (\S+) \| (\d+) \|$", zone, re.M)
        counts = {}
        for cls, n in rows:
            if cls in counts:
                raise SystemExit(f"脚本错误：附录 {name} 内类 {cls} 重复出现")
            counts[cls] = int(n)
        annex[name] = (declared, counts)
    return annex


def parse_stats(text):
    """统计表：({项目名: 计数}, 合计或 None)。"""
    zone = text.split("## 测试统计", 1)[1].split("## ", 2)[0] if "## 测试统计" in text else ""
    stats, total = {}, None
    for m in re.finditer(r"^\| (Vapor\.[\w.]+Tests|\*\*合计\*\*) \| \*{0,2}(\d+)\*{0,2} \|", zone, re.M):
        name, n = m.group(1), int(m.group(2))
        if name == "**合计**":
            total = n
        else:
            stats[name] = n
    return stats, total


def parse_tree(text):
    """目录树：{项目名: 计数}。"""
    zone = text.split("## 测试项目结构", 1)[1].split("## ", 1)[0]
    return {m.group(1): int(m.group(2))
            for m in re.finditer(r"^\s*[├└]+─+ (Vapor\.[\w.]+Tests)/\s+\((\d+) tests[),]", zone, re.M)}


def parse_detail(text):
    """明细区：{章节名: (标题数字, [(类名集合, 行计数)] 或 None)}。

    一行可合并多个类（"A + B" / "A / B / C"），行计数为这些类的合计——
    比对单位是行：组计数 == 附录同组类计数之和（合计无法逐类拆分）。
    """
    body = text.split("## 测试分类", 1)[1].split("## 运行测试", 1)[0]
    headers = list(re.finditer(r"^### (.+?)\((\d+) 个测试\)\s*$", body, re.M))
    details = {}
    for i, m in enumerate(headers):
        section, declared = m.group(1), int(m.group(2))
        zone = body[m.end(): headers[i + 1].start() if i + 1 < len(headers) else len(body)]
        rows = None
        if section not in PROSE_SECTIONS:
            rows = []
            for row in re.finditer(r"^\| (.+?) \| (\d+) \| .+ \|$", zone, re.M):
                n = int(row.group(2))
                # 剥类名上的说明后缀（"(性能)"、"(集成,门控)" 等）再拆分隔符。
                names = {re.sub(r"[（(].*$", "", part.strip())
                         for part in re.split(r"[+/]", row.group(1))}
                rows.append((names, n))
        details[section] = (declared, rows)
    return details


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "tests/TESTING.md"
    with open(path, encoding="utf-8") as fh:
        text = fh.read()

    annex = parse_annex(text)
    stats, total = parse_stats(text)
    tree = parse_tree(text)
    details = parse_detail(text)
    failures = []

    # 1. 附录自洽 + 求合计
    annex_sums = {name: sum(c.values()) for name, (_, c) in annex.items()}
    for name, (declared, counts) in annex.items():
        fail_if(declared != annex_sums[name], failures,
                f"附录 {name}: 标题 {declared} != 类计数和 {annex_sums[name]}")
        fail_if(not counts, failures, f"附录 {name}: 未解析到任何类行")

    # 2. 统计表对照附录
    for name, n in stats.items():
        expected = annex_sums.get(name)
        if expected is None:
            failures.append(f"统计表 {name}: 附录无此项目小节")
        elif n != expected:
            failures.append(f"统计表 {name}: {n} != 附录和 {expected}")
    if total is not None and total != sum(stats.values()):
        failures.append(f"统计表合计: {total} != 各项目行之和 {sum(stats.values())}")

    # 3. 目录树对照附录
    for name, n in tree.items():
        expected = annex_sums.get(name)
        if expected is None:
            failures.append(f"目录树 {name}: 附录无此项目小节")
        elif n != expected:
            failures.append(f"目录树 {name}: {n} != 附录和 {expected}")
    missing_tree = set(stats) - set(tree)
    fail_if(missing_tree, failures, f"目录树缺项目: {sorted(missing_tree)}")

    # 4. 明细区对照统计表与附录
    for section, declared_rows in details.items():
        declared, rows = declared_rows
        projects = DETAIL_MAP.get(section)
        if projects is None:
            failures.append(f"明细区章节 {section}: 无映射（请登记进 DETAIL_MAP）")
            continue
        stat_sum = sum(stats.get(p, 0) for p in projects)
        fail_if(declared != stat_sum, failures,
                f"明细区 {section}: 标题 {declared} != 统计表对应项目和 {stat_sum}")
        if rows is None:
            continue
        annex_classes = {}
        for p in projects:
            annex_classes.update(annex.get(p, (0, {}))[1])
        detail_sum = sum(n for _, n in rows)
        fail_if(detail_sum != stat_sum, failures,
                f"明细区 {section}: 表和 {detail_sum} != 统计表对应项目和 {stat_sum}")
        # 逐组比对 + 类不重叠（同一类出现两行会虚增表和）。
        seen = set()
        for names, n in rows:
            overlap = names & seen
            fail_if(overlap, failures, f"明细区 {section}: 类 {sorted(overlap)} 出现在多个合并行")
            seen |= names
            unknown = names - set(annex_classes)
            fail_if(unknown, failures, f"明细区 {section} 多出的类: {sorted(unknown)}")
            group_sum = sum(annex_classes.get(c, 0) for c in names)
            fail_if(n != group_sum, failures,
                    f"明细区 {section}: 行 {' / '.join(sorted(names))} 计数 {n} != 附录同组之和 {group_sum}")
        only_annex = set(annex_classes) - seen
        fail_if(only_annex, failures, f"明细区 {section} 缺少的类: {sorted(only_annex)}")

    missing_detail = set(DETAIL_MAP) - set(details)
    fail_if(missing_detail, failures, f"明细区缺章节: {sorted(missing_detail)}")

    print(f"附录小节 {len(annex)} 个（合计 {sum(annex_sums.values())}），"
          f"统计表 {len(stats)} 项目（合计 {total}），目录树 {len(tree)} 项目，"
          f"明细区 {len(details)} 章节")
    if failures:
        for f in failures:
            print(f"FAIL: {f}")
        sys.exit(1)
    print("ALL GREEN")


if __name__ == "__main__":
    main()
