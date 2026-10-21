简体中文 | English

# 依赖升级策略

Vapor 交付一个无头自动化运行时：经 SteamKit2 对接 Steam、所有数据存进 SQLite，并以 alpha 项目而言罕见的程度做了加固：构建把每一条分析器告警和每一条 NuGet 审计建议都当错误对待，CI 拒绝合并行与分支覆盖率跌破 100% 的提交。依赖是这种严格性可能悄悄腐烂的唯一输入，因此本页把规则写明白，并用机械手段钉住。

本页是仓库中每个 `PackageReference` 版本的**单一事实源**。`scripts/verify-dependency-versions.py` 从本文件解析出下表，若有 `.csproj` 与之不一致、有包未在此登记就被使用、或审计配置被放松，则让构建失败。改这张表要与改项目同步进行；脚本是裁判，而不是反过来。

## 1. 审计姿态

`Directory.Build.props` 设置：

```xml
<NuGetAuditMode>all</NuGetAuditMode>   <!-- direct *and* transitive -->
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

`NuGetAuditMode=all` 把审计从直接引用拓宽到整个传递闭包，`TreatWarningsAsErrors` 则把由此产生的 `NU1901`–`NU1904` 建议从告警升级为构建失败。两者合起来意味着已知有漏洞的包根本无法构建，更谈不上发布——不存在留待以后消化的「审计告警积压」，因为它压根编译不过。

基线是每个项目直接与传递依赖均为零建议：

```bash
for p in $(find src tools tests -name '*.csproj' -not -path '*/obj/*'); do
  dotnet list "$p" package --vulnerable --include-transitive | tail -1
done
```

要逐项目运行，别用 `dotnet list Vapor.sln package`：解决方案级形式在此解决方案布局下会因项目集合冲突而中止（两个项目解析出相同的全局属性），因此上面的逐项目循环才是受支持的写法。

同一姿态的另一半是 `dependency-review` workflow（`.github/workflows/dependency-review.yml`），它在每个 pull request 上运行 `dependency-review-action`，当 PR *引入*有漏洞或 GPL 授权的依赖时失败。NuGet 审计管的是树里已有什么；dependency review 管的是最初允许什么进树。

两道守卫都是配置而非口头惯例：§5 的脚本在每次 push 时重申 `NuGetAuditMode` 与 `TreatWarningsAsErrors`，因此悄悄把 `NuGetAuditMode` 改回 `direct`（或让告警退回告警）会让 CI 失败，而不是悄然削弱本节。

## 2. 版本舰队

`src/`、`tools/` 与 `tests/` 中的每个 `PackageReference` 都登记在此。`Scope` 取值之一：

| Scope | 含义 |
|-------|---------|
| `test-fleet` | 每个测试项目**必须**声明它，且精确为此版本 |
| `any` | 声明它的项目**必须**精确使用此版本（是否声明本身是可选的） |

这里的*测试项目*指 `*.Tests` 项目，全部 15 个也都设置了 `<IsTestProject>true</IsTestProject>`——脚本同样检查该属性，因为 `Directory.Build.props` 正是以它为键来套用覆盖率默认值。早前轮次有三个插件测试项目建立时没带它；由于该属性也是*识别*测试项目的天然手段，这个缺口本会让它们悄然豁免于下面的舰队检查，因此脚本按任一信号即认作测试项目，两信号不一致时失败。

`test-fleet` 之所以存在，是因为测试工具链必须在全部 15 个测试项目里行为一致：只要有一个项目还留在旧 runner 上，你就会得到一个本地通过却什么也不上报的测试，或一个悄悄变形的覆盖率分母。典型案例：`Vapor.ControlPlane.Tests` 在项目的整个生命周期里停在 xunit 2.6.2 / test SDK 17.8.0 / runner 2.5.4，而其余 11 个停在 2.9.3 / 17.14.1 / 2.8.2，却没有一条红的。

<!-- verify-dependency-versions:packages -->

| 包 | 版本 | Scope | 备注 |
|---------|---------|-------|-------|
| `xunit` | 2.9.3 | test-fleet | 测试框架。2.6.2 → 2.9.3 收紧了断言诊断；让舰队保持在一条 minor 线上。 |
| `xunit.runner.visualstudio` | 2.8.2 | test-fleet | VSTest 适配器。必须带 `<PrivateAssets>all</PrivateAssets>`（机械检查）——它会拖入整套 `Microsoft.TestPlatform.*` 外加构建资产。 |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | test-fleet | 测试宿主 + 发现。驱动 `--blame-hang`，覆盖率作业依赖它。 |
| `coverlet.collector` | 6.0.4 | test-fleet | 覆盖率收集器。此处刻意*不*要求 `PrivateAssets`：测试项目 `IsPackable=false` 且无任何项目引用它们，不存在可供它泄漏进去的依赖图。 |
| `FsCheck.Xunit` | 2.16.6 | any | 基于性质的测试。15 个测试项目里只有 5 个在用。 |
| `Moq` | 4.21.0 | any | Mock 框架。只有 4 个测试项目在用。 |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | any | 控制平面测试用的 `WebApplicationFactory` 宿主。 |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.12 | any | 同时也是 `src/` 包；全树一个版本。 |
| `Microsoft.Extensions.Logging.Console` | 10.0.12 | any | `src/Vapor.Agent`、`src/Vapor.Steam.Core`。 |
| `Microsoft.Extensions.DependencyInjection` | 10.0.12 | any | `src/Vapor.Agent`。 |
| `Microsoft.Data.Sqlite` | 10.0.12 | any | 存储层。其 `SqliteJobStore.Dispose` 收尾是已知 CI 抖动点——见 §4。 |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.5 | any | 原生 SQLite bundle；版本线独立于 .NET 那条。 |
| `Cronos` | 0.13.0 | any | 为周期任务解析 cron。 |
| `Swashbuckle.AspNetCore` | 10.3.0 | any | OpenAPI/Swagger，经 `Vapor_ENABLE_SWAGGER` 选择启用。 |
| `StackExchange.Redis` | 3.4.0 | any | 可选缓存后端。 |
| `SteamKit2` | 3.4.0 | any | Steam 协议客户端。此处的升级是 Steam 行为变更而非库卫生——见 §4。 |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.19.1 | any | OTLP 导出器。 |
| `OpenTelemetry.Extensions.Hosting` | 1.19.1 | any | OTel 宿主集成。 |
| `OpenTelemetry.Instrumentation.AspNetCore` | 1.19.0 | any | OTel ASP.NET Core instrumentation。上游独立编版本——1.19.0 与 1.19.1 之差是上游的拆版，不是本地漂移。 |

<!-- /verify-dependency-versions:packages -->

## 3. 哪些已自动化

`.github/dependabot.yml` 对仓库根按 `nuget` 生态每周运行，一次至多开 10 个 pull request，并**忽略所有 `version-update:semver-major`**：

```yaml
updates:
  - package-ecosystem: "nuget"
    directory: "/"
    schedule:
      interval: "weekly"
    open-pull-requests-limit: 10
    ignore:
      - dependency-name: "*"
        update-types: ["version-update:semver-major"]
