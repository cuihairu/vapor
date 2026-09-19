# Vapor 测试概览

本文档提供 Vapor 项目的完整测试概览。

## 测试项目结构

`tests/` 下 9 个测试项目(外加 2 个测试基建程序集:示例插件与故障 fixture 库):

```
tests/
├── Vapor.Steam.Core.Tests/               (1224 tests)
│   ├── Unit/                             动作/会话/交易/安全/数据/Web 客户端
│   ├── Integration/                      会话工作流 + Redis 缓存(门控)
│   └── Performance/                      并发与压力
├── Vapor.ControlPlane.Tests/             (603 tests)
│   └── Performance/                      队列吞吐/派发/SSE 扇出/时延/资源占用基准
├── Vapor.Plugins.Core.Tests/             (128 tests)
├── Vapor.Plugins.MobileAuthenticator.Tests/ (126 tests)
├── Vapor.Agent.Tests/                    (54 tests)
├── Vapor.Plugins.MarketWatch.Tests/      (56 tests)
├── Vapor.Plugins.Monitoring.Tests/       (35 tests)
├── Vapor.Protocol.Tests/                 (43 tests)
├── Vapor.E2E.Tests/                      (11 tests,真实双进程)
├── Vapor.Plugins.TestPlugin/             插件基础设施测试用示例插件
└── Vapor.Plugins.TestFixtures/           故障 fixture 库(故意坏实现/多实现类,供失败路径测试)
```

## 测试统计

| 测试项目 | 数量 | 覆盖范围 |
|----------|------|----------|
| Vapor.Steam.Core.Tests | 1236 | 动作、会话状态机、交易校验、凭据/加密(含轮换器)、maFile 解析、数据缓存(Redis mock 离线全覆盖)、Steam Web 客户端 + 契约回放、徽章页解析、报价列表、loot、addlicense、库存多 app 扫描、重复卡分析与 1:1 换卡匹配、QR 扫码登录会话流、市场挂单创建/撤单与手续费、积分商店、games tab 播放时间数据源、成就列表(社区页解析)与解锁/重置(client stats 协议位图数学/载荷门控/逐条结果/协议 records 契约)、auth token 反射桥、payload 值形状与分支加固、熔断器/限流器边界 |
| Vapor.ControlPlane.Tests | 604 | REST API、SQLite job/审计/抓取存储、任务派发、账户编排(boost/trade 策略,§35 编排守卫/审计隔离/payload 解析/结算回读深化,§36 trade 策略规范化 property 测试)、周期任务、通知、追踪 + WS 协议回放、报价查询/接受/拒绝/批量确认/loot/免费认领/库存读取/重复查询/换卡报价、数据抓取计划/执行/分片、静态面板契约(含 admin 写操作确认锚)、坏 JSON 边界、QR 挑战归类、Program 分支加固、Bearer 鉴权解析 |
| Vapor.Plugins.Core.Tests | 129 | 插件发现/清单/SemVer 兼容/加载/卸载/ALC 回收/事件分发/配置/信任与权限/故障 fixture 库 |
| Vapor.Plugins.MobileAuthenticator.Tests | 126 | TOTP、确认哈希、移动交易确认(单个/批量)、shared/identity secret 持久化、报价确认闭环、插件宿主实战加载 + 动作边界(payload 形状/失败语义/冷却)与确认客户端解析分支 |
| Vapor.Agent.Tests | 54 | 重连退避策略、任务执行器(含 QR 登录与 password+refreshToken 组合 payload)、WS URI 构造、maFile 离线导入 CLI、追踪注入 |
| Vapor.Plugins.MarketWatch.Tests | 56 | watch 存储/阈值评估/free watch 边沿告警/轮询告警与 webhook(含传输崩溃与取消路径)/轮询循环确定性停机/阈值 payload 值形状/插件宿主实战加载 |
| Vapor.Plugins.Monitoring.Tests | 35 | 指标注册表/HTTP 指标服务/插件生命周期 |
| Vapor.Protocol.Tests | 43 | JsonDefaults 序列化契约(camelCase/枚举字符串/null 省略/前向兼容)+ 全部协议模型逐字段往返 + record 边界(畸形 JSON/缺字段/默认值)+ FsCheck property 往返(任意字段值的心跳/取消/错误/握手模型恒等) |
| Vapor.E2E.Tests | 11 | 真实双进程闭环:CP 进程 + Agent 子进程(job 派发、任务回报、SSE、账户编排重平衡、静态页守护) |
| Vapor.KeyRotation.Tests | 27 | 凭据轮换 CLI 壳:参数解析(缺失/未知/help 双旗/dry-run)、key spec 四格式全臂、退出码契约(0/1/2)、真实旋转三态(dry-run 不落盘/applied+备份+新钥可解/aborted+FAILED 上报)、损坏 store 异常路径 |
| **合计** | **2321** | (2026-09-19 基线;另 E2E 以真实子进程覆盖 Agent 主循环,单测统计测不到) |

> 基线刷新方式(用 TRX 精确计数;`--list-tests` 会在终端宽度处折行长 theory 名,grep 计数会漏掉折行的用例):
> ```bash
> find tests -name "*.trx" -delete
> dotnet test Vapor.sln -c Release --logger "trx;LogFileName=count.trx"
> for f in $(find tests -name count.trx); do echo "== $f"; grep -o '<UnitTestResult [^>]*?testName="[^"]*"' "$f" | sed 's/.*testName="//;s/(.*//' | awk -F. '{print $(NF-1)}' | sort | uniq -c | sort -rn; done
> ```

## 测试分类

### Steam.Core(1236 个测试)

