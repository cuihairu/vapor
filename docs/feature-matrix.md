# Vapor 功能对标矩阵（2026-09-13）

> 方法:选取五个有代表性的同类产品,按能力域归一后逐项对照,识别差距并转化为可勾选的 P6 候选清单。
> 状态图例:✅ 有 / ⚠️ 部分 / ❌ 无 / ➖ 定位外不采用 / 📋 已立项待实施(见 `todo.md` 对应 P 阶段)。Vapor 列的现状以仓库代码与 `todo.md` 为准。

## 1. 取舍原则

矩阵对齐"能力域"而非逐功能复制。是否纳入 P6 按三条标准裁剪:

1. **契合定位**:Vapor 是多账号规模化运营平台(CP + Agent 舰队 + REST/插件/编排),不做单机工具箱功能(网络加速、本地账号切换、游戏内脚本)。
2. **GA 出口条件优先**(`todo.md` §0):登录、2FA 已闭环;**库存/交易、基础 farming 是明确未闭环的出口条件**,差距分析以此为最高权重。
3. **平台杠杆**:能复用既有编排/通知/插件/观测底座的功能优先(边际成本低、放大既有投资)。

## 2. 对标产品速览

| 产品 | 形态 | 账号模型 | 定位 |
|------|------|----------|------|
| [ArchiSteamFarm (ASF)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki) | C# 守护进程 | 多账号并发 bot | 卡牌挂机金标准 + 2FA + STM 换卡 + IPC/ASF-ui + 插件 |
| [Watt Toolkit (Steam++)](https://github.com/BeyondDimension/SteamTools) | 桌面/移动工具箱 | 本机多账号 | 令牌管理、账号切换、批量确认、网络加速(内嵌 ASF 挂卡) |
| [Steam Game Idler (SGI)](https://github.com/zevnda/steam-game-idler) | Tauri 桌面应用 | 多账号 | 两阶段卡牌引擎(32 并发)、成就解锁/管理、时长 boost、库存与市场挂单、免费游戏认领 |
| [steamguard-cli](https://github.com/dyc3/steamguard-cli)(SDA 系) | CLI | 单账号×多 maFile | 2FA 代码生成、交易/市场确认处理、maFile 互操作 |
| Idle Master Extended | 桌面应用 | 单账号 | 单账号挂卡,依赖浏览器 cookie 登录,已边缘化(仅作参照) |

## 3. 功能矩阵

### 3.1 登录与身份

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 凭证登录(含 access token 刷新) | ✅ | ✅ | ✅ | ✅ | ✅(BotSession + 令牌刷新) |
| 2FA 代码生成(TOTP) | ✅ | ✅ | ✅ | ✅ | ✅(SteamTotp + 时间同步) |
| 2FA 登录 challenge 自动应答 | ✅ | ✅ | ✅ | ✅ | ✅(TwoFactorAutoResponder,显式开启) |
| 交易/市场**确认**接受 | ✅ | ✅(批量) | ✅ | ✅ | ✅(mobile 确认闭环 + `confirm_all` 批量;identity secret 不出 agent) |
| QR 扫码登录 | ❌ | ✅ | ✅ | ✅ | ✅(login 任务 qr_login 触发,挑战 URL 上浮) |
| .maFile 导入互操作(SDA/steamguard-cli) | ✅(ASF 2FA) | ✅ | ➖ | ✅ | ✅(agent 本地 CLI `import-mafile`,兼容 SDA 嵌套/steamguard-cli 平铺) |
| 本地 Steam 客户端账号切换 | ➖ | ✅ | ✅ | ➖ | ➖(服务端架构不适用) |

### 3.2 挂机 / 农场

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 多账号并发挂机 | ✅ | ✅(内嵌 ASF) | ✅ | ➖ | ✅(Agent 舰队,天然并发) |
| **卡牌剩余掉落检测**(徽章页解析) | ✅ | ✅ | ✅ | ➖ | ✅(`get_card_drops` 徽章页解析) |
| smart farming 调度(掉完自动切换) | ✅ | ✅ | ✅(两阶段引擎) | ➖ | ✅(`farm` 期望状态,CP 编排掉完自动切换) |
| 挂机黑/白名单 | ✅ | ✅ | ✅ | ➖ | ✅(Idle 白名单 / Farm 排除名单) |
| 游戏时长 boost | ✅ | ➖ | ✅ | ➖ | ⚠️ play_games 同机制,无策略化 |

### 3.3 交易

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 交易报价读取 | ✅ | ✅ | ✅ | ✅ | ✅(`get_trade_offers` + `GET /v1/accounts/{name}/trade-offers`) |
| 报价接受 / 拒绝 | ✅ | ✅ | ✅ | ✅ | ✅(REST accept/decline,mobile 确认自动续派;默认人工,无编排器自动接受) |
| 报价发送(loot / 转移) | ✅ | ➖ | ➖ | ➖ | ✅(`loot_inventory` + `POST /v1/accounts/{name}/loot`) |
| 1:1 自动换卡(STM / TradeMatcher) | ✅ | ➖ | ➖ | ➖ | ✅(2026-09-13 `swap_duplicates`,严格双向互补配对 + dry_run 默认) |
| 批量确认 | ✅ | ✅ | ✅ | ✅ | ✅(`confirm_all_confirmations`,类型过滤 + allow/cancel) |

### 3.4 库存

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 库存读取 | ✅ | ✅ | ✅ | ✅ | ✅(`GET /v1/accounts/{name}/inventory`,多 app 扫描 + tradable/marketable 过滤) |
| 库存管理(API/UI/重复物清单) | ⚠️ | ✅ | ✅ | ➖ | ⚠️(重复物清单 API:`GET /v1/accounts/{name}/duplicates`;无 UI) |

### 3.5 市场

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 行情读取(app 详情/价格/搜索) | ⚠️ | ✅ | ✅ | ➖ | ✅(契约测试锁定的 Web 客户端 + 缓存) |
| 价格监控告警 | ➖ | ➖ | ✅ | ➖ | ✅(MarketWatch 插件 + webhook) |
| 挂单读取(自己的 mylistings) | ➖ | ➖ | ✅ | ➖ | ✅(`GET /accounts/{name}/market/listings`,P7-1,契约测试锁定解析) |
| 挂单创建(含费用感知定价) | ➖ | ➖ | ✅ | ➖ | ✅(`POST /accounts/{name}/market/listings`,P7-3,dry_run 默认+账户/agent 双开关+费用感知定价) |
| 挂单管理 / 批量撤单 | ➖ | ➖ | ✅ | ➖ | ✅(`POST /accounts/{name}/market/listings/cancel`,P7-2,dry_run 默认+无过滤实撤双层拒绝) |

### 3.6 自动化 / 编排

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| 任务系统(派发/重试/终态/output) | ⚠️(命令+定时) | ➖ | ✅(任务链) | ➖ | ✅(job/task + 周期任务 cron/interval) |
| 期望状态编排 / 节点丢失重平衡 | ❌ | ❌ | ❌ | ❌ | ✅(DesiredStateReconciler,**独有**) |
| 事件通知(webhook 等) | ⚠️(Steam 消息) | ➖ | ✅ | ➖ | ✅(HMAC webhook + 规则过滤) |
| 免费游戏提醒 + license 认领 | ✅(addlicense) | ➖ | ✅(提醒+自动认领) | ➖ | ✅(`add_license` + MarketWatch `kind=free` 边沿告警,提醒→认领闭环) |
| 积分商店认领 | ✅ | ➖ | ➖ | ➖ | ❌ |

### 3.7 平台 / 安全

| 能力 | ASF | Watt | SGI | steamguard-cli | **Vapor** |
|------|-----|------|-----|----------------|-----------|
| REST API | ✅(IPC) | ➖ | ➖ | ➖ | ✅(34 `/v1` 端点 + OpenAPI) |
| Web UI | ✅(ASF-ui) | ✅ | ✅ | ➖ | ✅(只读 dashboard + 管理面板,静态托管) |
| 插件系统 | ✅ | ✅ | ➖ | ➖ | ✅(ALC 隔离 + SemVer + 信任/权限) |
| 多节点舰队(CP 集中调度) | ❌(单进程) | ❌ | ❌ | ❌ | ✅(**独有**) |
| 可观测性(metrics/tracing/审计) | ⚠️ | ➖ | ➖ | ➖ | ✅(Prometheus + OTel + 审计) |
| 凭证加密存储 | ✅ | ✅ | ✅ | ✅ | ✅(AES + 密钥管理 + shared/identity secret 加密) |
| 凭证/验证码不外发(脱敏契约) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅(红线:验证码只发 bool,永不入通知/日志) |

## 4. 差距分析与 P6 建议

矩阵结论:Vapor 在**平台层(舰队编排/插件/观测/REST)是全场独有**,差距集中在 **Steam 功能纵深**——恰好压在 GA 出口条件 #2 的"库存/交易、基础 farming"上。以下按建议优先级排列(checkbox 供逐项核对,完成后勾选)。

> **2026-09-13 收口**:P6-1 / P6-2 / P6-3 全部落地,GA 出口条件 #2(库存/交易、基础 farming)闭环;剩余差异仅为明确后置(挂单创建 ToS 灰区、成就管理需求弱)与定位外不采用项。落地明细见 `todo.md` §11。
> **2026-09-14**:P7 立项(市场闭环——挂单读取/批量撤单/挂单创建),挂单两项自 P6-4 后置升级立项——"需库存/定价前置"已被 P6 补齐,ToS 灰区以分期 + dry_run 默认 + per-account 开关缓解;成就管理维持后置。见 `todo.md` §12。
> **2026-09-14**:P7-1 挂单读取落地(只读零风险增量)——`get_my_market_listings` + `GET /v1/accounts/{name}/market/listings`,mylistings 解析以构造 fixture + 三方互证契约测试锁定,供 P7-2 批量撤单取数。见 `todo.md` §12。
> **2026-09-14**:P7-2 批量撤单落地——`cancel_market_listings` + `POST /v1/accounts/{name}/market/listings/cancel`,过滤器(应用/名称/价格区间/挂龄)+逐条节奏+单项失败不中断如实汇总,dry_run 默认、无过滤实撤双层拒绝(CP 400 + action 独立拒绝),市场专用保守频控独立于交易限流预算。见 `todo.md` §12。
> **2026-09-14**:P7-3 挂单创建落地——`create_market_listing` + `POST /v1/accounts/{name}/market/listings` + `MarketFeeCalculator`(Steam 5%+发行商 10% 按卖方所得计费、买/卖两侧换算,契约与官方 economy_v2.js/market_multisell.js 四源互证),send 缺省 dry run 只出定价方案,实撤需账户 `marketListingsEnabled` + agent `AGENT_MARKET_LISTINGS_ENABLED` 双开关;价格仅来自调用方,market 类确认复用既有闭环。P7 市场闭环三期全部完成。见 `todo.md` §12。

### P6-1 卡牌 farming 闭环(GA 出口条件,平台杠杆最大)✅ 全部完成

- [x] **徽章页解析**:拉取玩家徽章/掉落页,得出"哪些 app 有剩余卡牌掉落、剩余几张"(✅ 2026-09-12 `SteamBadgesClient` + `get_card_drops`,SWR 缓存,fixture 三方解析器互证)。
- [x] **smart farming 调度**:剩余掉落 > 0 的 app 进入 idle 队列,掉完自动切下一个;与账户编排 DesiredState 整合(✅ 2026-09-12 `Farm` 期望状态:编排器周期派发查询→构建 FarmQueue→`play_games` 挂队首,队首掉完自动切换;spec 变更重置队列)。
- [x] **挂机排除名单**:IdleApps 补充排除语义(黑名单),对齐 ASF `Blacklist`(✅ 2026-09-12 Farm 模式下 IdleApps 为排除名单,Idle 模式仍为白名单)。

### P6-2 交易与确认闭环(GA 出口条件,安全底座已备)✅ 全部完成

- [x] **交易报价读取**:拉取 incoming/outgoing 报价列表入 CP(REST 化),复用既有脱敏与审计(✅ 2026-09-12 `get_trade_offers` + `GET /v1/accounts/{name}/trade-offers` 同步端点,三态 200/202/502)。
- [x] **报价接受/拒绝**:基于 MobileAuthenticator 既有确认哈希/响应能力;**自动接受必须按账户显式策略开启**(✅ 2026-09-12/13 人工路径 REST accept/decline + accept 后 mobile 确认自动续派;编排器侧自动接受经评估不实现,决策记录见 todo §11.2)。
- [x] **批量确认 action**:交易/市场确认批量处理(对标 Watt 批量确认)(✅ 2026-09-13 `confirm_all_confirmations` + CP `/confirmations/accept-all`,类型过滤 + allow/cancel)。
- [x] **报价发送(loot)**:向指定好友转移库存(✅ 2026-09-13 `loot_inventory` + CP `/loot`,库存扫描→可交易过滤→限流发送→mobile 确认续派)。
- [x] **1:1 换卡(STM/TradeMatcher 等价)**:已落地(2026-09-13,`find_duplicates` + `swap_duplicates` + CP duplicates/swap-offers 端点,详见 todo §11.2)。

### P6-3 互操作与认领(降低迁移/使用成本)✅ 全部完成

- [x] **.maFile 导入**:支持 SDA/steamguard-cli 的 maFile(解析 shared_secret/identity_secret/设备 ID,入库走既有加密存储)——存量 2FA 用户零成本迁移(✅ 2026-09-13 `Vapor.Agent import-mafile` 本地 CLI 导入,maFile 内容不落 CP 任务记录)。
- [x] **免费 license 认领**:addlicense 等价 action(sub/add 页解析);免费游戏提醒由 MarketWatch 扩展 watch 类型(✅ 2026-09-13 `add_license`(app 走协议、sub 走商店 checkout)+ MarketWatch `kind=free` 边沿告警,组成"提醒→认领"闭环)。
- [x] **库存 REST 化**:GetInventoryAction 深化为 `/v1/accounts/{name}/inventory`(按 app/类型过滤),为交易/市场功能供数(✅ 2026-09-13 多 app 扫描 + tradable_only/marketable_only 过滤,steam_id 从会话 cookie 反解)。

### P6-4 竞品对齐但后置(记录待决,不承诺)

- [x] Web Dashboard 只读面板(对标 ASF-ui 只读部分):`wwwroot/dashboard.html` 落地——统计卡/账户/Agent/会话/作业/审计 + 双 SSE 流 + 轮询兜底,零写操作(契约测试守护);管理功能走既有 admin.html。功能扩展(向导式配置等)后置。
- [x] 挂单创建/批量撤单(对标 SGI):ToS 灰区 + 需要库存/定价前置,后置。(2026-09-14 前置条件经 P6 补齐,立项为 P7 市场闭环,见 `todo.md` §12)
- [x] QR 扫码登录(对标 SGI/steamguard-cli):SteamKit2 QR 挑战 + 轮询落地;挑战 URL 经 session 事件上浮供人扫码,request_key 与 token 均不出 agent;管理面暂用 POST /v1/jobs 触发(UI 按钮后置)。
- [ ] 成就解锁/管理(对标 SGI):需求弱,后置。

### 明确不采用(定位外)

- 网络加速(Watt 品类不同)、本地账号切换(客户端概念)、通用 TOTP 保险箱(偏离核心)、游戏内脚本/成就数值编辑(高风险灰区)。

## 5. 参考资料

- [ASF Wiki](https://github.com/JustArchiNET/ArchiSteamFarm/wiki) / [ASF FAQ](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/faq)
- [Watt Toolkit 使用指南](https://xtsat.github.io/SteamTools-Guide/) / [Microsoft Store 页](https://apps.microsoft.com/detail/9mtcfhs560ng)
- [Steam Game Idler](https://github.com/zevnda/steam-game-idler) / [作者设计文章](https://dev.to/zevnda/how-i-built-an-open-source-alternative-to-archisteamfarm-and-why-you-should-care-37od)
- [steamguard-cli](https://github.com/dyc3/steamguard-cli) / [Idle Master Extended](https://github.com/jonas-med-ett-s/idle_master_extended)
