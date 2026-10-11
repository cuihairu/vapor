#!/usr/bin/env python3
"""SSE 事件名规范化静态校验（零 .NET 依赖，与 verify-api-docs.py 同型）。

背景：40.8-③（计划书 §24）验收=「约定入文档，SSE 事件名与文档一致」。
文档半（Domain/Operational/Audit 三层分桶）随 ebbbb81 落地，但约定纯靠
自觉——新增 Publish 调用忘写规范表、或文档举例写了一个代码里不存在的名
字（ebbbbb81 初版就举了 `plugin.installed`，代码里从无此 SSE 名），任何
门禁都不会红。本脚本把「SSE 事件名 ⇄ 文档」同步固化为机械守护。

规范表的唯一权威：docs/domain-model.md「Event layering」节的规范表
（| `name` | Layer | published by |）——脚本从该表提取全集做双向比对：

  1. 代码侧 A：src/Vapor.ControlPlane 全部 `_events.Publish(…, "x.y", …)`
     直接字面量事件名。
  2. 代码侧 B：脚本内 DYNAMIC_PUBLISHERS 显式登记的变量转发发布点
     （failureEvent/eventType/type 三处——字面量在调用点或三元里，无法
     就地提取），登记值集合必须能在源码字面量里找到，且登记行号处仍是
     `_events.Publish` 调用（行号漂移即 FAIL，强迫维护人更新登记）。
  3. A ∪ B ⇄ 规范表精确名：双向集合相等（多=僵尸文档，少=漏登记）。
  4. 规范表 family 行（`session.<eventType>` / `auth.<challengeType>`，
     开放族不参与集合比对）只校验格式；Layer 只允许 Domain/Operational。
  5. 交叉文档：每个精确名须在 api.md §2 SSE 段（`Types published by the
     code:` 至 `- Session events` 之间）反引号出现；该段提取出的名不在
     规范表内的，只允许 AUDIT_ONLY 集合（写入审计、明确不进 SSE 的名）。
  6. 任一侧解析为零条目 → FAIL（文件结构变化时显式失败，不静默绿）。

用法：python3 scripts/verify-sse-event-names.py [srcdir] [domain-model.md] [api.md]
退出码：0 = ALL GREEN，1 = 有 FAIL（逐条打印）。
"""
import re
import sys
from pathlib import Path

# 变量转发发布点：file → (line, 说明, 该点可能发布的全部事件名)。
# 行号是守卫不是索引：源码行内容漂移即 FAIL，更新登记是显式动作。
DYNAMIC_PUBLISHERS = {
    "TaskSchedulerService.cs": (210, "failureEvent 参数转发", {
        "task.dispatch_failed", "task.enqueue_failed"}),
    "CrawlRunWorker.cs": (449, "eventType 三元转发", {
        "crawl.run_skipped", "crawl.run_completed"}),
    "RecurringJobScheduler.cs": (188, "Publish 助手 type 参数转发", {
        "job.scheduled_triggered", "job.scheduled_skipped", "job.scheduled_completed"}),
}

# api.md §2 出现、但只写审计日志不进 SSE 的名（散文 Note 明说），豁免集合比对。
AUDIT_ONLY = {"crawl.run_failed"}

PUBLISH_RE = re.compile(r"\.Publish\(")
LITERAL_RE = re.compile(r'"([a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+)"')
TABLE_ROW_RE = re.compile(r"^\|\s*`([a-zA-Z0-9_.<>]+)`\s*\|\s*(Domain|Operational)\s*\|", re.MULTILINE)
FAMILY_RE = re.compile(r"^[a-z][a-z0-9_]*\.<[a-zA-Z]+>$")
BACKTICK_RE = re.compile(r"`([a-z][a-z0-9_]*(?:\.[a-z0-9_<>]+)+)`")
SECTION_RE = re.compile(r"^##\s", re.MULTILINE)


def collect_code_names(srcdir: Path) -> set[str]:
    names: set[str] = set()
    for path in sorted(srcdir.rglob("*.cs")):
        lines = path.read_text(encoding="utf-8").splitlines()
        for lineno, line in enumerate(lines, start=1):
            if not PUBLISH_RE.search(line):
                continue
            names.update(LITERAL_RE.findall(line))
        guard = DYNAMIC_PUBLISHERS.get(path.name)
        if guard is not None:
            expected_line, why, values = guard
            if len(lines) < expected_line or not PUBLISH_RE.search(lines[expected_line - 1]):
                fail(f"{path.name}:{expected_line} 不再是 Publish 调用（{why}）——请更新 verify-sse-event-names.py 的 DYNAMIC_PUBLISHERS 登记")
            for value in values:
                all_text = "\n".join(lines)
                if f'"{value}"' not in all_text:
                    fail(f"{path.name} 登记值 {value} 在源码中已不存在——请更新 DYNAMIC_PUBLISHERS")
                names.add(value)
    return names


def parse_table(doc_path: Path) -> tuple[set[str], set[str]]:
    text = doc_path.read_text(encoding="utf-8")
    exact: set[str] = set()
    families: set[str] = set()
    for name, _layer in TABLE_ROW_RE.findall(text):
        if "<" in name:
            if not FAMILY_RE.match(name):
                fail(f"规范表 family 行格式非法: {name}")
            families.add(name)
        else:
            exact.add(name)
    return exact, families


def parse_api_section(api_path: Path) -> set[str]:
    text = api_path.read_text(encoding="utf-8")
    start = text.find("Types published by the code:")
    end = text.find("- Session events", start)
    if start < 0 or end < 0 or end <= start:
        fail("api.md §2 SSE 枚举段定位失败（锚点文本变化）")
    return set(BACKTICK_RE.findall(text[start:end]))


FAILS: list[str] = []


def fail(message: str) -> None:
    FAILS.append(message)


def main() -> int:
    srcdir = Path(sys.argv[1] if len(sys.argv) > 1 else "src/Vapor.ControlPlane")
    doc = Path(sys.argv[2] if len(sys.argv) > 2 else "docs/domain-model.md")
    api = Path(sys.argv[3] if len(sys.argv) > 3 else "docs/api.md")

    code = collect_code_names(srcdir)
    table, families = parse_table(doc)
    api_names = parse_api_section(api)

    if not code:
        fail("代码侧解析出 0 个事件名（结构变化，显式失败）")
    if not table:
        fail("domain-model.md 规范表解析出 0 行（结构变化，显式失败）")
    if not families:
        fail("规范表缺 family 行（session./auth. 开放族）")
    if not api_names:
        fail("api.md §2 枚举段解析出 0 个名字（结构变化，显式失败）")

    for name in sorted(table - code):
        fail(f"规范表有、代码无（僵尸文档）: {name}")
    for name in sorted(code - table):
        fail(f"代码有、规范表无（漏登记）: {name}")

    for name in sorted(table):
        if name not in api_names:
            fail(f"规范表名在 api.md §2 未出现: {name}")
    for name in sorted(api_names - table - AUDIT_ONLY):
        fail(f"api.md §2 枚举了规范表之外的名字: {name}（若只写审计请加入 AUDIT_ONLY）")

    for message in FAILS:
        print(f"FAIL: {message}")
    if FAILS:
        print(f"verify-sse-event-names: {len(FAILS)} FAIL")
        return 1

    print(f"verify-sse-event-names: ALL GREEN（精确名 {len(table)}，family {len(families)}，代码侧 {len(code)}，api.md §2 {len(api_names)}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
