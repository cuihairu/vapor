简体中文 | English

# Valve 游戏插件：现状与边界

本页精确回答一个问题：**哪些 Valve 游戏专属插件存在、哪些不存在，以及为什么**。简短版本：

- 插件**机制**（发现、清单、信任/权限、隔离加载、热安装）已经完备并与 ASF 对齐——见
  [插件开发](plugins.md)。本页没有任何内容被它阻塞。
- 官方插件**目录**刻意保持精简。目前绑定特定 Valve 游戏的插件有三个：`vapor.csgo`
  （CS:GO/CS2：干运行模拟器 + 只读背包记账 + 一个受门控的真实 game-coordinator
  开箱）、`vapor.dota2`（Dota 2：只读库存/交易列表 + 基于游戏无关交易基座的
  受门控单报价发送），以及 `vapor.game-data`（只读的单游戏分析：Dota 2
  比赛/英雄/物品数据与 TF2/CS2 物品 schema）。
  合成、battle-pass 与比赛内自动化对三者而言都在范围之外，其中一部分缺口是
  结构性的，不是待办积压。

## 游戏无关的基座已经覆盖大部分需求

运营者对 Valve 游戏做的几乎所有操作都与具体游戏无关，且已随 agent 的动作目录交付
（[动作目录](actions.md)）——下表中游戏访问相关的行自宿主/插件拆分后落在官方
`vapor.game-access` 插件里；名称与 payload 均未改变：

| 能力面 | 核心覆盖（对任何 Valve 游戏都适用） |
|---------|-------------------------------------------|
| 游戏时长 / 挂机 / boost | `idle`, `play_games`, `get_playtime`；`boost` 期望状态，支持多应用挂机 |
| 集换式卡牌 | `get_card_drops` + farm 期望状态（带轮转的卡牌掉落队列）——适用于有卡牌集的游戏（例如 Dota 2 有；CS:GO/CS2 从来没有） |
| 许可证与 Key | `add_license`, `redeem_key` |
| 成就 | `get_achievements`, `unlock_achievements`, `reset_achievements` |
| 库存与交易 | `get_inventory`, `find_duplicates`, `swap_duplicates`, trade offers, `loot_inventory`（+ §39 仓储角色收集编排）；经 `get_game_inventory` / `get_item_details` 做游戏经济聚合/估值 |
| 社区市场 | `get_my_market_listings`, `create_market_listing`, `cancel_market_listings`, `get_price` |
| 商店数据 | `get_game_info`（批量）、`search_games`、市场挂单——分层缓存 |
| 积分商店 | `get_points_shop_summary`, `claim_points_shop_items` |

只有当某款游戏具有该目录无法表达的机制时，专属单游戏插件才**值得**存在。
上面的一切今天就在 CS2、Dota 2、TF2 以及任何其他游戏上运行，完全不依赖任何
单游戏代码。

### 经济动作的数据从哪来（诚实边界）

`get_game_inventory` 和 `get_item_details` 是只读的，但它们的数据来源与本页
其他一切同样值得精确说明：

- **库存读取**走的仍是 `get_inventory` 一直使用的同一个 `steamcommunity.com`
  库存端点——一个未文档化的社区面，不是 key 门控的 Steam Web API。格式与可用性
  由 Valve 随时更改；分页做了防御性上限（50 000 件物品）。
- **物品价格**来自社区市场 `priceoverview` 端点——同样未文档化、无 key、
  限速，并按 Steam 自己的规矩应答（HTML 风格的本地化价格字符串，未挂单时返回
  `success:false`）。价格按原样上报，从不解析或换算。
- 这两个端点都不是 Steam Web API，都不受任何 Steam 担保覆盖，用 bot 集群猛打
  它们会带来 [production.md](production.md)（Account safety posture）所述的常规
  账号安全风险：这些动作始终处于 web 处理器的限速器与价格缓存层之后，且从不写入
  ——不交易、不挂市场单、不开箱。

## 为什么有些缺口无法靠插件补上