#### 动作(Actions)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| RedeemKeyActionTests | 33 | Key 激活(含遮罩与边界/null 响应/良性码/不可重试与瞬态错误重试/可选字段透出) |
| IdleActionTests | 23 | 空闲动作 |
| PlayGamesPayloadParserTests | 12 | 输入规范化(`123`/`123,456`/`id/123`) |
| PlayGamesActionTests | 7 | 挂机游玩 |
| DataActionsTests | 34 | 数据动作(游戏信息/价格/市场/搜索 + 无 web handler/坏 app_id/商店客户端工厂故障) |
| EchoActionTests / PingActionTests | 27 | 回显/心跳 |
| ActionRegistryTests + ActionRegistryExecutionObserverTests | 22 | 注册表(18 + 执行观察者 4) |
| SendTradeOffer / AcceptTradeOffer / DeclineTradeOffer / CancelTradeOffer ActionTests | 12 | 交易动作(4/4/2/2) |
| LootInventoryActionTests | 20 | loot(可交易过滤/默认社区 app 753-6/app_ids 覆盖与 game context 2/分页/mobile 确认标志/空库存/失败语义/上限/值形状/限流/无登录态) |
| FindDuplicatesActionTests | 17 | 重复卡分析(keep 语义与非法值/untradable 不计/excess asset ids/空结果成功/库存失败/无 SteamID/无 web handler/上限/值形状) |
| SwapDuplicatesActionTests | 23 | 1:1 换卡报价(默认 dry_run 不发送/send=true 对称报价与 mobile 标志/自换拒绝/无互补失败/对方库存失败标明 partner 侧/双侧分页/参数范围/trade_url/失败语义/限流/值形状) |
| GetInventoryActionTests + GetInventoryActionBranchTests | 21 | 库存读取(12 + 9:steam_id 缺省 cookie 反解/app_ids 多 app 扫描 + loot context 规则/tradable 与 marketable 过滤/上限/JSON 往返/单 app 旧输出兼容/坏 steamid/cookie 无身份/classic 路径覆盖/分页去重/值形状) |
| GetCardDropsActionTests | 13 | 卡牌剩余掉落查询(排序/错误/steam_id 缺省 cookie 反解/缓存 SWR/force_refresh/名称与元数据/默认真实客户端装配) |
| GetTradeOffersActionTests | 8 | 报价列表读取(active_only 透传/输出映射/失败语义) |
| AddLicenseActionTests | 18 | 免费 license 认领(app 走 client 协议/sub 走商店 checkout/已拥有视为成功/双 ID 混合/去重/JSON 往返 payload/边界/混合值形状) |
| GetGameInfoBatchActionTests | 26 | 批量应用详情(≤200 app/批、缓存层共享、List 混合数值解析、空列表 400、工厂异常面) |
| LoginActionTests | 17 | 登录动作 |
| TradeOfferActionExecutionTests | 40 | 交易动作执行流(fake trade client 全分支) |
| MarketFeeCalculatorTests | 11 | 市场手续费计算(买家/卖家定价换算) |
| MarketFeeCalculatorPropertyTests | 5 | FsCheck property:手续费数学(拆分恒等 Buyer=Seller+费+费/费 ≥1/20 分起比例精确/买家价严格单调)+ FromBuyerPrice 极大性(落点 ≤ 目标且 seller+1 必超——**抓获 walker 起点低估 3 分的系统缺陷**)+ 域二分完备(3 分以下 null 以上必有)+ 上限守卫(AOORE 不 wrap——**int 乘法溢出边界**) |
| GetMyMarketListingsActionTests | 7 | 账户自己的市场挂单(start/count 分页) |
| CreateMarketListingActionTests | 21 | 创建挂单(参数校验/默认干跑/EmailConfirmation 域上报/amount 与 buyer 下限) |
| CancelMarketListingsActionTests | 16 | 批量撤单(负价格区间/pacing 间隔/取消透传/min_price 过滤) |
| GetPointsShopSummaryActionTests | 10 | 积分余额与奖励定义查询(definition_ids/free_only) |
| ClaimPointsShopItemsActionTests | 13 | 兑换积分奖励(metadata 契约/force 语义/lookup 失败前置短路) |
| GetAchievementsActionTests | 10 | 成就列表读取(社区页解析/steam_id 缺省 cookie 反解/显式传参与 cookie 缺失/失败语义/internal 工厂元数据/无 webHandler/非数字 steam_id) |
| UnlockAchievementsActionTests / ResetAchievementsActionTests | 18 | 成就解锁/重置(位图载荷门控/显式 names 双格式与不可用形状/reset 双 confirm/transport 抛异常与无响应/无 client/逐条结果与 verified 透传) |

#### 会话与核心组件
| 测试类 | 数量 | 说明 |
|--------|------|------|
| BotSessionTests | 27 | 会话状态机 |
| BotSessionQrLoginTests | 7 | QR 扫码登录会话流(批准后 refresh token 走 token 登录、挑战 URL 上浮与轮转重发、拒绝/超时/连接失败映射、stub 模式) |
| BotSessionBranchTests | 14 | 会话分支加固(命令循环崩溃/action 取消与超时 unwind/QR park 信号取消/QR 事件上浮/懒启动) |
| SessionManagerTests | 37 | 会话管理器(恢复回调/事件订阅/无凭证库/重复恢复恢复同一会话/登录失败清会话/后台 token 刷新跳过与故障吸收) |
| SteamClientManagerTests | 22 | Steam 客户端管理器 |
| SteamTransportContractTests | 14 | 传输层契约(SteamResult 线上编码镜像 + 接口可替换性) |
| ModelsTests | 41 | 数据模型和枚举 |
| EdgeCaseTests | 23 | 边界和异常场景 |
| SessionWorkflowTests(集成) | 19 | 完整工作流(含事件订阅确定性投递:stub 登录+断开双 StateChanged) |
| ConcurrencyTests(性能) | 9 | 并发和压力 |
| CacheBenchmarks(性能) | 2 | 缓存吞吐基准(打印数字为准,见 docs/performance.md) |
| TwoFactorAutoResponderTests | 7 | 2FA 自动应答(本地 TOTP 闭环 + 无活跃会话跳过) |
| TokenRefreshTests / LoginFlowTests / RedeemKeyFlowTests | 5 | 认证与激活流程 |

#### 交易安全层(Trade Safety)
| 测试类 | 数量 | 说明 |
|--------|------|------|
| TradeOfferStateMachineTests | 35 | 报价状态机 |
| TradeActionValidationTests | 15 | 交易动作校验 |
| TradeAssetValidatorTests | 13 | 资产校验 |
| TradeRateLimiterTests | 16 | 频控(窗口/并发槽/超时/租约与销毁交互) |
| CardSwapMatcherTests | 9 | 重复分组与 1:1 互补匹配(keep/excess 只取可交易/排序确定性/双向互补条件/单向不配/maxSwaps 截断/context 规则) |
| TradeUrlParamsTests / TradeUrlParamsExtendedTests | 7 | 报价 URL 参数 |
| TradeParsingPropertyTests | 7 | FsCheck property:报价 URL 解析(任意输入不抛且解析成功必 ≥ base/32 位形态加 base/64 位形态直通/token URL 编码往返,lone surrogate 过滤)与状态机谓词(无过期时间永不过期/CanAccept⟹CanDecline+收到的 Active/sender 自匹配合规域恒过——抓出 AccountIdOther 次规范形态被 ToAccountId 折 0 的防御语义边界) |
| CardSwapMatcherPropertyTests | 5 | FsCheck property:重复分组不变量(尺寸账目 TotalTradable-keep/只收可交易/组按 identity 排序)与 1:1 匹配(严格互补双向 DoesNotContain/双侧来自 excess 池/context 规则/maxSwaps 上限)+ 两确定性(逐字段比较——record 集合字段是引用比较)+ ContextIdFor 全域(753→6 其余→2);生成器 id 缩域保证碰撞(assetId 保持全域) |

#### 安全与凭据
| 测试类 | 数量 | 说明 |
|--------|------|------|
| FileCredentialStoreTests | 27 | 凭据存储(加密落盘/备份恢复/权限收紧含 symlink EPERM 降级/shared/identity secret/新版本拒载/缺 accounts 拒载) |
| MaFileParserTests | 13 | maFile 解析(SDA 嵌套/steamguard-cli 平铺/密码加密 PBKDF2+AES-CBC/无密码与错密码/无 secret/账户键回退/根数组与坏 payload 拒绝) |
| VaporCryptoHelper(Encryption)Tests + VaporCryptoHelperMethodTests + VaporCryptoHelperTests | 39 | AES-GCM 加密助手(12+23+4:往返/篡改/边界 + 方法级分支/文件密钥回退含 base64 过短回退 raw/不可读文件降级) |
| VaporCryptoRoundTripPropertyTests | 3 | FsCheck property:AES-GCM 任意明文×任意密钥往返恒等、错钥认证失败(NPE 永不静默回原文)、随机 nonce 密文互异且均可解 |
| CredentialStoreRotatorTests | 5 | 密钥轮换 |
| RedactingLoggerProviderTests / SensitiveDataRedactorTests | 17 | 日志脱敏(12+5:嵌套异常/结构化 scope/非泛型枚举臂/计数与索引器/空值归一) |
| AgentReconnectPolicyTests | 4 | Agent 重连策略 |