```

于是补丁与 minor 升级以普通 pull request 到达、由人审阅、并走常规门禁——零告警的 Release 构建、串行覆盖率轮次、以及双 100% 覆盖率门禁。Dependabot 升级在任何地方都没有特殊通道：与任何其他变更一视同仁，这正是要点。

有三样东西刻意**不**自动化，因为机器人判不了：

- **GitHub Actions 版本**——每个 action 都按 SHA 钉定（见 `docs/releasing.md` 约定与 workflow 内的 pin 注释）。升一个 SHA 是供应链决策，不是版本号升级。
- **Docker 基础镜像与 .NET SDK 版本**——它们决定已发布镜像的运行时下限，只在深思熟虑后变更。
- **semver-major 依赖升级**——即上面的全量 `ignore`。major 升级是行为变更，需要 §4 的流程，不是「一个绿 PR 形状」的变更。

## 4. 大版本升级（人工路径）

semver-major 升级，或任何对爆炸半径很大的依赖的改动，都按这份清单手工执行：

1. 在同一个 commit 里改 §2 表中的行**以及**声明该包的每个 `.csproj`。脚本对只改一半的升级会失败——那是刻意的摩擦，不是 bug。
2. `dotnet build Vapor.sln -c Release --no-incremental`，并读输出。预期分析器与可空告警会动：新 major 常收紧可空注解或新增分析器规则，配合 `AnalysisLevel=latest-all` 与 `TreatWarningsAsErrors`，它们会以构建错误浮出水面。把每一条当真实工作而非噪音——上一次 Moq 4.20.72 → 4.21.0 的 minor 升级落进 `main` 时就已经红着，带三处 CS8602。
3. 先 `./scripts/collect-coverage-serial.sh` 再 `python3 scripts/coverage-summary.py TestResults/coverage-serial --min 100 --min-branch 100`。工具链升级会移动覆盖率*分母*（新测试程序集、形状不同的生成文件）。此处悄然下降意味着门禁现在量的是别的东西。
4. `dotnet format Vapor.sln --verify-no-changes`。
5. `python3 scripts/verify-testing-docs.py`——测试计数必须与 `tests/TESTING.md` 完全一致。
6. `python3 scripts/verify-dependency-versions.py`——自检，让坏掉的脚本在 CI 之前就被抓住。
7. 在 `CHANGELOG.md` 的 `## [Unreleased]` 下记下变更。

有协议或数据模型暴露的包会受到额外审视，因为它们的测试无法证明升级安全：

- **SteamKit2**——Steam 协议面。测试套件用已知向量钉住线上行为（见 `Vapor.Steam.Core` 中的 TOTP 与确认哈希性质测试）；改变登录挑战路由或交易状态转移的 major 升级是产品变更。
- **Microsoft.Data.Sqlite**——拥有六个 SQLite 数据库。迁移启动时幂等且只增不减（见 `docs/production.md` § Data, backup and upgrades），因此加列的升级是安全的，但它带着本仓库唯一已知的 CI 抖动签名：`StorageCollectRunnerTests.DispatchCollectAsync_ConfirmationSettledWithoutConfirmation_ReportsNotConfirmed` 可能在 macOS 那条腿的收尾阶段从 `SqliteJobStore.Dispose` 抛出 `NullReferenceException`。先重跑失败的作业，再当作回归去排查。

## 5. 机械守卫

两个 workflow 强制执行以上规则，谁都不需要记任何事：

| 守卫 | 位置 | 强制内容 |
|-------|-------|----------|
| `verify-dependency-versions.py` | `.github/workflows/ci.yml`（format job） | §2 表 ⇄ 每个 `.csproj`；`test-fleet` 完整性；每个 `*.Tests` 项目设置 `IsTestProject`；runner 上的 `<PrivateAssets>all</PrivateAssets>`；`NuGetAuditMode=all` 与 `TreatWarningsAsErrors` 仍被设置 |
| `dependency-review-action` | `.github/workflows/dependency-review.yml`（pull request） | PR 不得引入有漏洞或 GPL 授权的依赖 |

本地这样运行版本守卫：

```bash
python3 scripts/verify-dependency-versions.py
```

树与本页一致时，它以带 `OK:` 摘要的退出码 0 结束；否则退出码 1，每条违规一行。新依赖预期会失败它一次——那次失败就是来此加一行的提示，而这正是让本页不至于悄悄烂成空文的东西。
