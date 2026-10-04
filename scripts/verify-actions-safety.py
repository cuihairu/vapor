#!/usr/bin/env python3
"""actions.md 执行安全分类三元一致性静态校验（零 .NET 依赖）。

背景（轮五十，P0-A 执行语义）：57 个 in-tree 动作各自声明 ActionSafety
（ReadOnly/Idempotent/GuardedWrite/NonIdempotent），调度器按分类约束
派发重试上限——不安全类 cap 至 2、安全类用配置上限、未分类（第三方
插件动作）按 Unknown 保守处理。分类的单源在 agent 侧源码注解，但
控制平面镜像表与 actions.md 目录各自手抄——三处漂移没有任何门禁会红
（镜像表漏一个动作，调度器对它静默按 Unknown 收紧；目录写错一列，
operator 看到的是错的分类）。本脚本把三元一致固化为机械守护，与
verify-api-docs.py（端点清单）同型。

校验（三集合两两逐值比对，动作名不区分大小写对齐注册表语义）：
  1. 源码侧：src/ 全部 `Safety = ActionSafety.X` 注解 → (name, X) 集合
     （X 取该注解前最近的 `public string Name => "n";`）。
  2. 控制平面侧：ActionSemantics.cs 镜像表 `["n"] = ActionSafety.X,`。
  3. 文档侧：actions.md 动作头部 `### \\`n\\` … (…, safety: X)`。
  4. 任一侧解析为空 → FAIL（文件结构变化时显式失败，不静默绿）。
  5. 集合不等（一侧多/少动作）或同动作分类不一致 → FAIL。
  6. 镜像表出现 Unknown → FAIL（in-tree 动作必须显式分类；Unknown 是
     第三方插件动作的保守缺省，不允许进入镜像表）。

负探针（人为改一处 → EXIT=1）：任一侧单独改分类、或删一个动作的
注解/表项/目录列，脚本即红。
"""

import re
import sys
import pathlib

SRC_ROOT = pathlib.Path("src")
CP_TABLE = SRC_ROOT / "Vapor.ControlPlane" / "ActionSemantics.cs"
CATALOG = pathlib.Path("docs") / "actions.md"

NAME_RE = re.compile(r'public string Name => "([a-z0-9_]+)";')
SAFETY_RE = re.compile(r'Safety = ActionSafety\.(\w+)')
CP_ENTRY_RE = re.compile(r'\["([a-z0-9_]+)"\] = ActionSafety\.(\w+),')
HEADER_RE = re.compile(
    r'^### `([a-z0-9_]+)`(?: — .+)? '
    r'\(login: (?:yes|no), timeout: \d+s, safety: (\w+)\)$'
)
VALID_CLASSES = {"ReadOnly", "Idempotent", "GuardedWrite", "NonIdempotent"}


def parse_source_annotations():
    found = {}
    for f in sorted(SRC_ROOT.rglob("*.cs")):
        text = f.read_text(encoding="utf-8")
        if "Safety = ActionSafety" not in text:
            continue
        for m in SAFETY_RE.finditer(text):
            names = list(NAME_RE.finditer(text[: m.start()]))
            if not names:
                raise SystemExit(f"FAIL: {f}: Safety 注解前无 Name 声明")
            name, cls = names[-1].group(1), m.group(1)
            if cls not in VALID_CLASSES:
                raise SystemExit(f"FAIL: {f}: {name} 分类 {cls} 不在合法集合")
            if found.setdefault(name, (cls, str(f)))[0] != cls:
                raise SystemExit(
                    f"FAIL: 源码动作 {name} 分类冲突: "
                    f"{found[name]} vs {(cls, str(f))}"
                )
    return found


def parse_cp_table():
    text = CP_TABLE.read_text(encoding="utf-8")
    found = {}
    for name, cls in CP_ENTRY_RE.findall(text):
        if found.setdefault(name, cls) != cls:
            raise SystemExit(f"FAIL: 镜像表动作 {name} 重复且分类冲突")
    return found


def parse_catalog():
    found = {}
    for i, line in enumerate(CATALOG.read_text(encoding="utf-8").splitlines()):
        m = HEADER_RE.match(line)
        if not m:
            continue
        name, cls = m.group(1), m.group(2)
        if cls not in VALID_CLASSES:
            raise SystemExit(f"FAIL: {CATALOG}:{i + 1}: 分类 {cls} 不在合法集合")
        if found.setdefault(name, cls) != cls:
            raise SystemExit(f"FAIL: {CATALOG}: 动作 {name} 头部重复且分类冲突")
    return found


def main():
    src = parse_source_annotations()
    cp = parse_cp_table()
    doc = parse_catalog()

    print(f"源码注解 {len(src)} 个，镜像表 {len(cp)} 个，目录 safety 列 {len(doc)} 个")
    failures = []
    if not src:
        failures.append("源码注解解析为 0 条（结构变化显式失败）")
    if not cp:
        failures.append(f"{CP_TABLE} 表解析为 0 条（结构变化显式失败）")
    if not doc:
        failures.append(f"{CATALOG} safety 列解析为 0 条（结构变化显式失败）")
    if not failures:
        for name in sorted(set(src) | set(cp) | set(doc),
                           key=str.lower):
            s, c, d = (side.get(name) for side in (src, cp, doc))
            if s is None or c is None or d is None:
                missing = [label for label, v in
                           (("源码", s), ("镜像表", c), ("目录", d)) if v is None]
                failures.append(f"{name}: 缺 {'/'.join(missing)}")
            elif not (s[0] == c == d):
                failures.append(f"{name}: 源码 {s[0]} != 镜像表 {c} != 目录 {d}")
        unknown_cp = [n for n, c in cp.items() if c == "Unknown"]
        failures += [f"镜像表 {n} 为 Unknown（in-tree 动作必须显式分类）"
                     for n in unknown_cp]

    if failures:
        for f in failures:
            print(f"FAIL: {f}")
        sys.exit(1)
    print("ALL GREEN")


if __name__ == "__main__":
    main()