#### 数据与 Web 客户端
| 测试类 | 数量 | 说明 |
|--------|------|------|
| MemoryVaporCacheTests | 22 | 内存缓存(TTL/SWR/单飞行去重) |
| RedisCacheEntryTests | 11 | Redis 信封编解码/新鲜度判定(纯逻辑,无需 Redis) |
| RedisVaporCacheTests | 36 | Redis 缓存全路径离线覆盖(mock IConnectionMultiplexer/IDatabase:信封协议/SWR 跨实例锁/索引维护/SCAN 前缀清理含全端点断连回退/单飞行共享) |
| RedisVaporCacheIntegrationTests(集成,门控) | 11 | Redis 端到端(需 `VAPOR_TEST_REDIS`) |
| SteamStoreApiClientTests | 32 | 商店 API 客户端(解析/降级与畸形响应/market 新旧双契约回退) + addlicense 结账端点(detail 解析/已拥有/HTTP 失败) |
| SteamStoreApiContractTests | 4 | 录制响应契约回放(appdetails/storesearch/market render 新旧双契约,fixture 见 `TestData/`) |
| SteamMarketClientTests | 23 | 社区市场客户端(挂单列表新旧契约/currencyid 字符串臂/非标准 JSON 值类型防御/空响应体) |
| SteamMarketMyListingsContractTests | 4 | 我的挂单契约回放(数值字段 JSON 字符串形状) |
| SteamBadgesClientTests | 14 | 徽章页解析变体(appid 双载体/掉落文案/分页/失败语义) |
| SteamBadgesPageContractTests | 3 | 徽章页 HTML 契约回放(fixture 为三方解析器互证构造,登录门控不可匿名录制) |
| SteamWebHandlerResilienceTests | 8 | 429/5xx 退避重试与熔断 |
| SteamWebHandlerRequestTests | 17 | 请求构造(cookie/自定义 header/分主机 Referer+Origin/UA/限流窗延迟/重试耗尽语义/无与不可解析 Retry-After 回退/Dispose 幂等/SteamWebResponse 分类属性) |
| HttpCircuitBreakerTests | 8 | 熔断器状态机(Closed/Open/HalfOpen 转换与单探测) |
| WebRequestMetricsTests | 3 | 请求指标采集 |
| SteamCacheTtlTests | 2 | 分级 TTL 新鲜度 |
| GameModelsTests | 3 | 游戏数据模型 |
| PayloadReaderTests / TradeModelsEdgeTests | 25 | payload 读取器方法级分支(17) + 交易模型边界(8:TradeUrlParams/TradeAsset 等值形状与非法输入) |

#### Steam 认证
| 测试类 | 数量 | 说明 |
|--------|------|------|
| SteamTotpTests | 14 | Steam TOTP(本地 2FA 码生成) |
| SteamTimeSynchronizerTests | 9 | Steam 服务器时间同步 |

### ControlPlane(603 个测试)

| 测试类 | 数量 | 说明 |
|--------|------|------|
| ProgramBranchCoverageTests | 73 | Program 组装层分支(配置解析/环境变量回退/装配路径逐支驱动) |
| AccountApiTests | 93 | `/v1/accounts` REST(含 farm 状态、报价查询/接受/拒绝、自动确认、批量移动确认、loot 同步端点、免费 license 认领、库存读取、挂单创建/撤单、积分兑换、重复查询/换卡报价:fake agent 顺序回报;五端点 202/502 三态与 claim 校验) |
| DesiredStateReconcilerTests | 78 | 账户编排(登录派发/退避/节流/重平衡/dry-run/smart farming 调度 + 循环存活/无 agent/在途窗口/形状怪癖执行路径加固/unassign 存储故障逃逸 + §35 派发守卫(agent 能力缺失与 dry-run)/审计隔离(异常吞咽与 OCE 传播)/payload 多类型解析防御/结算回读防御(空 Tasks/失败 deviation/confirm 链)) |
| TradePolicyPropertyTests | 8 | FsCheck property:trade 策略白名单(零剔除/去重/升序/输入序无关/幂等/全零 auto-accept 必拒)与 payload 数值读取(任意 boxed 值不抛/false 置零/JSON 数字臂往返);抓出并修复 NaN/∞ 透传边界(string 臂 `TryParse("NaN")` 为 true、boxed double 臂不滤非有限) |
| CrawlApiTests | 40 | 数据抓取 REST(计划 CRUD/PUT merge 语义/触发/分片领取/行回写/claim 校验与去重) |
| CrawlRunWorkerTests | 27 | 抓取执行 worker(读取/派发取消传播/GetJob 故障吞咽/坏 tick 兜底/停机竞态双路径/审计故障不阻断/混合列表输出解析) |
| SqliteJobStoreTests | 25 | job 存储(并发/迁移/周期模板) |
| AccountStoreTests | 23 | 账户存储(ConfigVersion 并发/空名校验) |
| AccountTaskRunnerTests | 2 | 账户任务运行器(TaskRunResult record 合成成员/克隆等值) |
| NotificationTests | 20 | 通知规则/webhook 签名/派发隔离/无 sink 快速返回/有限流 broker 三泵自然排空 |
| ScheduleClockTests | 16 | 周期计划时钟(interval/cron/触发点计数) |
| SqliteCrawlStoreTests | 15 | 抓取存储(守卫/同事务防御性 CAS) |
| RecurringJobSchedulerTests + RetireTests | 14 | 周期任务触发/missed/overlap/退役(10+4) |
| ControlPlaneApiTests | 13 | REST API(鉴权/任务/SSE/计划 job、QR 挑战归类与 URL 透传、坏 JSON 体 400 边界) |
| DashboardStaticTests | 9 | 静态面板(/dashboard.html 服务、无写动词契约、三视图互链、`/` 302 重定向、gamedata 五模型文档(含 ItemInfo 不回流守卫)、admin QR 按钮契约) |
| CrawlShardPlannerTests | 10 | 抓取分片规划(空池告警/overrides) |
| TaskSchedulerServiceTests | 8 | 任务派发/终态机制/无 listener 惰性分发 |
| SqliteAuditStoreTests | 8 | 审计存储 |
| EventBrokerTests | 8 | 事件总线 |
| AuthTests | 8 | Bearer 鉴权解析(合法/缺 scheme/非 Bearer/空 header/空配置键 fail-closed) |
| WsProtocolReplayTests | 7 | WS 隧道协议录制回放(5 类帧快照 roundtrip + 会话序列路由 + 前向兼容) |
| AgentRegistryTests | 7 | Agent 注册表 |
| AuditApiTests | 6 | 审计查询 API |
| TracingTests | 5 | OpenTelemetry 追踪注入(无 listener 惰性分发/双 null 臂) |
| ControlPlaneBenchmarks(性能) | 4 | 吞吐/派发/扇出基准 |
| ConfigStoreTests | 3 | 配置存储 |
| AuthChallengeTrackerTests | 3 | 挑战追踪 |
| SessionTrackerTests | 2 | 会话追踪 |
| CompositionRootSmokeTests | 2 | 组装根冒烟 |
| ApiLatencyBenchmarks(性能) | 7 | 只读端点与任务创建入口时延基准 |
| ResourceFootprintBenchmarks(性能) | 1 | 每操作托管分配量基准 |

### 插件体系(345 个测试)

