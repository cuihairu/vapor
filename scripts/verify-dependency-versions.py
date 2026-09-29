#!/usr/bin/env python3
"""依赖版本编队一致性机械校验:docs/dependencies.md 版本表 ⇄ 全部 .csproj。

todo.md §36 收官点名的后续可迭代项「NuGetAudit 依赖升级策略」需要一个不会随
时间腐化的载体:仓库有 25 个 csproj、19 个 NuGet 包、且没有 Central Package
Management,每个项目手写版本号——手写就会漂。实证漂移是真实存在的:
Vapor.ControlPlane.Tests 整个项目生命期停在 xunit 2.6.2 / Test.Sdk 17.8.0 /
runner 2.5.4,其余 11 个测试项目早已在 2.9.3 / 17.14.1 / 2.8.2,`git log -S`
证明它从未被对齐过,而**没有任何门禁会红**——测试照样全绿,覆盖率照样 100%。
覆盖率门禁的盲区在这里和漏挂鉴权是同一类:坏的不是代码,是被门禁看不见的一致性。

本脚本把 docs/dependencies.md 的版本表锚点围栏当作唯一权威源(与
TESTING.md 之于 verify-coverage-inventory.py 同一哲学:文档是权威源,
脚本是机械守护),六向比对,任一违规即退出码 1:
  1. 登记闭合:全部 csproj 的每个 PackageReference 必须在册(新依赖漏登记即红)
  2. 编队完整:Scope=test-fleet 的包,每个测试项目必须声明
  3. 版本一致:在册包的版本号与 csproj 逐一对齐(任意项目声明即须同版本)
  4. 资产不外泄:xunit.runner.visualstudio 必须带 PrivateAssets=all
     (漏了会让适配器与 build 资产流向生产依赖图)
  5. 审计姿态未松:Directory.Build.props 的 NuGetAuditMode=all 与
     TreatWarningsAsErrors 仍在(整个「零已知漏洞」承诺的唯一支点;
     有人改成 direct 或改回 warning,本页的 §1 就变成谎言而构建照绿)
  6. 测试项目自认:每个 *.Tests 项目必须显式声明 IsTestProject=true——
     **本脚本第一版的第六个洞**:只按 IsTestProject 属性识别测试项目时,
     三个插件测试项目(它们是本仓最后几轮新建的)没写这个属性,于是被整族
     跳过第 2 向的编队校验,却照样跑在覆盖率门禁里。识别口径因此取并集
     (属性或 *.Tests 文件名),并把「是测试项目却不自认」本身列为违规。

无 .NET 依赖,纯静态解析。CI(format job)以默认参数跑。

用法:python3 scripts/verify-dependency-versions.py [deps.md] [Directory.Build.props]
"""

import argparse
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description="依赖版本编队一致性校验")
parser.add_argument("deps_md", nargs="?", default="docs/dependencies.md")
parser.add_argument("props", nargs="?", default="Directory.Build.props")
opts = parser.parse_args()

failures: list[str] = []


def add(why: str, extra: list[str]) -> None:
    for item in extra:
        failures.append(f"{why}: {item}")


# ---------- 权威源:docs/dependencies.md 锚点围栏内的版本表 ----------

if not os.path.exists(opts.deps_md):
    sys.exit(f"FAIL: 找不到 {opts.deps_md}")
md = open(opts.deps_md, encoding="utf-8").read()

m = re.search(
    r"<!-- verify-dependency-versions:packages -->\n(.*?)<!-- /verify-dependency-versions:packages -->",
    md, re.S)
if not m:
    sys.exit(f"FAIL: {opts.deps_md} 缺少 verify-dependency-versions:packages 锚点围栏"
             f"(不得删围栏绕过校验)")

