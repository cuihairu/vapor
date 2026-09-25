#!/usr/bin/env python3
"""api.md 鉴权声明与代码鉴权调用一致性静态校验（零 .NET 依赖）。

背景：维护轮十七侦察实证——四个写操作端点（achievements unlock/reset、
trade-offers accept/decline）经共享 helper 委托鉴权，合并小节（enable /
disable 共用 Auth 行）与委托形态都是人眼对照容易漏的形态；而「漏挂鉴权」
恰是覆盖率门禁的盲区（没挂鉴权就没有 401 分支，覆盖率不会红）。本脚本把
Auth 声明 ↔ 代码鉴权调用的对照固化为机械守护，与 verify-api-docs.py
（端点存在性）同型而维度不同：那份守护文档同步，这份守护安全属性。

校验（62 端点三方对齐，2026-09-25 基线全绿）：
  1. 代码侧：每个 Map* 路由块内的 Auth.TryAdmin / Auth.TryAgent 调用
     （块边界 = 下一路由起点或顶格 static 成员定义——文件尾 helper 不属
     于任何路由）。
  2. 委托白名单 DELEGATES：块内无调用但调用委托 helper 的端点，helper
     体内的鉴权调用计入端点形态；helper 本身必须存在且体内含鉴权调用
     （防止「白名单挂名、helper 裸奔」）。
  3. 文档侧：api.md 每个端点小节（#### 标题）的 `- Auth:` 行，归一为
     none / admin / agent / both；合并标题（`A / B`）的 Auth 行归属小节
     内全部端点。
  4. 逐端点比对：admin ⟺ TryAdmin、agent ⟺ TryAgent、both ⟺ 双调用、
     none ⟺ 无调用；文档缺 Auth 行或 Auth 行无法归一（UNPARSED）都 FAIL。

新增端点的预期摩擦：忘写鉴权或忘写 Auth 行立即红——这是护栏不是障碍。

用法：python3 scripts/verify-api-auth.py [Program.cs] [api.md]
退出码：0 = ALL GREEN，1 = 有 FAIL（逐条打印）。
"""
import re
import sys

# 无鉴权调用的共享派发 helper：鉴权在 helper 体内，端点块只出现委托调用。
DELEGATES = ("DispatchAchievementWrite", "RunOfferDecisionAsync")


def classify(calls):
    if calls == {"TryAdmin", "TryAgent"}:
        return "both"
    if calls == {"TryAdmin"}:
        return "admin"
    if calls == {"TryAgent"}:
        return "agent"
    if not calls:
        return "none"
    return None


def doc_kind(raw):
    raw = raw.strip().lower().replace("*", "")
    if "admin or agent" in raw:
        return "both"
    if raw.startswith("agent"):
        return "agent"
    if raw.startswith("admin"):
        return "admin"
    if raw.startswith("none"):
        return "none"
    return None


def main():
    cs_path = sys.argv[1] if len(sys.argv) > 1 else "src/Vapor.ControlPlane/Program.cs"
    md_path = sys.argv[2] if len(sys.argv) > 2 else "docs/api.md"
    with open(cs_path, encoding="utf-8") as fh:
        cs = fh.read()
    with open(md_path, encoding="utf-8") as fh:
        md = fh.read()

    failures = []
    starts = [(m.start(), m.group(1).upper(), m.group(2))
              for m in re.finditer(r'\bMap(Get|Post|Put|Delete)\(\s*"([^"]*)"', cs)]
    if not starts:
        failures.append(f"{cs_path}: 未解析到任何 Map* 路由")

    helper_calls = {}
    for name in DELEGATES:
        dm = re.search(rf'\bstatic async Task<IResult> {name}\(.*?\n{{\n(.*?)(?=\nstatic |\Z)',
                       cs, re.S)
        if not dm:
            failures.append(f"委托 helper {name}: 未找到定义（白名单与代码脱节）")
        else:
            found = set(re.findall(r'\bAuth\.(TryAdmin|TryAgent)\b', dm.group(1)))
            if not found:
                failures.append(f"委托 helper {name}: 体内无任何鉴权调用（白名单挂名、helper 裸奔）")
            helper_calls[name] = found

    # 文档 Auth 归属：小节标题内的全部端点共享小节 Auth 行。
    doc_auth = {}
    for sec in re.split(r'^#### ', md, flags=re.M)[1:]:
        head = sec.split('\n', 1)[0]
        eps = re.findall(r'`(GET|POST|PUT|DELETE) ([^`\n]+)`', head)
        am = re.search(r'- Auth: \*{0,2}([^.\n]+)', sec)
        if not eps:
            continue
        if not am:
            for verb, path in eps:
                doc_auth[(verb, path)] = None
            continue
        kind = doc_kind(am.group(1))
        if kind is None:
            failures.append(f"{md_path}: Auth 行无法归一: '{am.group(1).strip()}' "
                            f"（小节 {' / '.join(v + ' ' + p for v, p in eps)}）")
            kind = "UNPARSED"
        for verb, path in eps:
            doc_auth[(verb, path)] = kind

    for i, (pos, verb, path) in enumerate(starts):
        end = starts[i + 1][0] if i + 1 < len(starts) else len(cs)
        body = cs[pos:end]
        sm = re.search(r'\nstatic ', body)  # 顶格 static 成员 = 路由区结束（文件尾 helper 不属于路由）
        if sm:
            body = body[:sm.start()]
        calls = set(re.findall(r'\bAuth\.(TryAdmin|TryAgent)\b', body))
        for name in DELEGATES:
            if f"{name}(" in body:
                calls |= helper_calls.get(name, set())
        actual = classify(calls)
        expected = doc_auth.get((verb, path))
        if actual is None:
            failures.append(f"代码侧形态未知: {verb} {path}（块内调用 {sorted(calls)}）")
        elif expected is None:
            failures.append(f"{md_path}: {verb} {path} 无 Auth 归属（新端点缺 Auth 行）")
        elif actual != expected:
            failures.append(f"{verb} {path}: 代码 {actual} != 文档 {expected}")

    if len(doc_auth) < len(starts):
        code_keys = {(v, p) for _, v, p in starts}
        for verb, path in sorted(code_keys - set(doc_auth)):
            failures.append(f"{md_path}: {verb} {path} 无 Auth 归属（不在任何带 Auth 行的小节）")

    print(f"路由 {len(starts)} 个，文档 Auth 归属 {len(doc_auth)} 个，"
          f"委托 helper {len(DELEGATES)} 个")
    if failures:
        for f in failures:
            print(f"FAIL: {f}")
        sys.exit(1)
    print("ALL GREEN")


if __name__ == "__main__":
    main()