- **Plugins.Core(128)**:清单解析(13)、发现(5)、加载(11)+加载器(8)、卸载与 ALC 回收(9,含 DisposeAsync 卸载自身抛错隔离)、信任与权限(18)、事件分发(12)、配置扩展(20)、能力(7)、插件 API 与 SemVer 兼容(18,含 TryParseVersion theory 展开)、管理器并发(1)、故障 fixture 库(6)
- **MobileAuthenticator(126)**:动作含 save_shared_secret/save_identity_secret/confirm_trade_offer/confirm_all_confirmations(41)+ 动作边界:payload 形状/失败语义/冷却与并发(44)、确认客户端解析含 type 归一化(10)+ 客户端会话分支(17)、解析分支(10)、确认哈希(8)、设备 ID(3)、插件加载与 9-action 断言(3)、shared/identity secret 存储行为(7,位于 Steam.Core 的 FileCredentialStoreTests)
- **MarketWatch(56)**:watch 存储(18)/阈值评估/三个 watch action(kind=price/free)/轮询告警与 webhook(free_game_alert 与 price_alert/传输崩溃吞并/周期中取消干净停机/手动驱动前 drain-and-stop 测试钩子)/单 app 抓取失败隔离/阈值 payload 值形状/无参构造 Info/插件宿主实战加载(2)
- **Monitoring(35)**:指标注册表(9)/HTTP 服务(16)/插件生命周期(10)

### Agent(54 个测试)

重连退避策略(25)、任务执行器(15,含 QR 登录与 password+refreshToken 组合 payload 解析与优先级)、maFile 离线导入 CLI(9,含幽灵路径点名)、WS URI 构造(4)、追踪注入(1)

### E2E(11 个测试)

真实双进程:`E2EStack` 启动真实 ControlPlane 进程 + Agent 子进程(独立 HOME、WS 隧道、SQLite)。分类:ControlPlaneAgentE2ETests(5,健康检查/job 全链路/任务取消/SSE 事件流/2FA 挑战人工提交)、AccountOrchestrationE2ETests(1,账户声明→自动分配→kill agent→重平衡)、StaticPagesE2ETests(5,静态页守护:E2E 的 CP 进程是"bin dll + 测试器 cwd"的裸部署形态,守护 admin/dashboard/gamedata/favicon 匿名 200 与 `/`→admin UI 落地——曾因 Web SDK 只在 publish 复制 wwwroot + WebRoot 锚 cwd 导致部署态全 404 而全测试套件无一页面请求)。

## 运行测试

> 测试目标框架为 `net10.0`（.NET SDK 10.x）。`./scripts/run-tests.sh` / `.\scripts\run-tests.ps1` 已默认设置 `DOTNET_ROLL_FORWARD=Major`。

### 基本命令

```bash
# 运行所有测试
dotnet test

# 运行特定测试项目
dotnet test tests/Vapor.Steam.Core.Tests/Vapor.Steam.Core.Tests.csproj

# 运行特定测试类
dotnet test --filter "FullyQualifiedName~PingActionTests"

# 运行特定测试方法
dotnet test --filter "FullyQualifiedName~ExecuteAsync_ReturnsSuccess"
```

### 按测试类别运行

```bash
# 只运行单元测试
dotnet test --filter "FullyQualifiedName~Unit"

# 只运行集成测试
dotnet test --filter "FullyQualifiedName~Integration"

# 跑 Redis 集成测试（需本地或容器 Redis；未设 VAPOR_TEST_REDIS 时自动跳过）
VAPOR_TEST_REDIS=localhost:6379 dotnet test --filter "FullyQualifiedName~RedisVaporCacheIntegrationTests"

# 只运行性能测试
dotnet test --filter "FullyQualifiedName~Performance"

# 运行特定类型的测试
dotnet test --filter "FullyQualifiedName~PingActionTests"
dotnet test --filter "FullyQualifiedName~SessionWorkflowTests"
dotnet test --filter "FullyQualifiedName~ConcurrencyTests"
```

### 使用测试脚本

```bash
# Linux/macOS
./scripts/run-tests.sh                    # 运行所有测试
./scripts/run-tests.sh -c                # 带覆盖率报告
./scripts/run-tests.sh -v                # 详细输出
./scripts/run-tests.sh -f "PingAction"   # 过滤测试

# Windows PowerShell
.\scripts\run-tests.ps1
.\scripts\run-tests.ps1 -Coverage
.\scripts\run-tests.ps1 -Verbose
```

> 注意:全量跑请用上面的脚本而不是裸 `dotnet test Vapor.sln`——后者九个测试项目并行执行,`ControlPlaneBenchmarks.SseEndpoint_ConcurrentConnections_AllReceiveEvents` 等 60s 预算的基准会因 CPU 争抢超时假红(2026-09-16 实测:并行全跑该基准超时,单独重跑项目 39s 全绿)。

### 生成代码覆盖率报告

```bash
# 推荐（整个解决方案，coverlet.collector，输出到 */TestResults/*/coverage.cobertura.xml）
./scripts/run-tests.sh -c

# HTML 报告（可选，需要 ReportGenerator）
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/TestResults/*/coverage.cobertura.xml -targetdir:./TestResults/coveragereport
```

## 代码覆盖率

9 个测试项目统一接入 coverlet.collector；`run-tests.sh -c` 在收集前清理历史残留报告（清理必须在测试之前——测试结束后这些路径上的文件就是本次结果），覆盖整个解决方案。

### 当前基线（2026-09-19，行覆盖 99.7%）

合并全部报告计算：`./scripts/coverage-summary.py`（按程序集归一化文件路径后，以 (程序集, 文件, 行) 去重取最大命中）：

| 程序集 | 行覆盖 |
|--------|--------|
| Agent | 100.0% |
| MobileAuthenticator | 100.0% |
| Monitoring | 99.4%（`MetricsHttpServer` 时序边沿 2 行，本轮未命中） |
| Plugins.Core | 100.0% |
| Plugins.TestFixtures | 100.0%（故障 fixture 库，已由 TestFixturesTests 全覆盖） |
| Plugins.TestPlugin | 100.0%（示例插件，fixture 程序集） |
| Protocol | 100.0% |
| ControlPlane | 99.8% |
| Steam.Core | 99.7% |
| MarketWatch | 98.9% |
| KeyRotation | 98.1%（CLI 壳全覆盖；`GetValue` 缺值臂 `Environment.Exit(2)` 2 行——测试进程内会终止 testhost，结构性不可测） |
| **合计** | **99.7%** (14543/14581) |