# 表行: | `Name` | 1.2.3 | scope | note |
fleet: dict[str, tuple[str, str]] = {}
for line in m.group(1).splitlines():
    cells = [c.strip() for c in line.strip().strip("|").split("|")]
    if len(cells) < 3:
        continue
    name = cells[0].strip("`").strip()
    if not name or name in {"Package", "---"} or set(name) <= {"-"}:
        continue
    fleet[name] = (cells[1].strip("`").strip(), cells[2].strip().lower())

if not fleet:
    sys.exit(f"FAIL: {opts.deps_md} 版本表为空")

# Scope 取值白名单:写错一个 scope 名会静默变成「谁都不强制」的 any。
bad_scope = sorted(f"{n}={s}" for n, (v, s) in fleet.items() if s not in {"test-fleet", "any"})
if bad_scope:
    sys.exit(f"FAIL: {opts.deps_md} 版本表 Scope 取值非法(只允许 test-fleet / any): {bad_scope}")

# 不用 floating 版本——`Version="*"` 会让整张表失去意义。
floating = sorted(n for n, (v, _s) in fleet.items() if v in {"", "*"})
if floating:
    sys.exit(f"FAIL: {opts.deps_md} 版本表含空/浮动版本号: {floating}")

TEST_FLEET_REQUIRED = {n for n, (_v, s) in fleet.items() if s == "test-fleet"}
# runner 会拖入整套 Microsoft.TestPlatform.* 与 build assets,漏 PrivateAssets 会
# 让适配器与 build 资产流向消费方。只守这一个包:coverlet.collector 同样只影响
# 测试进程,但测试项目一律 IsPackable=false 且无任何项目引用它们(已 grep 实证),
# 给它加 PrivateAssets 是零收益改动、纯 churn——不立无后果的规矩。
PRIVATE_ASSETS_REQUIRED = {"xunit.runner.visualstudio"}


# ---------- csproj 侧解析 ----------

def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


# 文件名口径:12 个测试项目全部以 .Tests 结尾,而 tests/ 目录下的
# TestPlugin / TestFixtures 是被测支撑程序集——只有文件名前缀能把两者分开。
TEST_PROJECT_SUFFIX = ".Tests.csproj"


def parse_project(path: str) -> tuple[bool, bool, dict[str, str]]:
    """返回 (IsTestProject, 文件名像测试项目, {包名: 版本})。用了 XML 解析
    而非正则,这样条件引用或注释里的字样不会混进来。"""
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        sys.exit(f"FAIL: {path} XML 解析失败: {exc}")
    is_test = any((local(p.tag) == "IsTestProject")
                  and (p.text or "").strip().lower() == "true"
                  for p in root.iter())
    by_name = os.path.basename(path).endswith(TEST_PROJECT_SUFFIX)
    packages: dict[str, str] = {}
    for ref in root.iter():
        if local(ref.tag) != "PackageReference":
            continue
        name = (ref.get("Include") or "").strip()
        if not name:
            sys.exit(f"FAIL: {path} 存在无 Include 的 PackageReference")
        version = (ref.get("Version") or "").strip()
        if version:
            packages[name] = version
    return is_test, by_name, packages


projects: dict[str, dict[str, str]] = {}
test_projects: set[str] = set()
unmarked: list[str] = []
for base in ("src", "tools", "tests"):
    for path in sorted(glob.glob(f"{base}/**/*.csproj", recursive=True)):
        if f"{os.sep}obj{os.sep}" in path or f"{os.sep}bin{os.sep}" in path:
            continue
        rel = path.replace(os.sep, "/")
        is_test, by_name, packages = parse_project(path)
        projects[rel] = packages
        # 并集口径:只信 IsTestProject 属性会整族漏掉没写该属性的项目(本轮
        # 实证三个插件测试项目就是),而漏掉意味着它们不在编队校验范围内。
        if is_test or by_name:
            test_projects.add(rel)
        if by_name and not is_test:
            unmarked.append(rel)

if not projects:
    sys.exit("FAIL: src/tools/tests 下未解析出任何 .csproj")
if not test_projects:
    sys.exit("FAIL: 未解析出任何测试项目")


