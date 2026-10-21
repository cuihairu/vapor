简体中文 | English

# Vapor 插件开发指南

Vapor 的 Agent 内置一套插件系统：隔离加载、按 SemVer 门控
的 API 兼容性、信任/权限模型，以及一个小而明确的 API 面。
本指南完整走一遍插件的构建、声明、配置与调试。

本文所述的每个细节都有七个官方插件和测试套件在演练 —— 
`Vapor.Plugins.TestPlugin`（基础设施测试）、
`Vapor.Plugins.MarketWatch`（最丰富的示例），以及 
`Vapor.Plugins.Core.Tests` 中的加载/卸载测试。

## 插件能做什么

插件是一个 .NET 类库，由 Agent 在启动时从插件目录加载。它
可以按任意组合贡献以下能力：

| 能力 | 接口 | 宿主如何处置 |
|------------|-----------|----------------------------|
| 动作 | `IActionPlugin` | 把贡献的 `IAction` 注册进 Agent 的动作注册表；它们经任务（`run_action`）变为可调用 |
| 控制台命令 | `ICommandPlugin` | 加载时收集，由 `commands` 权限门控；树内尚无宿主提供运行它们的前端，因此目前只是契约面 |
| Web 路由 | `IWebApiPlugin` | 加载时收集，由 `web` 权限门控；尚无宿主把它们经 HTTP 挂载（监控插件改为在自己的监听器上自托管端点）——见 [Web 路由](#web-routes-iwebapiplugin) |
| 会话事件 | `IEventPlugin` | 经 `PluginEventDispatcher` 接收每一个会话事件（状态变更、认证/两步验证提示、错误） |

## 快速上手

一个最小的插件目录长这样：

```
plugins/
└── my-plugin/
    ├── plugin.json
    └── MyPlugin.dll
```

`plugin.json`：

```json
{
  "id": "acme.my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "apiVersion": "1.0",
  "description": "Does something useful.",
  "trust": "community",
  "permissions": ["actions"],
  "resources": ["network"],
  "entryAssembly": "MyPlugin.dll"
}
```

`MyPlugin.cs`：

```csharp
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

public sealed class MyPlugin : IPlugin, IActionPlugin
{
    public PluginInfo Info { get; } = new(
        Id: "acme.my-plugin",
        Name: "My Plugin",
        Version: new Version(1, 0, 0),
        ApiVersion: PluginApi.Current);

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task ShutdownAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public IEnumerable<IAction> GetActions()
    {
        yield return new HelloAction();
    }
}
```

如果程序集恰好包含一个公开的 `IPlugin` 实现，清单里可以省
略 `entryType`；否则须显式声明：

```json
{ "entryType": "Acme.MyPlugin.MyPlugin" }
```

用 `VAPOR_PLUGINS_DIR`（默认 `./plugins`）把 Agent 指向该
目录，插件即随启动加载。

## 清单（plugin.json）

| 字段 | 必填 | 说明 |
|-------|----------|-------|
| `id` | 是 | 稳定标识，用于去重、日志与注册表键 |
| `name` | 是 | 显示名 |
| `version` | 是 | 插件自身的 SemVer 版本 |
| `apiVersion` | 是 | 插件构建所依据的插件 API 的 SemVer 版本（用 `PluginApi.Current`） |
| `description` | 否 | 展示于日志与加载报告 |
| `entryAssembly` | 是 | DLL 文件名，相对插件目录 |
| `entryType` | 否 | `IPlugin` 实现的完整类型名；程序集恰有一个公开实现时可省略 |
| `trust` | 否 | `unknown`（默认）、`community` 或 `official` —— 见[信任与权限](#trust-and-permissions) |
| `publisher` | 否 | 自由格式的发布者声明标签；空白/纯空白字符按未提供存储。仅供参考 —— 安装时与目录索引比对、更新检查时漂移标记，绝非签名。见[信任边界，直白陈述](#trust-boundary-stated-plainly) |
| `permissions` | 否 | `actions` / `commands` / `web` / `events` 组成的数组 —— 见下文 |
| `resources` | 否 | `network` / `filesystem` / `process` 组成的数组 —— 建议性运行时资源声明；见[运行时资源声明](#runtime-resource-declarations) |
| `configuration` | 否 | 初始化时交给插件的自由格式字符串键值 |
| `configurationSchema` | 否 | `configuration` 的逐键校验规则 —— 在 `InitializeAsync` 之前检查；见[配置](#configuration) |
| `dependencies` | 否 | 本插件依赖的其他插件，可带 `apiVersion` 约束 —— 启用时解析；见[依赖](#dependencies) |

清单的解析很宽松（允许注释与尾随逗号），但校验很严格：非
法的 `trust` 值或未知的权限名会让该插件的发现失败并按加载
失败上报，绝不静默跳过。

## 生命周期

1. **发现** —— 宿主扫描 `VAPOR_PLUGINS_DIR`（每个插件一个
   子目录），解析各自的 `plugin.json`。无效清单被上报并跳过
   。
2. **依赖解析** —— 在已发现的插件之上构建声明的 
   `dependencies` 图：缺失或成环的依赖、不满足的 
   `apiVersion` 约束会把依赖方排除并附上失败消息，幸存者按依
   赖顺序加载（见[依赖](#dependencies)）。
3. **兼容性检查** —— `PluginApi.IsCompatible` 校验主 API 
   版本与宿主一致、插件的次版本不超过宿主的（为 API 1.2 构建
   的插件能跑在 1.4 宿主上，反过来不行）。预发布/构建元数据
   被忽略。
4. **信任门** —— 宿主的 
   `PluginManagerOptions.MinimumTrust` 在任何插件代码运行之
   前强制执行。
5. **权限评估** —— 实现了能力却没有对应声明的，会在 
   `InitializeAsync` 之前被剥除（或在严格策略下被拒绝）。
6. **隔离加载** —— 入口程序集加载进一个可回收的 
   `AssemblyLoadContext`；实例化插件类型并运行 
   `InitializeAsync`。
7. **贡献** —— 宿主触发 `PluginLoaded`；Agent 注册获准的
   动作并挂上获准的事件订阅者。
8. **关停/卸载** —— 先触发 `PluginUnloading`（宿主移除各
   项贡献），随后运行 `ShutdownAsync`，最后卸载加载上下文并
   回收。

初始化与关停失败是隔离的：一个坏掉的插件绝不会妨碍其他插
件加载。

## 信任与权限

插件声明*自己是什么*、*需要什么*；宿主决定给什么。

**信任级别**（清单 `trust` 字段）：

- `unknown` —— 未声明（默认）
- `community` —— 经审阅的第三方插件
- `official` —— 随宿主一同发布与维护

### 运行时资源声明

清单还可声明 `resources` —— 插件*自称*在宿主提供的面之外
还会触碰什么。已知名称有 `network`（超出宿主 HTTP 客户端
的连接）、`filesystem`（宿主管理的插件目录之外的文件）和 
`process`（子进程）。

与 `permissions` 不同，resources **仅是建议性登记**：运行
时什么都不强制（量化的故障域与进程沙箱非目标记录在
[进程边界笔记](research/process-boundary.md)中）。声明会
被规范化（小写、去重），加载时对照已知名称校验，随后搭上
每一个上报面供运维审阅：`plugin_list` / 每个 `plugin_*` 
输出镜像都为每个插件带一个 `resources` 数组（未声明时为空
），ControlPlane 的逐 Agent 清单镜像它，
`GET /v1/plugins/installed` 暴露它，管理面板在每个已安装
插件行上显示 `res:…` 徽片。Obsidian 的 `isDesktopOnly` 是
这种“先声明、后审阅”最小形态的参照（见市场调研，§7）。

**权限**（清单 `permissions` 数组）：

| 权限 | 授予什么 |
|------------|--------|
| `actions` | `IActionPlugin.GetActions()` 的结果被注册 |
| `commands` | `ICommandPlugin.GetCommands()` 的结果在加载时收集；树内尚无宿主运行它们 |
| `web` | `IWebApiPlugin.GetRoutes()` 的结果在加载时收集；尚无宿主经 HTTP 挂载它们 |
| `events` | 插件被订阅到会话事件 |

强制执行模型默认是最小信任：

- 实现了能力接口却没有对应权限声明的会被**剥除** —— 动作/
  路由/命令永不被注册，插件也收不到事件 —— 并记录一条警告。
- 宿主可以用 `PluginManagerOptions` 加硬：

```csharp
var manager = new PluginManager(hostServices, loggerFactory, new PluginManagerOptions
{
    MinimumTrust = PluginTrust.Community,       // refuse unknown-trust plugins
    RequirePermissionsDeclared = true           // undeclared capability = load failure
});
```

`LoadedPlugin.GrantedPermissions` 暴露插件实际拿到的权限
交集（对诊断、以及对展示插件能力的宿主都有用）。

实现了什么就声明什么 —— 官方插件全都这么做。

## 能力

### 动作（`IActionPlugin`）

动作是主力：Agent 在 bot 会话上（经任务）或独立执行的具名
操作。返回带输出字段的 `ActionResult`；错误放进 `Error` 
并置 `Success = false`。

```csharp
public sealed class HelloAction : IAction
{
    public string Name => "hello";

    public ActionMetadata Metadata { get; } = new(
        Name: "hello",
        Description: "Greets the payload subject",
        RequiresLogin: false,        // set true only if you need a live session
        TimeoutSeconds: 10)
    { Safety = ActionSafety.ReadOnly }; // execution-safety class (see below)

    public Task<ActionResult> ExecuteAsync(
        BotSession session,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var who = PayloadReader.GetString(payload, "who") ?? "world";
        return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
        {
            ["greeting"] = $"hello {who}"
        }));
    }
}
```

经 `PayloadReader`（`GetString` / `GetInt32` / `GetBool`
）读取 payload 值 —— 它规范化来自 JSON 的值，因此 `5`、
`"5"` 和 `5.0` 都能用。

`RequiresLogin: false` 的动作仍会收到 `session` 参数；把
它当可空对待，除非你要求登录，否则永远不要假设已有已认证
的 Steam 客户端。

`Safety`（`Vapor.Protocol.ActionSafety`）分级的是重复执行
对外部世界的影响，控制平面据此限制再派发（见
[动作目录](actions.md)的“执行安全”约定）：`ReadOnly`（从
不改动）与 `Idempotent`（重复执行收敛到同一状态）使用配置
的派发尝试上限；`GuardedWrite`（有改动，但有界重试是安全
的）与 `NonIdempotent`（重复执行使外部副作用翻倍）总共封
顶 2 次派发尝试。该属性可选，默认 `Unknown`，调度器保守对
待（与不安全分级同一上限），注册表还会为它记一条警告 —— 
永远给你的动作分级。默认值正是让基于旧元数据二进制构建的
插件程序集保持兼容的关键；`Safety` 是 init-only 属性，不
是位置参数。

### 命令（`ICommandPlugin`）

供宿主前端使用的交互式命令：

```csharp
public IEnumerable<IPluginCommand> GetCommands()
{
    yield return new MyPingCommand();
}

public sealed class MyPingCommand : IPluginCommand
{
    public string Name => "my-ping";

    public string Description => "Responds with pong";

    public Task<PluginCommandResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => Task.FromResult(PluginCommandResult.Ok("pong"));
}
```

宿主在加载时收集命令（由 `commands` 权限门控），但
**树内没有任何宿主提供运行它们的前端** —— Vapor 的控制平
面与 Agent 都是无头的。该接口是预留的契约面；插件当下需要
可达的能力时，请用动作（可经任务管线调用）。

### Web 路由（`IWebApiPlugin`）

与宿主无关的 HTTP 处理器 —— 适合不得依赖控制平面的仪表盘
或指标端点：

```csharp
public IEnumerable<PluginWebRoute> GetRoutes()
{
    yield return new PluginWebRoute(
        "GET", "/my-plugin/status",
        (_, _) => Task.FromResult(PluginWebResponse.Json("{\"ok\":true}")));
}
```

宿主在加载时收集它们（由 `web` 权限门控，未声明则剥除），
但**尚未经 HTTP 挂载** —— 今天没有任何 Agent 或控制平面监
听器伺服 `PluginWebRoute`。在带暴露面与认证方案的宿主侧监
听器出现之前，与宿主无关端点的可行模式是自托管，正如监控
插件用自己的 TCP 监听器伺服 `/metrics`。在运维表达需求之
前，把路由挂进宿主是刻意的非目标。

### 会话事件（`IEventPlugin`）

实现接口*并且*声明 `events` 权限即可订阅。宿主的 
`PluginEventDispatcher` 把每个 `SessionEvent` 扇出给全部
订阅者；抛异常的处理器会被隔离（记日志，绝不向上传播）。

```csharp
public sealed class SessionLogger : IEventPlugin
{
    // ... IPlugin members ...

    public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{sessionEvent.Timestamp:O} {sessionEvent.Type} {sessionEvent.AccountName}");
        return Task.CompletedTask;
    }
}
```

事件包括 `StateChanged`（带 `NewState`）、
`AuthCodeNeeded`、`TwoFactorCodeNeeded`、`Connected`、
`Disconnected` 和 `Error`（带 `Message`）。处理器应快速且
不阻塞；耗时的工作放到你自己的任务上。

## 配置

清单的 `configuration` 节是一个字符串键值字典，交给 
`IPluginContext.Configuration`。用 `Vapor.Plugins.Core` 
的类型化扩展读取；每个读取器都接受一个环境变量来覆盖配置
值（优先级：**环境变量 > 配置 > 回退值**），运维因此无需
编辑 plugin.json 就能调整行为：

```csharp
var config = context.Configuration;
var host = config.GetString("metrics.host", "127.0.0.1", "VAPOR_METRICS_HOST");
var port = config.GetInt32("metrics.port", 9700, "VAPOR_METRICS_PORT", min: 0, max: 65535);
var consumeEnabled = config.GetBool("consume.enabled", false, "VAPOR_CSGO_CONSUME_ENABLED");
var openEnabled = config.GetBool("open.enabled", false, "VAPOR_CSGO_OPEN_ENABLED");
```

解析不了或超出范围的值会静默回退 —— 关键值在启动时自行校
验，并把解析结果记进日志。

要把这些静默回退变成加载期错误，请在清单里声明 
`configurationSchema`。每个条目描述一个配置键，由宿主在 
**`InitializeAsync` 之前**检查 —— 违规会使加载失败，并给
出点名每个违规键的字段级消息：

```json
"configurationSchema": {
  "metrics.port": { "type": "int", "min": 0, "max": 65535 },
  "alert.threshold": { "type": "decimal", "min": 0.01 },
  "metrics.mode": { "type": "string", "enum": ["push", "pull"] },
  "metrics.token": { "type": "string", "required": true },
  "metrics.verbose": { "type": "bool" }
}
```

规则字段：`type`（`string` / `int` / `bool` / `decimal`，
必填）、`required`（默认 false）、`min` / `max`（仅 int/d
ecimal 规则，含边界）、`enum`（仅 string 规则，大小写不敏
感）。空白值视为未提供（与读取器的空白即回退语义一致），
出现在 `configuration` 里却不在 schema 中的键会被拒绝 —— 
schema 对随它发布的这一版插件是权威的，拼错的键会让加载失
败而不是静默无事发生。环境变量覆盖不经 schema 校验（它们
经读取器的回退解析）。Market Watch 插件里有一个完整示例。

## 依赖

清单可以声明一个可选的 `dependencies` 数组。每个条目点名
另一个插件的 `id`，并可选地约束该插件必须声明的 
`apiVersion`：

```json
"dependencies": [
  { "pluginId": "vapor.base" },
  { "pluginId": "vapor.other", "apiVersion": "1.2" }
]
```

启用时，宿主在已发现的插件之上构建依赖图：

- **缺失或失败的依赖** —— 从未被发现、或自身加载失败的 
  `pluginId` 会把依赖方排除，并附上说明原因的消息；失败会传
  递级联（A 需要 B 而 B 失败，A 也被排除）。
- **`apiVersion` 约束** —— 按宿主自身的兼容规则检查：提供
  方的主版本必须精确匹配，次版本至少达到所请求的（1.3 的提
  供方满足 1.2 的约束）。声明了无法解析的版本、主版本不符或
  次版本过低的提供方会把依赖方排除，消息会说明违反了哪条规
  则。
- **循环依赖** —— 环上每个成员（包括依赖自身的插件）都失
  败，消息列出环的成员；仅依赖环成员的插件则按常规的“依赖失
  败”消息失败。
- **加载顺序** —— 幸存者在它们所依赖的一切之后加载；无依
  赖的插件保持发现顺序。

条目必须唯一（大小写不敏感）且不得点名插件自身；
`apiVersion` 一旦出现必须是合法的 SemVer 版本 —— 两者都在
清单解析期被拒绝，与任何其他清单错误一样。图失败与其他加
载失败一样隔离：`PluginLoadReport.Failures` 点名每个被排
除的插件，其余照常加载。

## 宿主服务

`IPluginContext.Host` 暴露：

- `LoggerFactory` —— 在宿主的日志管线之下创建 logger。永
  远用这些 logger 记日志，而不是 `Console`。
- `Services` —— 供可选宿主服务（`IVaporCache`、
  `ISessionManager`、…）使用的 `IServiceProvider`。把一切都
  当可选对待（`GetService`，判空）：宿主未必注册全部服务，
  插件必须优雅降级。

## 隔离与卸载规则

每个插件加载进自己的可回收 `AssemblyLoadContext`。实践中
：

- 共享契约没问题：来自 `Vapor.Plugins.Core` 与 
  `Vapor.Steam.Core` 的类型解析到宿主的副本，因此你交出的 
  `IAction` 实例与宿主认识的是同一批类型。
- 私有依赖归你：把你自己的 NuGet DLL 放进插件目录；它们加
  载进你的上下文，也随它卸载。
- 关停时清理：在 `ShutdownAsync` 里取消你的后台循环并等它
  们结束（见 `MarketWatchPlugin`），移除你在宿主服务上注册
  的观察者，释放自有的 `HttpClient`/处理器。宿主在调用 
  `ShutdownAsync` 之前移除你的注册表贡献。
- 不要泄漏静态状态：静态量能扛过卸载并阻止回收。优先用实
  例字段；测试里用 `LoadedPlugin.UnloadTracker` 验证回收。

## 调试技巧

- 启动日志会记录每一次插件加载，带版本、获准权限与贡献计
  数；失败连同原因一起记录（API 不兼容、信任门、严格模式缺
  权限、初始化异常）。
- `PluginManager.LoadAllAsync` 返回带 `Loaded` 与 
  `Failures` 的 `PluginLoadReport` —— 测试里对它断言，或在
  宿主工具里呈现。
- 加载失败是隔离的：查报告，别假设整个目录都失败了。
- 按本仓库的方式写测试：`Vapor.Plugins.Core.Tests` 在磁盘
  上暂存插件目录（`PluginStaging`）并经 `PluginManager` 加
  载真实程序集；
  `Vapor.Plugins.MarketWatch.Tests/PluginHostLoadTests.cs` 
  展示了对真实插件的完整宿主加载。
- 实现了能力却忘记声明权限的插件能正常加载，但会静默失去
  该能力（最小信任默认）—— 遇到东西“不出现”时，先找 
  `capability stripped` 警告。

## 官方插件

| 插件 | 清单 id | 演示内容 |
|--------|-------------|--------------|
| Mobile Authenticator | `vapor.mobile-authenticator` | 仅动作；TOTP/确认逻辑留在宿主之外 |
| Monitoring | `vapor.monitoring` | 动作；自托管的 Prometheus 端点与后台指标泵 |
| Market Watch | `vapor.market-watch` | 动作 + 后台轮询 + 配置 + webhook 告警；完整的信任/权限声明 |
| CS2/CSGO | `vapor.csgo` | 动作 + 配置 + 结果记录/归档 + 消耗台账；干跑的 CS:GO 武器箱模拟器、只读背包记账，以及门控的真实 game-coordinator 开箱（见上文其章节） |
| Dota 2 | `vapor.dota2` | 仅动作；游戏无关的交易基座的 Dota 2 落地 —— 带 7 天冷却分类的只读库存、Dota 范围的交易报价列表，以及门控（`trade.enabled`）的带实时所有权校验的单笔报价发送（见 [Valve 游戏插件](game-plugins.md)） |
| Game Data | `vapor.game-data` | 仅动作；只读 Steam Web API 摘要（Dota 2 比赛/英雄/物品，TF2/CS2 schema），密钥由 Agent 侧处理（见下文其章节） |
| Game Access | `vapor.game-access` | 仅动作；十六个游戏访问动作 —— 从宿主拆出且名称/payload 不变的十四个（挂机、许可与密钥、成就、库存、战利品、积分商店），加上游戏经济对 `get_game_inventory`/`get_item_details` —— 线缆兼容的抽取先例（见下文其章节） |

按游戏的插件覆盖面 —— CS:GO/CS2、Dota 2 与 TF2 各有什么、
什么是刻意的非目标、未来的每游戏插件长什么样 —— 另行记录
在 [Valve 游戏插件](game-plugins.md)。

## 打包清单

1. 类库面向与宿主相同的 .NET 版本，把 
   `Vapor.Plugins.Core`（用动作的话再加 `Vapor.Steam.Core`）
   作为 `ProjectReference` 或包引用 —— 契约程序集与宿主共享
   ，**不要**把它们复制进你的插件目录。
2. 把 `plugin.json` 连同入口 DLL 一起产出（
   `CopyToOutputDirectory`）。
3. 如实声明 `trust` 与 `permissions`；实现了的什么都别漏
   。
4. 只发布你自己的二进制；共享契约程序集由宿主自行解析。
5. 把目录放进 `VAPOR_PLUGINS_DIR`，到启动日志里找 
   `granted [...]` 那一行。

## 运行时安装与 PluginStore

在启动期目录扫描之外，插件可以在 Agent 运行中安装。传输方
式刻意无聊：由 Agent 自己下载 zip 包，校验后解包到暂存目
录并热加载 —— ControlPlane 从不经手二进制。

### 包格式

包是一个普通 zip，根目录含 `plugin.json`、入口程序集及任
何私有依赖 DLL（与插件目录同一布局）。每个安装请求都必须
携带包的 SHA-256 十六进制摘要；不匹配则在向插件根目录写入
任何东西之前失败。

```
my-plugin.zip
├── plugin.json          # required at the zip root
├── MyPlugin.dll
└── Deps/*.dll
```

### Agent 侧任务动作

四个宿主动作（无需 bot 会话，用 `agent:{id}` 任务目标前缀
定向）覆盖插件生命周期：

| 动作 | Payload | 行为 |
|--------|---------|-----------|
| `plugin_install` | `url`、`sha256`、可选 `pluginId`/`version`；目录模式额外把索引声明作为 `expectedTrust`/`expectedPermissions`/`expectedApiVersion` | 下载 → 校验和 → 暂存解包 → 清单校验（含上文的目录对账）→ 热加载；重装已安装的 id 会替换之（`replaced: true`） |
| `plugin_uninstall` | `pluginId` | 卸载并退役目录（先改名挪开再删除）；卸载未知 id 是幂等成功（`removed: false`） |
| `plugin_list` | — | 报告当前清单 |
| `plugin_update_check` | `candidates`（从索引暂存的 `{ id, version }` 对） | 针对已加载插件给出每个候选的只读裁决 —— `updateAvailable` / `upToDate` / `notComparable` / `notInstalled`；从不下载或替换任何东西 |

每个动作的输出都在 `plugins` 之下带上 Agent 的完整已装列
表，因此即便安装失败，ControlPlane 镜像也保持最新。安装管
线在碰真正的插件根目录之前先校验：URL 方案（`http`/
`https`/`file`）、摘要十六进制、zip-slip 条目、清单在根目
录，以及 —— 当请求了 `pluginId`/`version` 时 —— 清单与它
们相符。失败的安装不在插件目录留痕。

### 安装期的目录对账

目录模式的安装（带 `pluginId` 的 
`POST /v1/plugins/install`）把索引条目自身的 `trust`/
`permissions`/`apiVersion`/`publisher` 声明带进安装指令，
成为 `expectedTrust`/`expectedPermissions`/
`expectedApiVersion`/`expectedPublisher`。Agent 在热加载
之前，拿这些期望与包里实际携带的清单对账：trust 与权限名
大小写不敏感比较（权限按顺序无关的集合），`apiVersion` 精
确比较，publisher 大小写不敏感比较，索引未声明的字段不检
查。任何分歧都会以一条聚合错误让安装失败，点名每个分歧字
段 —— 运维在目录里浏览到的元数据与包内容一起核验，而不是
分走两条通道。

当索引还声明了 `signing` 块时，目录把两半都暂存为 
`expectedSigningPublicKey`/`expectedSigningSignature`。Ag
ent **在校验和通过之后、zip 解包之前**验证包签名 —— 未通
过验证的包绝不被解压。签名方案是对运维钉住的十六进制小写
摘要的 ASCII 字节做 RSA-PSS（SHA-256），因此被验证的正是
运维钉住的那份摘要，而不是它的某种再序列化形态。

### ControlPlane PluginStore

ControlPlane 增加了一个插件**索引源**（
`Vapor_PLUGIN_INDEX_URL`，形如下文的 JSON 文档）和若干 RE
ST 端点，把安装任务扇出给点名的 Agent：

```json
{ "plugins": [ { "id": "vapor.monitoring", "name": "Monitoring",
  "version": "1.0.0", "apiVersion": "1.0", "description": "...",
  "url": "https://.../vapor.monitoring.zip", "sha256": "<64 hex>",
  "trust": "official", "permissions": ["actions"],
  "publisher": "vapor",
  "signing": { "publicKey": "<base64 SPKI>", "signature": "<base64 RSA-PSS>" } } ] }
```

- `GET /v1/plugins/catalog` —— 拉取到的索引（60 秒缓存，
  `error` 内联呈现）
- `GET /v1/plugins/installed` —— 每个 Agent 最近上报的清
  单
- `POST /v1/plugins/install` —— 按 `pluginId`（从目录解析
  ）或直接 `url`+`sha256`；每个 Agent 一个定向任务
- `POST /v1/plugins/uninstall/{pluginId}` —— 从点名的 Age
  nt 卸载
- `POST /v1/plugins/inventory/refresh` —— 重跑 
  `plugin_list` 以重新同步镜像
- `POST /v1/plugins/update-check` —— 在点名（或全部已连接
  ）的 Agent 上运行 `plugin_update_check`；裁决呈现在 
  `/v1/plugins/installed` 的 `updates` 之下

管理控制台的 `插件管理` 面板（PluginStore）渲染目录，支持
逐 Agent 定向与一键批量安装。

### 信任边界，直白陈述

校验和保证的是*相对于你钉住的摘要的完整性*，而不是包的来
源；`trust` 仍是清单声明的标签，宿主的 `MinimumTrust` 策
略不变。`publisher` 字段是同一种陈述：索引与清单关于自身
的自由格式标签，安装时对账，更新检查时在双方都声明且不一
致的情况下漂移标记（`publisherChanged`）。它是建议性元数
据，**不是**签名 —— 它的任何内容都不被验证，恶意索引源可
以随意声明任何 publisher。在意来源时，见下文 `signing` 块
；把 `Vapor_PLUGIN_INDEX_URL` 指向你控制的索引源，钉住你
自己算出的摘要。

`signing` 块是唯一真正被验证的来源主张，而且只对照**你**
在 Agent 上钉住的密钥验证。把 `VAPOR_PLUGIN_TRUST_KEYS`（
逗号分隔的 base64 SubjectPublicKeyInfo）设为你接受的发布
者密钥。一个不钉任何密钥的 Agent 对声明了签名的目录条目
**故障关闭** —— 它无法锚定的来源主张被当作未满足而非被跳
过。密钥以规范化后的 SPKI 字节比较，因此 base64 换行无关
紧要；某一条解析不了的 pin 会被跳过而不禁用其余。没有 CA
、没有证书链也没有吊销列表：吊销一个发布者就是编辑你的 pi
n 列表，这是配置变更而非基础设施。信任 
`Vapor_PLUGIN_INDEX_URL` 去*点名*密钥 —— 密钥本身仍必须是
你的 —— 并让 `VAPOR_PLUGIN_TRUST_KEYS` 短到一眼能审计。

## 与 ASF（ArchiPlugin）的对齐

Vapor 的插件系统以 ArchiSteamFarm 的官方插件机制（
`IPlugin` 核心 + 可选加入的能力接口）为蓝本，并按 Vapor 
的架构调整。本节陈述映射关系与刻意的分歧，让来自 ASF 的插
件作者知道该期待什么。（ASF 参考：
[JustArchiNET/ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)
 中的 `ArchiSteamFarm/Plugins/Interfaces/`、
[Plugins / Plugins-development wiki 页面](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins)
，以及 FreePackages、ASFEnhance 等社区插件。）

| 关注点 | ASF（ArchiPlugin） | Vapor | 说明 |
|---------|-------------------|-------|------|
| 契约形态 | 极小的 `IPlugin`（`Name`/`Version`/`OnLoaded`）+ 约 22 个可选加入的能力接口，经 `OfType<T>()` 扇出分派 | `IPlugin`（`Info`/`InitializeAsync`/`ShutdownAsync`）+ 可选加入的 `IActionPlugin`/`ICommandPlugin`/`IWebApiPlugin`/`IEventPlugin` | 同一增量模式：新能力以*新的可选接口*交付，旧插件不受触动 |
| 发现 | 递归扫描 `plugins/` 目录的 `*.dll` + MEF2 约定目录；无清单 | `VAPOR_PLUGINS_DIR`，每插件一个子目录，显式 `plugin.json` | 清单携带 `trust`、`permissions` 与 `configuration` —— 这些是 ASF 从配置残料里读的东西 |
| 版本兼容 | 对第三方没有（不匹配以 `TypeLoadException`、冗长日志、10 秒延迟、进程退出码 1 浮现）；ASF 的*官方*插件按精确版本钉住（`HasSameVersion`） | `PluginApi.IsCompatible` 握手：主版本必须匹配、插件次版本 ≤ 宿主次版本；**official 信任的插件必须精确面向宿主的 API 版本**（对齐 ASF 的 `HasSameVersion`） | 带原因的显式拒绝好过崩溃；官方钉版让捆绑插件不与同发的宿主漂移 |
| 宿主 API 暴露 | 静态单例（`ASF.*`）+ 传入每个钩子的 `Bot` 实例 | `IPluginContext` → `IPluginHostServices`（`ILoggerFactory`、`IServiceProvider`） | 刻意分歧：以 DI 取代全局状态，为了可测试性与热卸载卫生 |
| 逐插件配置 | `GlobalConfig.json` / `BotConfig.json` 的 `[JsonExtensionData]` 残料 | `plugin.json` 里的 `configuration` 映射，交给 `InitializeAsync` | 同一思路，声明在插件所在之处 |
| 事件面 | `IBotConnection`（登录上/下线）、`IBotCardsFarmerInfo`（挂机生命周期）、`IBotCommand2`、聊天/交易/好友钩子，PICS 变更流 | `IEventPlugin.OnSessionEventAsync`（状态变更、连接/断开、认证/2FA/QR 提示、错误） | Vapor 刻意没有 ASF 挂机钩子的等价物：挂机编排在控制平面一侧，经任务/审计/事件总线流动，而非 Agent 本地状态 |
| 自动更新 | 按宿主版本索引的 GitHub-release 资产（`Plugin-V6-0.zip` 约定），可选加入的白名单，重启时应用 | PluginStore：索引 URL + SHA-256 钉住的 zip + `plugin_install` 任务，立即热加载 | Vapor 在运行时安装而无需重启；更新*策略*（可选加入、摘要钉住）与 ASF 的保守一致 |
| 隔离 | 默认加载上下文，进程生命周期常驻；更换需重启 | 每插件一个可回收 `AssemblyLoadContext`；`plugin_uninstall` 热卸载并验证回收 | 超出 ASF |
| 信任与权限 | 自定义插件以完全信任加载（`-modded` 警告是唯一信号） | 清单声明的 `trust` 门 + 逐能力 `permissions`，在任何插件代码运行*之前*评估；未声明的能力被剥除（或在严格模式下被拒绝） | 超出 ASF；Vapor 插件默认不受信任 |
| Web 面 | 插件程序集成为 MVC application part；`IWebServiceProvider`/`IWebInterface` 钩子 | 与宿主无关的 `PluginWebRoute` 契约 —— 加载时收集并按权限门控，尚无任何宿主挂载（树内与宿主无关的端点自托管，如监控的 TCP 监听器） | 更小、与宿主无关的面 |

**兼容性策略（取自 ASF 的弃用纪律）：** 新能力即新的可选
接口；钩子签名必须变更时，发布带编号的后继接口而不是改动
旧的（ASF 的 `IBotCommand2` 模式）；行为弃用在移除前至少
一个版本内记日志警告。核心的 `IPlugin`/`IPluginContext` 
对与上文的 `PluginApi` SemVer 规则是插件唯一可依赖的契约 
—— 宿主中的其他一切都可以自由变更。


## 游戏访问插件（`vapor.game-access`）

本插件是一次**线缆兼容的抽取**：过去由 Agent 宿主直接注册
的十四个游戏访问动作（`play_games`、`get_card_drops`、
`get_playtime`、`add_license`、`redeem_key`、
`get_achievements`、`unlock_achievements`、
`reset_achievements`、`get_inventory`、`loot_inventory`、
`find_duplicates`、`swap_duplicates`、
`get_points_shop_summary`、`claim_points_shop_items`）现
在位于一个独立的官方插件程序集中，ASF 风格。游戏经济对 
`get_game_inventory` / `get_item_details`（库存聚合与逐物
品市场估值）随后加入同一插件，使其达到十六个动作。

### 它为何存在

ASF 把 `ArchiSteamFarm.OfficialPlugins.*` 作为随宿主同行
的独立程序集发布；Vapor 的游戏访问面曾长到 Agent 动作注册
表的一半，以插件形式持有它让边界变得显式：宿主保留会话引
擎、交易/市场/商店面与诊断；插件持有“账号玩什么、拥有什么
、解锁什么”。

### 兼容契约

- **名称与 payload 模式逐字节不变。** 已部署的任务、日程
  与控制平面代码路径按同样的字符串派发。
- **Agent Docker 镜像把插件捆进** 
  `/app/plugins/vapor.game-access`（Monitoring 的机制，如今
  多了第二位住户），因此默认的 hello 能力集不变。
- **没有该插件时** Agent 不再宣告这十六个名称；控制平面调
  度器只路由已连接 Agent 在 hello 中宣告过的动作，因此受影
  响的任务在派发时失败（“no agent declares the action”）—— 
  强行路由的子任务则会以常规的 `action not found` 会话错误
  失败。两个方向都不会静默空转。
- 源码运行不会自动暂存任何东西：把 `VAPOR_PLUGINS_DIR` 指
  向包含已构建插件输出的目录，才能拿到完整动作集。

### 宿主服务接线，不变

该插件解析的宿主单例与被删除的宿主接线相同 —— 来自宿主工
厂的 logger、供带缓存的掉卡/时长动作使用的 `IVaporCache`
，以及来自宿主服务提供器、供 loot/swap 使用的 
`TradeRateLimiter`。没有限流器的宿主会记录警告，被限流的
动作改为不限流运行（其构造器接受 null 限流器）而不是让加
载失败。拆分对核心的唯一必要改动：
`SendTradeOfferAction.NoopLease` 改为 public，让 loot/swa
p 离开宿主程序集后仍能以同样方式获取租约。

### 测试

迁移来的动作套件位于 `Vapor.Plugins.GameAccess.Tests`，其
中 `PluginHostLoadTests` 经真实的 `PluginManager`（发现、
隔离 ALC、official 信任门控）加载编译出的插件并对十六个名
称断言 —— 把 MarketWatch 的宿主加载先例应用到一次抽取上。

## CS2/CSGO 插件（`vapor.csgo`）

本插件兼任完整能力面的参照示例：动作 + 配置 + 结果记录，
外加（在一道显式门之后）真实的 game-coordinator 开箱。

### 它是什么

一个**干跑的开箱模拟器**。它拿 Valve 公布的 CS:GO/CS2 开
箱概率对着本地武器箱目录掷点，记录每个结果，并经动作（
`case_open`，可经任务管线调用）暴露 catalog/open/results/
stats；其声明的插件 web 路由（`GET cases`、`POST open`、
`GET results`、`GET stats`）是尚无宿主挂载的契约面。除非
运维启用 `open.enabled` 门（默认**关**），插件里的任何东
西都开不了箱 —— 见 [ToS 边界](#tos-boundary)与
[门控的真实路径](#modes-and-the-backend-seam)。

自 backlog 轮起，插件还携带**本地记账那一半**：
`case_inventory` 把实时 CS2 背包（app 730 / context 2，与
 `get_inventory` 同一读取通道）分类为武器箱容器、钥匙与其
他物品，`case_consume` 把实际消耗记录进台账 —— 于是“还剩
多少把钥匙”“开了哪些箱”可以由数据而非记忆回答。见
[库存与消耗记账](#inventory-and-consumption-bookkeeping)
。

### 算法与来源

概率来自 Valve 自己的公示 —— CS:GO 官方中文站的概率公示页
（probability disclosure，2017-09-11，
[csgo.com.cn](https://www.csgo.com.cn/news/gamebroad/20170911/206155.shtml)
），这是武器箱概率的唯一一手来源：

| 稀有度档 | 公布概率 | 精确掷点窗口（782 中的） |
|-------------|----------------------|----------------------------|
| Mil-Spec（蓝） | 79.923% | 625 |
| Restricted（紫） | 15.985% | 125 |
| Classified（粉） | 3.197% | 25 |
| Covert（红） | 0.639% | 5 |
| 稀有特殊物品（金 —— 刀/手套） | 0.256% | 2 |

同一份公示还固定了其余结构，引擎严格照做：每一档相对上一
高档是 1:5（金:隐秘为 2:5），同稀有度的物品等概率，StatTr
ak™ 对有 StatTrak 变体的物品是一次独立的 1:10 掷点。没有
保底系统 —— 每次开箱都是独立同分布的抽取。

每次开箱的掷点顺序（确定性，基于 `System.Random`，种子可
注入供测试）：

1. **稀有度** —— 对上表 625:125:25:5:2 掷一次。
2. **物品** —— 在该箱所掷档位的物品中均匀选取。
3. **StatTrak™** —— 选中的物品允许时按 1:10。
4. **Float（磨损值）** —— 均匀 U(0,1) 线性映射进物品自身
   的 `[min_float, max_float]` 区间。磨损范围按涂装（paint k
   it）而非按箱定（如 AK-47 | Redline 为 0.10–0.70）；CSFloa
   t 的 float/paint-seed 分析（
   [blog.csfloat.com, 2020](https://blog.csfloat.com/analysis-of-float-value-and-paint-seed-distribution-in-cs-go/)
   ）是映射与磨损阈值的参照：FN < 0.07、MW < 0.15、FT < 0.38
   、WW < 0.45、BS ≥ 0.45。
5. **Paint seed（涂装序号）** —— 0–1000 的均匀整数，与 fl
   oat 独立。

参考过的开源项目（仅思路，未抄代码）：
[Desmait/OpenCasePlugin](https://github.com/Desmait/OpenCasePlugin)
（C#，同一概率表配累计掷点 —— 无许可证，思路参照）、
[jonese1234/Csgo-Case-Data](https://github.com/jonese1234/Csgo-Case-Data)
（逐箱物品池与概率出处），以及 
[kratos1812/Case-Opening](https://github.com/kratos1812/Case-Opening)
（GPL-3.0，SourceMod —— 另一种加权桶掷点）。

### 模式与后端接缝

`ICaseOpeningBackend` 是“决定开出了什么”与“它如何而来”之
间的唯一接缝。插件恰好交付一个实现：

- `SimulationBackend`（默认，也是今天唯一被接受的 
  `backend` 配置值）：纯本地 RNG。无网络、无 Steam 调用、无
  需账号。`case_open` 动作在输出中报告 `mode: "dry-run"`。

请求任何其他 `backend` 值都会让插件初始化带着显式错误失败
，而不是静默模拟。

#### 门控的真实路径（`open.enabled`）

模拟器并不是 `case_open` 的唯一应答方式。门打开时（
`open.enabled: true` 或 `VAPOR_CSGO_OPEN_ENABLED=true`）
且 payload 里有数字型的 `crateAssetId`/`keyAssetId`，该动
作执行一次**真实的 game-coordinator 开箱** —— 与 CS2 客户
端所做的同一事务，在运维自己的账号上，一次一箱：

1. **读取背包** —— 共享的库存分页器（app 730 / context 2
   ）找到箱与钥匙。显式 asset id 可跳过此扫描；自动选取（两
   个 id 都缺省）匹配第一个市场名与目录箱名相同的容器，外加
   任意一把钥匙。
2. **GC 握手** —— `ISteamTransport.EnsureCs2SessionAsync`
    玩 app 730，发送 `ClientHello`（版本 2000568）并等待 
   `ClientWelcome`（SteamKit2 生成的 CS2 GC 绑定 —— 与运维私
   有的 uudog-worker Steam-bot 分支用于其开箱队列的同一协议
   面，本路径正是循此而来）。
3. **开箱** —— `OpenCrate`（2534），`tool_item_id` = 钥匙
   、`subject_item_id` = 箱；插件最多等 120 秒等携带请求 
   `1007`（`UnlockCrate`）的 `ItemCustomizationNotification`
   （1090），它载有奖品 asset id。
4. **奖品映射** —— 重读背包（至多 3 次，因为库存端点可能
   滞后于 GC 应答）把奖品 asset id 映射到市场名；始终不现身
   的奖品以 `matched: false` 上报而不是被丢弃。
5. **台账** —— 消耗（1 箱 + 1 钥匙 + 奖品）记录进 
   `case_consume` 所写的同一台账。

该门**默认关闭**，关闭期间动作拒绝走真实路径，并给出配置
键及其环境变量覆盖的指引。一次门控开箱每个请求精确烧掉一
箱 + 一钥（`count != 1` 被拒绝 —— 要多开就重复调用），这
也是 `case_open` 在控制平面被分级为 `NonIdempotent` 的原
因。真实路径需要已登录的 Steam 客户端会话（bot 会话上的 
`ISteamClientManager`）；没有它时动作带着显式指引失败，而
不是回退到模拟。

### 结果记录

每次开箱向配置的结果文件（`results.path`）追加一行 JSON（
箱 id、物品、稀有度、stattrak、float、磨损、涂装序号、时
间戳）。聚合 —— 每箱开箱数与每稀有度计数 —— 从同一批记录
计算（`GET stats`）。省略 `results.path` 时，结果保存在有
界的内存环（最近 1000 条）中，不落盘。

### 库存与消耗记账

对账拆成运维自行配对的两半：

- **`case_inventory`**（只读）经共享库存分页器翻页 CS2 背
  包并按固定次序规则分类：市场哈希名以 `"Case"` 结尾的是箱
  容器（容器名命中已知箱时携带目录箱 id），以 `"Key"` 结尾
  的是钥匙（“Case Key” 也是钥匙），其余归其他。输出携带 
  `mode: "readonly"` 加台账合计，于是背包减去已记录消耗就是
  应该还在的东西。
- **`case_consume`** 向台账记录一条（箱 + 钥匙 + 奖品）。
  它是**门控的**：`consume.enabled` 默认 **false**（及 
  `VAPOR_CSGO_CONSUME_ENABLED` 环境变量覆盖），打开它只解锁
  本地记账 —— 本插件不存在任何 Steam 写路径。条目记录的是运
  维声明的消耗；物理开箱是运维在 CS2 客户端里（或由下文门控
  的真实路径）亲手完成的。
- 门控的真实开箱也会自动记入台账 —— 每次真实 `case_open` 
  一条（1 箱 + 1 钥匙 + 映射后的奖品）—— 于是无论开箱来自运
  维的客户端还是插件，台账都是唯一事实来源。
- 台账把每条记录以一行 JSON 追加到 `ledger.path`（与 
  `results.path` 同一归档纪律：坏行跳过、I/O 错误吞掉、内存
  保留最近 1000 条）。Web 路由镜像两半：`GET inventory` 供
  台账摘要 + 近期条目，`POST consume` 是门控写入。

### ToS 边界

直白陈述，免得任何人瞎猜：

- CS2 的开箱是**游戏客户端事务**；没有公开 API。规模化地
  自动化它（网页会话脚本、开箱舰队、交易机器人服务）正是 
  [Steam 订阅协议 §4.C](https://store.steampowered.com/subscriber_agreement/)
   禁止的“自动化”，也是 Valve 2016 年 7 月打击开箱/博彩网站
  时针对的模式。
- 因此：默认情况下插件**从不开箱** —— 唯一的开箱面是基于
  公布概率的本地 RNG。真实的 game-coordinator 路径
  **只在一道显式的运维门之后**（`open.enabled`，默认关），
  运维必须为它自己的账号与会话刻意启用。它执行的是与运维自
  己的客户端相同的单账号、一次一箱事务；它不是舰队、不是服
  务，也不是 §4.C 描述的博彩站自动化。
- 记账那一半对 Steam **只读**（`case_inventory` 用与 
  `get_inventory` 相同的普通 Web 读取 —— 读自己的库存不算客
  户端自动化），且除单次门控开箱外
  **不向 Steam 写回任何东西**。没有交易报价、没有市场调用、
  没有网页会话动作；`case_consume` 只向本地文件追加，且只在
  显式的 `consume.enabled` 门之后。每个动作与路由输出都携带
   `mode` 字段（`"dry-run"` / `"gc"` / `"readonly"` / 
  `"ledger"`），运维因此总能分辨是哪个面在应答。
- Steam Web API 条款（
  [apiterms](https://steamcommunity.com/dev/apiterms)）另将
   Steam 数据限定为个人非商业用途；模拟器的输出完全不涉及 S
  team 数据，记账那一半也只经公开端点读取运维自己的库存，在
  条款之内。

## 游戏数据插件（`vapor.game-data`）

本插件补上了 [Valve 游戏插件](game-plugins.md)中的只读分
析候选项：公开的 Dota 2 数据与 TF2/CS2 物品 schema，直接
取自官方 Steam Web API。

### 它是什么

四个**只读动作** —— 每次调用都是带密钥认证、打向 
`api.steampowered.com` 的 GET，投影为紧凑摘要（绝不是原始
的兆级 payload）：

| 动作 | Web API 调用 | 返回 |
|--------|--------------|---------|
| `dota2_match_history` | `IDOTA2Match_570/GetMatchHistory/v1/` | 状态、计数、紧凑比赛列表（int64 id —— 真实比赛 id 超出 int32） |
| `dota2_heroes` | `IEconDOTA2_570/GetHeroes/v1/` | 状态、计数、id/name/localizedName/legs |
| `dota2_game_items` | `IEconDOTA2_570/GetGameItems/v1/` | 状态、计数、id/name/localizedName/cost |
| `econ_item_schema` | `IEconItems_{440,730}/GetSchema/v1/` | 状态、计数、defIndex/name 摘要、appId |

### 密钥处理（Agent 侧，惰性）

Web API 密钥放在插件配置（`webapi.key`）或 
`VAPOR_GAME_DATA_WEBAPI_KEY` 环境变量里 —— 两者都由 
**Agent** 在插件初始化时读取。它永远不会到达控制平面、任
务管线或任何审计记录。未配置密钥时插件照常加载并注册动作
；调用任何一个都会带着显式指引失败（`webapi.key` / 环境变
量名），而不是伪造数据 —— 与开箱插件后端接缝同一套诚实降
级规则。

### 边界

- 无写路径：没有交易报价、没有市场调用、没有库存变更、没
  有游戏客户端自动化 —— 真实比赛、交易与勇士令状进度都在外
  （SSA §4.C，见 [game-plugins.md](game-plugins.md)）。
- **`econ_item_schema` 对 appid 走白名单**，仅 440（TF2）
  与 730（CS2）；其他一律失败并把白名单写明，而不是去探 Val
  ve 的 API。
- Web API 的可用性与覆盖面由 Valve 决定变更；HTTP 与解析
  失败都变成带操作名的失败动作结果，绝不抛进任务管线。