> 历史基线：2026-09-12 首次真实全解决方案基线为 74.3%（此前 44.6% 的初版系统性偏低：不同 testhost 生成的报告里同一源文件的 `filename` 前缀写法不一致，合并未归一化导致同一行被重复计入分母）。2026-09-13 覆盖率冲刺（逐文件提取未覆盖行并针对性补测）后达 95.9%。2026-09-14 第二轮冲刺后达 98.6%（Agent 91.9%→98.1%、ControlPlane 97.9%→99.5%、Plugins.Core 88.1%→99.1%、Monitoring 89.7%→97.2%）；同日第二轮半（fd1ae2e，+36 测试）删除第二轮归档的死代码（`VaporCryptoHelper` 防御 catch、`HttpCircuitBreaker` HalfOpen 存储态、`RecurringJobScheduler` missed 组合、`SteamTotp` 空 base64、`RedactingLoggerProvider.AppendPairs`）并新增 TracingTests/TestFixturesTests/SteamTimeSynchronizerTests，TestFixtures 故障 fixture 库亦获全覆盖，TestFixtures 68.3%→100%、Monitoring 97.2%→100%；回调泵同步 Sleep 改异步 Delay 后达 **99.5%**（57 行未覆盖）。2026-09-15 P7/P8/P9 三个功能阶段落地后新代码覆盖率债使合计回落至 98.5%（Steam.Core 97.8%、ControlPlane 98.8%）。
>
> 2026-09-16 覆盖率回填冲刺（+53 测试，2007→2060 全绿：Steam.Core 1092→1117 / ControlPlane 475→503）还清 P7-3/P8/P9 新代码债，合计回到 **99.5%**（Steam.Core 97.8%→99.4%、ControlPlane 98.8%→99.5%）。覆盖内容：积分商店两 action 的 metadata/definition_ids 值形状（SQLite JSON 往返后的 JsonElement、.NET List 全数值形状、单标量包装、全无效值跳过）；挂单创建/撤单 action 的参数校验、pacing、取消透传（OCE 经 `throw;` 不吞）、EmailDomain 上报与 webHandler 缺失分支；`SteamMarketClient` 非标准 JSON 值类型防御臂与空响应体；批量动作 .NET List 解析与工厂异常；`CrawlRunWorker` 的读取/派发取消传播、GetJob 故障吞咽、CancelJob 故障与取消、审计故障不阻断、无 games/errors 键输出与混合列表输出解析；`SqliteCrawlStore` 守卫；planner 空池告警；REST 侧 cancel 四过滤器 payload、五端点 202/502 三态与 claim 校验/去重。Monitoring 100%→99.4%：`MetricsHttpServer` 客户端断开防御 catch（2 行）本轮实测未命中，属时序边沿而非代码债。
>
> 2026-09-16 覆盖率收尾冲刺（+21 测试，2065→2086 全绿：ControlPlane 503→511 / Steam.Core 1117→1128 / Agent 53→54 / Plugins.Core 127→128）把上一轮剩余 74 行中全部可确定性覆盖的行收掉，合计升至 **99.7%**（13394/13433，未覆盖 74→39 行；Agent 99.5%→**100%**、Plugins.Core 99.4%→**100%**、ControlPlane 99.5%→99.8%、Steam.Core 99.4%→99.6%）。覆盖内容：`BotSession` QR 挑战轮转 republish 无回调分支与 `SessionCommand` 合成成员；`RedisVaporCache` SWR 缺键直填与 `RemoveByPrefix` 全端点断连回退；`RedactingLoggerProvider` 非泛型枚举臂；`FileCredentialStore` 符号链接→/dev/null 的 chmod EPERM 降级（Linux 非 root）；`VaporCryptoHelper` 合法 base64 解码过短回退 raw；`SteamMarketClient` currencyid 字符串臂；积分/挂单 action 的 min_price 过滤与批量解析报错点名；`CrawlRunWorker` 坏 tick 兜底 + 停机竞态双路径 + 审计取消；`DesiredStateReconciler` unassign 存储故障逃逸到后台循环 catch；`NotificationService` 有限流 broker 三泵自然排空；`TaskSchedulerService` 无 listener 惰性分发 + `VaporTracing` 双 null 臂；`Program` agent WS 循环正常出口（SlowHeartbeatStore 延迟后不传已取消 token）；`AccountTaskRunner` record 合成成员；`MaFileImportCli` 幽灵路径点名；`PluginManager.DisposeAsync` 卸载抛错隔离。确定性加固：`RecurringJobScheduler`/`TaskSchedulerService` 的 StartStop 冒烟从 Task.Delay 改为等 store 被调用信号。
>
> 2026-09-17 收尾推进（+4 测试，2089→2093 全绿：ControlPlane 515→518 / Steam.Core 1127→1128）把剩余 39 行再收 9 行并删 5 行死代码，合计升至 **99.8%**（13387/13417；Steam.Core 99.6%→99.7%、Monitoring 时序边沿行本轮实测命中回到 **100%**）。覆盖内容：`SessionManager` 并发 GetOrCreate 竞态败者分支（40 轮并发循环，双字典探针间的同步窗口）；`SqliteJobStore.SetTaskResult` 对未 claim 任务的 `NotFoundException`；`WebhookNotificationSink` 每次尝试都抛传输异常的重试至放弃路径（`StubHandler` 系新增 `ThrowingWebHandler`）；`CrawlRunWorker` tick<=0 kill switch（`StartAsync` 后等 `ExecuteTask` 终态，断言零调度）。死代码清理：`SteamStoreApiClient.JsonOptions` 静态字段（仅声明无任何引用，覆盖缺口暴露）。判定为不可收敛的缺口及理由：`TriggerScheduledJob`/`ClaimNextQueuedTask`/`SqliteCrawlStore.ClaimDuePlan` 的「读后 UPDATE 0 行」三处同事务防御性死分支（读查询已过滤状态且持互斥锁，单连接下不可达）；`SteamAuthTokenProvider.GenerateAccessTokenForAppAsync`（反射调用 SteamKit2 需活 CM 连接，真网测试不进 CI 覆盖率）；hosted 循环闭括号（取消经 OCE 退出，正常出口不可达）；`FileCredentialStore` 的 Windows-only return；`SteamTimeSynchronizer` 默认重载（会打真网）；`Program` 无 wwwroot 的 `UseStaticFiles` 分支（需删 bin 部署形状）。
>
> 2026-09-18 基线刷新（§31 admin 管理台 / §32 编排器保守自动接受 / §33 成就管理落地后）：TRX 计数 2093→2216，新代码债使合计回落至 **99.1%**（Steam.Core 99.7%→99.0%、ControlPlane 99.8%→99.1%）；同日 +10 测试（2216→2226）收掉成就读写 action 的边界缺口（`GetAchievementsAction` 的 internal 工厂 ctor 与元数据/无 webHandler/非数字 steam_id；unlock/reset 的 transport 抛异常臂；reset 无 client 与无响应；`ParseNames` 双格式值形状——裸 string、.NET List、JSON 数组非字符串元素逐字透传、null 与标量拒绝），Steam.Core 99.0%→99.4%、Monitoring 时序边沿行本轮实测命中回到 **100%**，合计升至 **99.3%**（14354/14452）。剩余 98 行的定性见下表：`DesiredStateReconciler` 51 行编排分支（no-agent skip/dry-run 守卫/审计异常隔离/payload 多类型解析）为干净但集成型构造，留后续测试深化冲刺；§33 新 records（`ISteamTransport`/协议 handler）的合成成员 Equals/ToString 属覆盖噪音，定性入册不强追。
>
> 2026-09-19 §35 Reconciler 测试深化（+22 测试，2226→2248 全绿：ControlPlane 573→595）收掉 `DesiredStateReconciler` 51 行编排分支中的 48 行，ControlPlane 99.1%→**99.8%**，合计升至 **99.6%**（14407/14459）。覆盖内容：派发守卫（boost evaluate 的 ActiveJobId 在飞行等待、playtime/trade 扫描/gift accept 的 no-agent skip 与 dry-run guard）；审计隔离（评估与 auto-accept 的异常吞咽及 OCE rethrow、201 offer 的 decisionsTruncated 软上限）；payload 解析防御（offerId==0/非 object JsonElement/裸 string 条目、OutputFlagIsTrue 双形态、boost 空 targets、playtimes 容器为 string、数值全形状臂——boxed int/long/double/string/JsonElement/缺 key）；结算回读防御（playtime/trade 扫描/accept 三处 StripTasks 空 Tasks 静默返回、accept 失败 deviation 且队列 drain、confirm Finished/Failed 两态）。两处实测定性防御深度不可达入册：accept dry-run guard 661（全局 dry-run 下前置扫描已被拦，cfg 只读无中途切换）、confirm 放弃 759-760（agent 消失走步骤 1 rebalance 清空 AssignedAgent+ActiveJobId）。修正三处测试自身断言错误：Boost 无 targets 被 store fail-fast 拒绝（改用 Online 状态直测）、审计断言未计入 ThrowOnRecord 置位前的合法条目（改记置位前计数）、gift accept no-agent 实际命中两轮 skip（settle 同 pass 尝试+下轮重试）。意外收获一个族一 flake：`GetMarketListings_AgentReportsFailure_Returns502` 与 `ProgramBranchCoverageTests` 类间并行时，30s 等待循环读到后者的 300ms `AccountTaskRunner.WaitWindow` 窗口提前 202（新增 `AccountTaskWaitWindowCollection` 串行化两类，全量覆盖率轮实测复现、修复后全绿）。Monitoring 时序边沿行（137/140）本轮未命中回到 99.4%，照旧定性。剩余 52 行定性见下表。
>
> 剩余 52 行未覆盖，六类（非测试缺口，不计入门禁预期）：
> - **编排分支防御深度不可达**（3 行）：`DesiredStateReconciler` 661（accept dry-run guard：全局 dry-run 下前置扫描派发已被拦，cfg 只读无中途切换路径）、759-760（confirm 放弃：agent 消失走步骤 1 rebalance 清空 `AssignedAgent`+`ActiveJobId`，confirm 结算读到的状态组合不可达）。§35 实测定性，同「同谓词防御性死分支」家族。
> - **record 合成成员（噪音）**（16 行）：`ISteamTransport`（210-240 的 Equals/ToString）与 `SteamUserStatsProtocolHandler`（12-15）——真实路径仅构造不比较，定性入册不强追。
> - **平台分支**（2 行）：`FileCredentialStore` 的 Windows return 与 Unix 权限 catch 另一侧（本机 Linux 非 root 只能走单侧，root 下 chmod 成功需守卫）。
> - **集成壳/反射**（14 行）：`SteamClientManager` 的 SteamKit2 内部类型反射（66-72）、`SessionManager` 从不 Complete 的通道正常出口（122/216/276）与 Barrier 竞态臂（132/133，现有测试在 2 核 CI 零命中）、`BotSession` SteamKit 回调深处（224/246）、`SteamProfileGamesClient` 前导空白跳过 while（154）。
> - **结构性 DEAD**（4 行）：`RedisVaporCache` for(;;) 无自然出口（108）、`RecurringJobScheduler`/`TaskSchedulerService` 的 `PeriodicTimer.WaitForNextTickAsync` 仅在 Dispose 时返 false（56/42）、`MarketWatchPlugin` const 行（27）。
>
> 2026-09-19 §36 ASF 风格 CI 收紧（P1 静态分析 + P2a 覆盖率门禁 + P2b property 测试，2248→2263 全绿：ControlPlane 595→603 / Steam.Core 1204→1207 / Protocol 39→43）。**P1**：Directory.Build.props 五开关（`AnalysisLevel=latest-all`+`EnforceCodeStyleInBuild`+三重 TreatWarningsAsErrors+`NuGetAuditMode=all`），首轮 ~12,800 条告警分类治理至 Release 全量 0 warning——src 真边界逐个修（CA1305 协议值 InvariantCulture 29 处防 locale 逗号损坏 API 参数、CA1307 域名比较 Ordinal、CA1032 七异常类补齐 ctor、CA2100 SQL 三 store 审后 pragma、CA2213/CA2000 真泄漏修复+所有权转移误报 pragma 化、CA5350 SHA1 定性为 Steam 协议要求），噪声族 .editorconfig 分层降级（design/风格族 none、CA2007/1849 等 suggestion 留可见性、tests 独立层）；教训一条：Dockerfile 没 COPY `.editorconfig` 时容器内严格构建直接红（降级配置是构建输入，与 Directory.Build.props 同列）。**P2a**：`coverage-summary.py --min` 门禁接入 CI coverage job，`--min 99.5`（基线 99.6% 留 0.1pct 时序边沿缓冲——本轮合计 99.614%，距裸基线仅 3 行余量，门禁是防真回退不是防噪声）。**P2b**：FsCheck.Xunit 2.16.6 三项目 +15 property；性质测试首战即抓获真实边界——`TryGetDouble` 对 boxed NaN/∞ 与 `TryParse("NaN")` 字符串透传非有限值（会静默流入 deviation 记录），产品修复为 `IsFinite` 守卫保守拒绝；crypto/协议往返性质各 3/4 条（随机 nonce 推翻初稿「密文稳定」断言、record 值相等对 Dictionary 是引用比较改逐字段、FsCheck 默认字符串生成器产 null/重复 key 需折叠）。FsCheck 2.x 固定种子（StdGen 确定）满足确定性纪律；`object?` 字典模型排除（STJ 回读 JsonElement 属已知可接受损耗，注释在册）。