Valve 对 **game-coordinator 事务没有公开 API**——合成、
battle-pass/candy-works 交互、匹配。那些都是游戏客户端流量。通过脚本化
web 会话或 bot 集群来自动化它们，正是
[Steam 订阅协议 §4.C](https://store.steampowered.com/subscriber_agreement/)
所禁止的“自动化”（与 `vapor.csgo` 插件在其
[ToS 边界](plugins.md#tos-boundary) 中记录的是同一份分析）。这些是
**非目标**，不是待办工作：

- 任何游戏的 match making / 比赛内挂机自动化
- Dota 2 / TF2 的 battle pass、活动或合成自动化

开箱是这份非目标清单中唯一刻意的例外，而且范围很窄：`vapor.csgo` 提供一个
**受门控的**真实 game-coordinator 开箱——默认关闭（`open.enabled`），
每次请求一箱加一把钥匙，跑在运营者自己已登录的会话上，镜像运营者自己的
CS2 客户端执行的同一笔事务。它遵循的是运营者自己 Steam-bot 部署的开箱协议，
不是集群化模式；边界论证见
[该插件的 ToS 边界](plugins.md#tos-boundary)。

因此，诚实的单游戏插件面只有三种形态：

1. **干运行模拟器**，架在 Valve 公开的数据上（`vapor.csgo` 模式：对公开
   概率做本地 RNG，结果留档，零 Steam 调用）。
2. **只读分析**，架在 Valve 真实公开的 Steam Web API 面上（长期存在的
   key 门控接口，如 `IEconItems_*` 物品 schema 与
   `IDOTA2Match_570` / `IEconDOTA2_570` 比赛/英雄数据）。可用性与覆盖面
   由 Valve 随时决定；插件必须能优雅降级。
3. **编排粘合层**，架在上面的通用目录上（游戏专属预设、掉落窗口调度器），
   完全不需要任何新的 Steam 访问。

## 现有目录与候选

| Valve 游戏 | 专属官方插件 | 状态 | 说明 |
|-------------|---------------------------|--------|-------|
| CS:GO / CS2 | `vapor.csgo` | ✅ 已交付 | Valve 公开概率模拟、背包/台账记账，外加受门控的真实 GC 开箱（`open.enabled`，每次调用一箱加一钥匙） |
| CS:GO / CS2 | 掉落/游戏时长挂机 | ✅ 经通用目录 | `play_games`/`idle`/`boost`；无需插件 |
| CS:GO / CS2 | 库存/交易/市场工具 | ✅ 经通用目录 | 库存、重复项、交易报价、§39 仓储收集、市场动作 |
| CS:GO / CS2 | 物品 schema 摘要（只读，`IEconItems_730`） | ✅ 经 `vapor.game-data` 交付 | `econ_item_schema` appid 730——仅紧凑摘要；完整 schema 留在 Web API |
| Dota 2 | 只读比赛/经济分析（`IDOTA2Match_570` / `IEconDOTA2_570`） | ✅ 经 `vapor.game-data` 交付 | `dota2_match_history` / `dota2_heroes` / `dota2_game_items`；卡牌挂机与游戏时长本就经通用目录可用 |
| Dota 2 | 库存/交易工具 | ✅ 经 `vapor.dota2` 交付 | `dota2_inventory`（7 天冷却分类）、`dota2_trades`（Dota 范围的报价列表）、`dota2_send_trade`（受门控 `trade.enabled`，每次调用一个报价，100 件上限）；确认走游戏无关的 mobile-authenticator 动作 |
| TF2 | 只读 schema 摘要（`IEconItems_440`） | ✅ 经 `vapor.game-data` 交付 | 同一个 `econ_item_schema` 动作，appid 440 |
| 任意游戏 | 市场监视 / 价格告警 | ✅ `vapor.market-watch` | 面向整个经济，不专属单游戏 |

三个只读候选全部交付的插件只有一个：[`vapor.game-data`](plugins.md#game-data-plugin-vaporgame-data)：
只含动作，对 `api.steampowered.com` 发起 key 认证的 GET，密钥由 agent 侧
保管，以紧凑摘要代替原始载荷。其余单游戏候选（如果将来真的出现）会沿用同一形态。

## 未来的单游戏插件会是什么样

机制侧已经就绪——新增一个插件无需任何改动，且 `vapor.game-data` 现在是
继 `vapor.csgo` 之后的第二个完整示例：

- 按普通插件打包（[快速开始](plugins.md#quick-start)）；声明 `trust: official`
  并如实声明 `permissions`；official 信任级的插件必须精确钉定宿主的插件
  API 版本（ASF `HasSameVersion` 对齐）。
- 遵循 `vapor.csgo` 参考形态：动作经 `IActionPlugin`（可从任务与编排调用——
  这是可达的面，因为目前还没有宿主挂载 `IWebApiPlugin` 路由），配置经清单的
  `configuration` 映射加环境变量覆盖，而在将来某天可能存在真实集成的地方，
  只留一个如 `ICaseOpeningBackend` 的诚实后端接缝，默认实现为模拟/只读，
  初始化时对任何其余实现**拒绝**，而不是静默伪装。
- 把边界写进清单描述与文档：如果某项能力需要 game-coordinator 或脚本化客户端
  流量，它就是非目标——接缝把拒绝记录在案，而不是为它预留桩。