# ---------- 第 1 向:登记闭合 ----------

unregistered = sorted(f"{p} :: {name}"
                      for p, packages in projects.items()
                      for name in packages
                      if name not in fleet)
add("依赖未在 docs/dependencies.md 版本表登记(新依赖漏登记)", unregistered)
unused = sorted(name for name in fleet if not any(name in pk for pk in projects.values()))
add("版本表在册但无任何项目引用(僵尸登记)", unused)


# ---------- 第 2/3 向:编队完整 + 版本一致 ----------

missing = sorted(f"{p} 缺 {name}"
                 for p in test_projects
                 for name in sorted(TEST_FLEET_REQUIRED)
                 if name not in projects[p])
add("test-fleet 包在测试项目缺失", missing)

drifted = sorted(f"{p} :: {name} {projects[p][name]}(应 {fleet[name][0]})"
                 for p, packages in projects.items()
                 for name, version in packages.items()
                 if name in fleet and version != fleet[name][0])
add("版本与文档表不一致", drifted)


# ---------- 第 4 向:PrivateAssets 不外泄 ----------

for path in sorted(glob.glob("**/*.csproj", recursive=True)):
    if f"{os.sep}obj{os.sep}" in path or f"{os.sep}bin{os.sep}" in path:
        continue
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError:
        continue  # 已在 parse_project 里报过
    for ref in root.iter():
        if local(ref.tag) != "PackageReference":
            continue
        name = (ref.get("Include") or "").strip()
        if name not in PRIVATE_ASSETS_REQUIRED:
            continue
        meta = [local(c.tag) for c in ref]
        if "PrivateAssets" not in meta:
            add("测试工具包缺 PrivateAssets(依赖会外泄到生产依赖图)",
                [f"{path.replace(os.sep, '/')} :: {name}"])
        elif (ref.find("{*}PrivateAssets") is None
              or (ref.find("{*}PrivateAssets").text or "").strip() != "all"):
            add("PrivateAssets 非 all", [f"{path.replace(os.sep, '/')} :: {name}"])


# ---------- 第 5 向:审计姿态未松 ----------

if not os.path.exists(opts.props):
    sys.exit(f"FAIL: 找不到 {opts.props}")
props = open(opts.props, encoding="utf-8").read()


def prop_value(name: str) -> str | None:
    mm = re.search(rf"<{name}>([^<]*)</{name}>", props)
    return mm.group(1).strip() if mm else None


if (prop_value("NuGetAuditMode") or "").lower() != "all":
    add("NuGetAuditMode 不是 all(直依赖审计=传递依赖漏洞静默通过,docs §1 承诺失效)",
        [f"{opts.props} NuGetAuditMode={prop_value('NuGetAuditMode')!r}"])
if (prop_value("TreatWarningsAsErrors") or "").lower() != "true":
    add("TreatWarningsAsErrors 未开启(NU19xx 审计告警降回 warning,漏洞不再阻断构建)",
        [f"{opts.props} TreatWarningsAsErrors={prop_value('TreatWarningsAsErrors')!r}"])


# ---------- 第 6 向:测试项目自认 ----------

add("*.Tests 项目缺 <IsTestProject>true</IsTestProject>"
    "(既逃出编队校验,也拿不到 Directory.Build.props 的覆盖率默认设置)",
    sorted(unmarked))


# ---------- 判读 ----------

if failures:
    print(f"FAIL: 依赖版本编队一致性校验 {len(failures)} 处不一致")
    for item in failures:
        print(f"  - {item}")
    sys.exit(1)
print(f"OK: {len(projects)} 个 csproj({len(test_projects)} 个测试项目)、"
      f"{len(fleet)} 个在册依赖(其中 {len(TEST_FLEET_REQUIRED)} 个 test-fleet)、"
      f"审计姿态 NuGetAuditMode=all + TreatWarningsAsErrors 全部一致")