> - **防御兜底与时序边沿**（13 行）：`SqliteJobStore` 迁移边沿（316/622/623/867）、`SqliteCrawlStore` 同事务同谓词防御性 CAS（251）、`SteamTimeSynchronizer` 真实网络超时（73）、`MetricsHttpServer` 断连 catch（137/140，时序边沿，多轮实测在命中与未命中间波动）、`MarketWatchPlugin` 轮询循环 catch OCE/Exception（194/196/197，时序边沿）、`Program` 135 取决于测试 bin 是否含 wwwroot、2653 agent WS 循环异常出口 finally。

> 2026-09-19 维护轮（Code scanning 告警清零 + FsCheck 扩面，2263→2270 全绿：Steam.Core 1207→1214）。①**CodeQL `cs/cleartext-storage-of-sensitive-information` 13 条 open（high）根治**——名字启发式把 ulong 局部变量 `accountId` 命中为敏感账户数据，taint 沿 `PartnerSteamId` 传入五个文件的 13 个日志 sink（值实为公开 SteamID 非凭据；09-18 dismiss 过一轮但代码改动行号漂移后同源重开——dismiss 绑定位置治标不治本），源改名三处（`TradeModels` 的 `out var parsedPartner`、`ToSteamId64(uint partnerId)` 参数、round-trip 测试参数 `rawId64`）断名流，CodeQL 重扫 open 清零且结构性不再重开。**命名纪律入册：src/tests 的 ulong 标识符局部变量避开 `accountId` 形命名**（SteamID 一律 `steamId`/`partnerId`）。②**FsCheck 扩面**（§36 收官列明的可迭代方向）+7 property（`TradeParsingPropertyTests`）：报价 URL 解析四条（任意输入不抛且解析成功必 ≥ base、32 位形态加 base、64 位形态直通、token URL 编码往返——lone surrogate 过滤照 crypto 先例）+ 状态机三条（无过期时间永不过期、CanAccept ⟹ CanDecline + 收到的 Active、sender 自匹配合规域恒过）。两处修正入册：时间偏移 helper 须用 `FromUnixTimeMilliseconds`（epoch 起算）而非 `AddMilliseconds`（epoch 总量加到 2026 溢出 9999 上限）；ValidateForAccept 自匹配性质需规范域 guard——`AccountIdOther` 次规范形态（< base）被 `ToAccountId` 折 0 属有意防御语义（`ToAccountId_WithSubBaseValue_ReturnsZero` 锚定），恒等式只在 64 位规范域成立。③统计表 stale 修正：Steam.Core 行 1197 为 §34 轮漏更（实为 1207），本轮起以 TRX 实测计数为准。验证：Steam.Core 1214 全绿、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 维护轮二（FsCheck 扩面续——换卡匹配算法 property，2270→2275 全绿：Steam.Core 1214→1219）。+5 property（`CardSwapMatcherPropertyTests`，覆盖 `FindDuplicates`/`MatchSwaps`/`ContextIdFor` 纯算法面）：①**分组不变量**——尺寸账目（TotalTradable > keep 且 Excess 恰为 TotalTradable - keep）、只收可交易身份且 excess 拷贝全部 Tradable、组按 (app, class, instance) 升序输出；②**匹配严格互补**——每个 swap 的收/给卡对方均零持有（双向 DoesNotContain）、双侧资产均来自各自 excess 池、context 规则随 Give/Receive、maxSwaps 上限；③**两确定性**——同输入两次结果全等（DuplicateGroup 是 record 且集合字段引用比较，逐字段 + SequenceEqual 比较，§36 教训复用）；④**ContextIdFor 全域**——753→6 其余→2。**生成器方法论入册**：identity id（app/class/instance）必须缩域（4×5×2 池）否则随机 id 几乎零碰撞、性质真空转；assetId 保持全域保证唯一性；数组参数（uint[]/ulong[]/bool[]）经最短长度对齐构造 items——FsCheck 默认 Arb 覆盖原始类型数组，自定义 record 不进生成器。`IsTradableNow` 依赖 `UtcNow`——property 只用 `TradabilityDate=null`（未来锁定期与 Tradable=false 在该谓词等价，仅测其一保持确定性）。验证：Steam.Core 1219 全绿、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 维护轮三（FsCheck 扩面三——市场手续费数学 property，2275→2280 全绿：Steam.Core 1219→1224，**性质测试再抓两个真产品缺陷并修复**）。+5 property（`MarketFeeCalculatorPropertyTests`）：拆分恒等（Buyer = Seller + 双费）+ 费下界、20 分起比例精确（floor 域）、买家价严格单调、`FromBuyerPrice` **极大性**（落点 ≤ 目标且 seller+1 必超）、域二分完备（<3 分 null、≥3 分必有解）、上限守卫。**缺陷一（定价精度，影响日常域）**：`FromBuyerPrice` 从「精确 15% 费」估计 `floor(target×100/115)` 起 down-walk，但两个 floor 费分量之和可低于 floor 的 15%（分数截断 + 低价 min-1-cent 区欠至 2 分）→ 真实可行 seller 可达估计 +2~3 而 walker 永远到不了——反例 buyer=39 时返回 34 而可行 35（39 分买家价）——**所有 <$1 卡的 dry-run 定价系统性少算卖家收益**；修复为起点 +3（欠账通用上界，推导入注释），115→100 等精确域不变（既有 11 例全过锚定）。**缺陷二（溢出守卫，极端域）**：`sellerProceedsCents × 5/10` int 乘法在 >`int.MaxValue/15` 时 unchecked wrap 可产出负买家价——新增 `MaxSellerProceedsCents` public 上限（≈$1.43M，远超真实挂单）双侧 AOORE 守卫 + buyer 侧乘法 long 化；property 用「上限域内断言正确、上限外断言守卫」的双分支形态钉死溢出行为。方法论：**极大性断言是定价/搜索类 walker 的杀手锏性质**——「落点合法（≤ 目标）」单侧断言对次优结果完全失明，「+1 必超」直接暴露起点低估。验证：Steam.Core 1224 全绿（property 5 + 既有 11 全过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 覆盖率扫零轮（用户指令「找出覆盖率为 0 的项目/类并提至 98%+」；2280→2317 全绿：Steam.Core 1224→1234 / 新增 Vapor.KeyRotation.Tests 27）。**盘点**：全解决方案程序集最低 98.9%、合计 99.7%，分母对比（src 全部源文件 vs cobertura class 条目）定位唯一真正的 0% 项目/类 = `tools/Vapor.KeyRotation`（211 行 CLI 壳，无测试项目、从未进覆盖率分母）；三个纯接口（`IJobStore`/`IActionExecutionObserver`/`ISteamTradeClient`）与 `Program.Partial.cs`（3 行空 partial 声明）无可执行行，定性为分母噪音。**KeyRotation 壳测试（+27，98.1%）**：`Program.Main` 直调全分支（help 双旗/未知参数/三形态缺参/坏 base64/同钥拒绝/store 缺失/损坏 JSON）+ 真实旋转三态（dry-run 断言逐行输出与文件零改动、applied 断言备份落盘且新钥可解回原文、aborted 断言 FAILED 逐账户上报）+ 反射直测私有静态（`ParseKeySpec` 六臂：base64 大小写、file 缺失/长 base64 解码/短 base64 回退/纯文本、env 设/未设、裸文本；`ExpandPath` `~/` 与 `~\` 双臂 + 相对路径；`GetValue` 正常取值与索引推进）。**不可测定性**：`GetValue` 缺值臂的 `Environment.Exit(2)`（117-118）在测试进程内会终止 testhost，结构性不可达——CLI 进程语义与测试进程语义的根本冲突。**教训两条入册**：①CLI 测试传参必须带 key-spec 前缀（`base64:`）——裸 base64 会被文档化的 plain-text 回退当作密钥材料，产品行为正确而测试静默错语；本轮用「同进程内 SHA256 hash 对比」定位：直调 `Rotate` 成功、镜像 `ParseKeySpec` 产出直调也成功、唯 Main 失败 → 在 Main 内临时 dump 实收参数 hash，发现与测试侧构造的 hash 不一致 → 反推出参数本身缺前缀（部件探针全绿 + 组合失败时，对比「产品进程内部实际收到的参数」而非继续拆部件）。②`Environment.Exit` 路径是 CLI 覆盖率的固有天花板（98%+ 即为满覆盖），不追。**上会话遗留落地**（工作树中未提交的 +10 测试一并验证并入）：`SteamAuthTokenProvider.GenerateAccessTokenForAppAsync` 反射桥（09-17 曾定性「不可收敛：需活 CM 连接」——推翻：反射替换 `_authentication`/`_generateAccessTokenMethod` 两字段指向 fake generator 即可钉死参数顺序与结果映射，SteamClientManager 88.6% 中反射 4 行收掉，剩异常类 CA1032 备用 ctor 4 行入合成噪音册）与 `UserStatsProtocol` records 契约 9 例（构造面/相等语义/OK 载荷配对规则/协议响应单载荷，定性表中「record 合成成员」类相应缩减，`ISteamTransport` 仅剩 240 一行 ToString）。**本轮后剩余 43 行定性**：异常类 CA1032 备用 ctor（SteamClientManager 4 + ControlPlane Exceptions 2 + PluginException 1，合成噪音）、KeyRotation `Environment.Exit`（2，结构性）、防御性死分支/时序边沿/平台分支（其余，多轮实测波动，照旧）。验证：全量两轮全绿（含覆盖率轮，合计 **99.7%** 14538/14581，门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

### 排除项

- 测试项目自身与 `Vapor.Plugins.TestPlugin`
- xUnit / Moq 框架程序集
- Microsoft / System 命名空间

## 测试框架和工具

- **xUnit** - 测试框架
- **Moq** - Mock 框架
- **Coverlet** - 代码覆盖率工具
- **ReportGenerator** - HTML 报告生成器

## 测试编写指南

### 命名约定

- 测试类: `<ClassName>Tests`
- 测试方法: `MethodName_State_ExpectedResult`
- 测试命名空间: `Vapor.Steam.Core.Tests.{Unit|Integration|Performance}`

### AAA 模式

```csharp
[Fact]
public async Task ExecuteAsync_WithValidPayload_ReturnsSuccess()
{
    // Arrange - 设置测试数据
    var session = CreateTestSession("test_account");
    var payload = new Dictionary<string, object?>();

    // Act - 执行被测试的方法
    var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

    // Assert - 验证结果
    Assert.True(result.Success);
}
```

### Theory 测试

```csharp
[Theory]
[InlineData("value1")]
[InlineData("value2")]
[InlineData("value3")]
public void Test_MultipleValues(string input)
{
    // Arrange & Act & Assert
    Assert.NotNull(input);
}
```

### Mock 使用

```csharp
// 创建 Mock
var mockLogger = new Mock<ILogger<MyClass>>(MockBehavior.Loose);
var mockDependency = new Mock<IDependency>(MockBehavior.Strict);

