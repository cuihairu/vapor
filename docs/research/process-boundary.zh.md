简体中文 | English

# 插件进程边界：量化笔记

先说结论：六个官方插件合计只占 **≈ 2.3 MB 常驻内存**（其中托管堆仅
≈ 45 KB）—— 内存不构成把插件挪进独立进程的理由。当前进程内宿主的真实
暴露面是**失控计算故障域**：一个无视自身取消令牌的插件会把它的会话
变成僵尸（该会话之后每个动作全部超时），而 Agent 本身保持在线并继续
续租。本页记录机制清单、故障域矩阵与测量方法，让后续决策（独立进程
vs. 配额 + 硬超时）能建立在数据上。它回答 research §6 候选 5 的
"先量化、再决策"一步；没有任何代码被改动。

## 1. 现有隔离有哪些

| 层 | 机制 | 实际约束住什么 |
|---|---|---|
| 加载 | 每插件可回收的 `PluginLoadContext`（`Vapor.Plugins.Core`） | 版本共存与热卸载；契约程序集（`Vapor.Plugins.Core`、`Vapor.Steam.Core`、`Vapor.Protocol`）按类型标识与宿主共享 |
| 权限 | `plugin.json` 的 `permissions` 门控注册面（动作/事件/web） | 插件能*注册*什么，而不是它运行时能*做*什么 |
| 单动作超时 | `BotSession` 经 `CancelAfter` 把 `ActionMetadata.TimeoutSeconds` 施加到交给 `ExecuteAsync` 的令牌上 | 只约束守规矩的动作 —— 取消是协作式的；没有任何东西能中止一个从不观察令牌的循环 |
| 任务看门狗 | `TaskTimeoutPolicy`（默认 900 s，`AGENT_TASK_TIMEOUT_SECONDS`）取消整个任务 | 把 Agent 的串行任务循环*作为调用方*解堵（`ExecuteActionAsync` 在带令牌的 `TaskCompletionSource` 上等待，因此循环继续推进）—— 但插件任务本身仍在会话内运行 |
| 命令循环守卫 | `BotSession` 的命令循环逐命令捕获每个异常并报告为失败结果 | 插件*异常*只杀死一个任务，不会杀死会话或 Agent |

关键的不对称性：**异常路径被约束住，失控路径没有。**
.NET 没有线程中止；两层超时按构造都是协作式的
（`TaskTimeoutPolicy` 自己的文档注释就说，
令牌"在某处从不被观察"的挂死
正是看门狗无法恢复的情形）。

## 2. 故障域矩阵（源自代码，并对照上述机制核实）

| 插件内的故障 | 爆炸半径 | 运维看到什么 | 恢复 |
|---|---|---|---|
| 动作抛异常 | 单个任务 | 任务报告失败，会话与 Agent 继续服务 | 无需处理 |
| 动作尊重令牌并超时 | 单个任务 | `TimeoutSeconds` 后得到 `"action timeout"` 结果 | 无需处理 |
| 动作忙循环无视令牌 | **会话**：`BotSession` 的 `_actionLock` 被永久持有，该会话之后每个动作排队并超时；卡死的 `HandleExecuteAction` 任务及其 `CancelAfter` CTS 泄漏。**Agent 存活**：看门狗在 900 s 报告该任务，心跳/WS 是独立循环 | Agent 保持"在线"，租约继续续期；该账号的每个动作都以 `"action timeout"` 失败 —— 一个看起来健康的僵尸会话 | 重连会话（Agent 重启或会话拆除） |
| CPU 失控（循环或重计算） | 整个 Agent 进程：线程池争用波及其他会话的命令循环与心跳延迟 | 延迟劣化，高负载下可能错失租约 | 同上，若持续则再加进程重启 |
| 内存失控 | 整个 Agent 进程：不存在每插件配额；OOM 杀死 Agent 及其上的所有会话 | Agent 从 CP 断开 | CP 重连；Agent 重启（带会话恢复） |
| 插件卸载泄漏 | 当前已被约束：`PluginManager.UnloadAsync` 核验 ALC 回收（有测试覆盖） | — | — |

## 3. 内存测量

方法：一个一次性控制台探针（不属于 `Vapor.sln`，因此不进入
覆盖率分母），完全按 Agent 的方式复用
`PluginManager`/`PluginDiscovery`。它先把六个插件完整加载并卸载一遍
（预热：支付 JIT 与文件缓存成本），取一份基线，再从与 Agent 的
`plugins/` 根一致的目录布局加载六个插件，然后在 GC 之后再次读取
`/proc/self/statm`（页级 RSS）、`GC.GetTotalMemory(true)` 与
`Process.PrivateMemorySize64`。

环境：Linux 6.x 内核、.NET 10、Release，展示单次运行
（重复运行偏差在 ~0.1 MB 内）。六个插件全部以 0 失败加载，
并注册 34 个动作：caseopening 1、game-access 16、
game-data 4、market-watch 3、mobile-authenticator 9、
monitoring 1。

| 指标 | 加载全部六个插件时的增量 |
|---|---|
| 常驻集（statm 页 × 4 KiB） | **+2,416,640 B ≈ 2.3 MB** |
| 托管堆（`GC.GetTotalMemory(true)`） | **+45,336 B ≈ 45 KB** |
| 私有字节 | 0（程序集镜像由文件映射且共享；堆增长落在已被触及的 GC 段内） |
| 插件自身 DLL 占盘 | ≈ 0.4 MB 总计（最大：GameAccess 113 KB；每目录的契约 DLL 副本与宿主共享页，从不被双重加载） |

探针（裁剪到测量部分）：

```csharp
var manager = new PluginManager(
    new DefaultPluginHostServices(loggerFactory, emptyServices), loggerFactory);
var report = await manager.LoadAllAsync("/tmp/plugins-dist");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
await Task.Delay(300);
long rssAfter = File.ReadAllText("/proc/self/statm").Split(' ')
    .Select(long.Parse).ToArray()[1] * 4096L;   // resident pages
Console.WriteLine(rssAfter - rssBefore);
```

解读：六个官方插件**比任何能以内存为由证成进程隔离的量级低
一个数量级** —— 2.3 MB 对 ~100 MB 的 Agent 进程。GOG Galaxy
式的每插件进程会把 Agent 的足迹大致按每份进程基线相乘，
去防御一种没有任何官方插件会去触发的故障模式
（内存）。

## 4. 决策落点

- **进程隔离**（GOG Galaxy 模型）：内存上不成立（§3）；它的真实收益 ——
  CPU/循环约束 —— 要求每个动作跨越进程/序列化边界，是一次与计划声明的
  非目标（无进程沙箱，见 `docs/vnext-convergence-plan.md` §5）冲突的
  结构性重写。基于量化依据暂不采纳；只有当出现带真实计算/足迹的
  第三方插件时，才重新审视这一选项。
- **配额 + 硬超时**（低一档）：针对 §2 中真实的故障域。缺口不是内存 ——
  而是*会话内的失控计算*。若推进，具体形态：一个硬性的每动作墙钟上限，
  把会话标记为中毒并回收它（用新会话取代僵尸），以及在 monitoring
  插件中呈现 CPU/墙钟记账。两者此处都未启动；这是要记录下来的
  决策，不是本轮的改动。
- **运行时资源声明**（research §6 候选 3）仍是廉价的声明式补充：
  注册插件*可以*触碰什么，在加载报告与审计中呈现，
  不做强制执行。

决策已记录在 git 历史（todo.md 40.10，重写前）：量化已完成；后续工作
（中毒会话回收 / 配额）保持待办，直到出现让该故障域变得真实的第三方
插件生态。