// 设置期望
mockDependency.Setup(d => d.Method(It.IsAny<string>())).Returns(true);

// 验证调用
mockDependency.Verify(d => d.Method("expected"), Times.Once);
```

## CI/CD 集成

测试可以轻松集成到 CI/CD 流程中：

CI（`.github/workflows/ci.yml`）在 Ubuntu Release 上跑全解决方案测试并上传覆盖率：

```yaml
- name: Test with coverage (Ubuntu Release)
  run: ./scripts/run-tests.sh -c
- name: Upload coverage to Codecov (Ubuntu Release)
  uses: codecov/codecov-action@v5
  with:
    files: "**/TestResults/*/coverage.cobertura.xml"
```

### CI 挂死守护（`--blame-hang`，2026-09-17 起）

CI 两个全量测试步（build-test 矩阵、coverage）挂 `--blame-hang --blame-hang-timeout 12m`：testhost 无进展 12 分钟即采集转储、终止宿主并把未完成测试标失败快速红，防挂死把 job 静默烧满 job 级超时；测试步失败时还会上传 `TestResults/`（hangdump + Sequence.xml）作取证。integration-redis 不挂（SE.Redis 内部 15s 超时兜底）；本地 run-tests.sh 不挂（交互跑挂住即 Ctrl+C）。取证备注：`gh run view --log` 读回偶发截断（首次计数 51，缓存后 56），取证计数以缓存文件为准。

守护背后是 MarketWatch.Tests 的间歇卡死（2026-09-14 起 6 次，全部 windows build-test，todo §28），一分为二：

- **在飞失速**（测试不交付、同程序集后续排队；证据：Edge 19/24 在飞）——根因是测试自己的 park 舞蹈：`Shutdown_DuringParkedFetch_CancelsCleanly` 把无视取消令牌的 fake fetch Task 在 `InitializeAsync` 前挂上、watch 又注册在循环重启之前，CI 负载下原循环首轮快照落在 add 之后即 park、重启的 `await _loop` 死锁。已修（脚本与 add 一律放在重启之后，原循环存活期 store 必空）。教训：**让循环 park 在不可取消的 Task 上时，必须保证任何存活的前序循环都够不到它**；「无断言红、无测试卡住」不是测试代码无罪的证据，在飞清单能把卡点定位到测试类。
- **收尾失速**（56/56 全部交付后宿主不退出，烧满 job 超时）——定性待转储定谳（vstest 基础设施 vs 残留 park），下次发作的 hangdump 是裁决证据。

## 性能基准

### 基线数字（唯一权威源：`docs/performance.md`）

基准覆盖三个维度：**时延**（只读 REST 端点 p50/p95 + 任务派发写入口对照，
`ApiLatencyBenchmarks.cs`）、**吞吐**（队列、并发 claimer、EventBroker/SSE 扇出、
缓存层，`ControlPlaneBenchmarks.cs` + `CacheBenchmarks.cs`）、**资源占用**
（每操作托管分配量，`ResourceFootprintBenchmarks.cs`）。

断言只设置宽松上限防止 CI 抖动；**打印出来的数字才是基线**，实测数字、测量环境
与口径说明统一记录在 `docs/performance.md`（刷新走其文末归档），此处不再手抄
以免双处漂移。`Vapor.Steam.Core.Tests/Performance/ConcurrencyTests.cs` 只断言
并发正确性，不记录数字。

运行方式：

```bash
./scripts/run-benchmarks.sh
# 或手动（detailed 是必须的，否则 xunit 不显示基准打印的数字）
dotnet test tests/Vapor.ControlPlane.Tests -c Release \
  --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~Performance" -- RunConfiguration.MaxCpuCount=2
```

## 最佳实践

1. **单一职责**: 每个测试只验证一个行为
2. **独立性**: 测试之间不共享状态
3. **可重复性**: 测试结果稳定，不依赖外部因素
4. **快速运行**: 单元测试应该快速执行
5. **清晰命名**: 测试名称应该描述它测试的内容
6. **适当的隔离**: 使用 Mock 隔离外部依赖
7. **边界条件**: 测试边界值和异常情况
8. **文档化**: 测试作为代码行为的文档
9. **进程全局状态必须入非并行 collection**: 见下节

## 进程全局状态与测试并行

xUnit 默认**类间并行**（每个测试类一个 collection，不同 collection 并行、同 collection 串行）。类内串行只保护同类方法之间——它**不**保证与其他类不并行。任何触碰进程级全局状态的测试都必须与全部并行 collection 互斥：给相关测试类挂同一个 `[CollectionDefinition(... DisableParallelization = true)]` collection。

仓内三类进程全局状态与既有隔离（2026-09-17 全仓审计，无遗漏）：

| 全局状态 | 触碰者 | 隔离 collection |
|---|---|---|
| `VaporCryptoHelper` 静态加密 key（`Encrypt`/`Decrypt`/`SetEncryptionKey`/`ResetForTests`） | VaporCryptoHelper{Tests,MethodTests,Encryption}Tests、FileCredentialStoreTests | `VaporCryptoHelperTestCollection`（Steam.Core）|
| `VaporCryptoHelper` 静态 key（Agent 侧 CLI 导入） | MaFileImportCliTests | `MaFileImportCliCollection`（Agent）|
| 进程环境变量 + OTel 宿主 boot 的进程级 ActivityListener | CompositionRootSmokeTests、TracingTests | `ProcessGlobalTracingCollection`（ControlPlane）|

> 案例（§23，CI 间歇红三挂一绿后定位）：`CompositionRootSmokeTests` 设 `OTEL_EXPORTER_OTLP_ENDPOINT` boot 真宿主,OTel SDK 注册**进程级** listener（宿主存活 ~2s）;并行的 `TracingTests.DispatchWithoutListener` 断言"无 listener 时派发不带 traceparent",撞上宿主存活窗口即炸。本地复现配方：testhost 设该 env + `taskset -c 0,1` + `MaxCpuCount=2` 模拟 CI 慢机（修复前 3 轮挂 1,入非并行 collection 后 3/3 绿）。
>
> 审计口径：其余 `Environment.Get/SetEnvironmentVariable` 使用均为只读门控（`VAPOR_TEST_REDIS`/`STEAM_TEST_*`）或私有命名空间（Plugins.Core 的 `VAPOR_TEST_PLUGIN_*`，无他类读者）；`ProgramBranchCoverageTests` 走 host 级 `UseEnvironment`/configuration 注入不碰进程 env;`Activity.Current` 赋值处已 save/restore 配对且所在类已入非并行 collection。新测试触碰上述任何全局状态时，先查此表入列或建新列。

## 测试维护

- 定期更新测试以匹配代码变更
- 保持测试覆盖率稳步提升（当前 99.7%，CI 覆盖率门禁 99.5%，见上方基线表）
- 新功能必须包含测试
- 修复 bug 时添加回归测试
- 定期审查和重构测试代码

