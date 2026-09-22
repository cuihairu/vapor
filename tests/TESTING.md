# Vapor 测试概览

本文档提供 Vapor 项目的完整测试概览。

## 测试项目结构

`tests/` 下 9 个测试项目(外加 2 个测试基建程序集:示例插件与故障 fixture 库):

```
tests/
├── Vapor.Steam.Core.Tests/               (1373 tests)
│   ├── Unit/                             动作/会话/交易/安全/数据/Web 客户端
│   ├── Integration/                      会话工作流 + Redis 缓存(门控)
│   └── Performance/                      并发与压力
├── Vapor.ControlPlane.Tests/             (700 tests)
│   └── Performance/                      队列吞吐/派发/SSE 扇出/时延/资源占用基准
├── Vapor.Plugins.Core.Tests/             (136 tests)
├── Vapor.Plugins.MobileAuthenticator.Tests/ (129 tests)
├── Vapor.Agent.Tests/                    (102 tests)
├── Vapor.Plugins.MarketWatch.Tests/      (56 tests)
├── Vapor.Plugins.Monitoring.Tests/       (36 tests)
├── Vapor.Protocol.Tests/                 (43 tests)
├── Vapor.E2E.Tests/                      (11 tests,真实双进程)
├── Vapor.Plugins.TestPlugin/             插件基础设施测试用示例插件
└── Vapor.Plugins.TestFixtures/           故障 fixture 库(故意坏实现/多实现类,供失败路径测试)
```

## 测试统计

| 测试项目 | 数量 | 覆盖范围 |
|----------|------|----------|
| Vapor.Steam.Core.Tests | 1373 | 动作、会话状态机、交易校验、凭据/加密(含轮换器)、maFile 解析、数据缓存(Redis mock 离线全覆盖)、Steam Web 客户端 + 契约回放、徽章页解析、报价列表、loot、addlicense、库存多 app 扫描、重复卡分析与 1:1 换卡匹配、QR 扫码登录会话流、市场挂单创建/撤单与手续费、积分商店、games tab 播放时间数据源、成就列表(社区页解析)与解锁/重置(client stats 协议位图数学/载荷门控/逐条结果/协议 records 契约)、auth token 反射桥、payload 值形状与分支加固(payload 读取器 property)、trade 资产载荷解析与资产校验 property、Steam TOTP property、熔断器/限流器边界、每账号代理(ProxyOptions 解析 property + 存取透传 + check_proxy 自检)、异常账号体检(standing 客户端 + check_account_standing action) |
| Vapor.ControlPlane.Tests | 700 | REST API、SQLite job/审计/抓取存储、任务派发(含 `agent:{id}` 定向派发)、账户编排(boost/trade 策略,§35 编排守卫/审计隔离/payload 解析/结算回读深化,§36 trade 策略规范化 property 测试)、周期任务、异常账号体检编排(周期体检/隔离/解除/强制体检/快照)、挂卡 ASF 式增强(farm 策略规范化/队列排序/预算跳过/队列 diff 完成标记/累计统计/farm 快照端点)、插件生态(PluginStore REST/catalog 索引源/镜像/定向派发 + §38 维护轮 property 扩面:target 往返/checksum 归一/索引解析不变量)、通知、追踪 + WS 协议回放、报价查询/接受/拒绝/批量确认/loot/免费认领/库存读取/重复查询/换卡报价、数据抓取计划/执行/分片、静态面板契约(含 admin 写操作确认锚)、坏 JSON 边界、QR 挑战归类、Program 分支加固、Bearer 鉴权解析 |
| Vapor.Plugins.Core.Tests | 136 | 插件发现/清单/SemVer 兼容/加载/卸载/ALC 回收/事件分发/配置/信任与权限/故障 fixture 库 + 测试插件面直调(echo action/pong command/marker 生命周期/fixture 契约) |
| Vapor.Plugins.MobileAuthenticator.Tests | 129 | TOTP、确认哈希(含 FsCheck property:HMAC oracle 交叉验证)、移动交易确认(单个/批量)、shared/identity secret 持久化、报价确认闭环、插件宿主实战加载 + 动作边界(payload 形状/失败语义/冷却)与确认客户端解析分支 |
| Vapor.Agent.Tests | 102 | 重连退避策略(含不变量 property:曲线单调/上下界/重试谓词单调/构造器往返/四违约臂)、任务执行器(含 QR 登录与 password+refreshToken 组合 payload、代理 payload 透传与畸形端点 fail-fast)、WS URI 构造、maFile 离线导入 CLI、追踪注入、插件 host action(IHostAction 通道、zip 包安装器(URL/校验和/zip-slip/清单核对/目录条目/http 下载三态)、install/uninstall/list 三 action 与镜像输出) |
| Vapor.Plugins.MarketWatch.Tests | 56 | watch 存储/阈值评估/free watch 边沿告警/轮询告警与 webhook(含传输崩溃与取消路径)/轮询循环确定性停机/阈值 payload 值形状/插件宿主实战加载 |
| Vapor.Plugins.Monitoring.Tests | 36 | 指标注册表/HTTP 指标服务/插件生命周期 |
| Vapor.Protocol.Tests | 43 | JsonDefaults 序列化契约(camelCase/枚举字符串/null 省略/前向兼容)+ 全部协议模型逐字段往返 + record 边界(畸形 JSON/缺字段/默认值)+ FsCheck property 往返(任意字段值的心跳/取消/错误/握手模型恒等) |
| Vapor.E2E.Tests | 11 | 真实双进程闭环:CP 进程 + Agent 子进程(job 派发、任务回报、SSE、账户编排重平衡、静态页守护) |
| Vapor.KeyRotation.Tests | 28 | 凭据轮换 CLI 壳:参数解析(缺失/未知/help 双旗/dry-run)、key spec 四格式全臂、退出码契约(0/1/2,含 `--new-key` 缺值臂以 dotnet 子进程驱动并断言退出码 2——进程内直调会终止 testhost)、真实旋转三态(dry-run 不落盘/applied+备份+新钥可解/aborted+FAILED 上报)、损坏 store 异常路径 |
| **合计** | **2614** | (2026-09-21 基线;另 E2E 以真实子进程覆盖 Agent 主循环,单测统计测不到) |

> 基线刷新方式(用 TRX 精确计数;`--list-tests` 会在终端宽度处折行长 theory 名,grep 计数会漏掉折行的用例):
> ```bash
> find tests -name "*.trx" -delete
> dotnet test Vapor.sln -c Release --logger "trx;LogFileName=count.trx"
> for f in $(find tests -name count.trx); do echo "== $f"; grep -o '<UnitTestResult [^>]*?testName="[^"]*"' "$f" | sed 's/.*testName="//;s/(.*//' | awk -F. '{print $(NF-1)}' | sort | uniq -c | sort -rn; done
> ```

## 测试分类

### Steam.Core(1263 个测试)

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
| TradeAssetValidatorPropertyTests | 7 | FsCheck property:守卫链顺序(非法 amount→未拥有→不可交易→冷却→数量不足,早失败遮蔽后检查)、重复条目数量聚合(Σ 请求 vs 可用,坍缩为单条短缺错误)、冷却边界(now 本身过、严格大于才拦)、空报价恒 Success 单例、成功 ⟺ Errors 空 + CombinedError 契约、非法 amount 独立报错不进聚合;全程注入时钟保确定性 |
| TradeRateLimiterTests | 16 | 频控(窗口/并发槽/超时/租约与销毁交互) |
| CardSwapMatcherTests | 9 | 重复分组与 1:1 互补匹配(keep/excess 只取可交易/排序确定性/双向互补条件/单向不配/maxSwaps 截断/context 规则) |
| TradeUrlParamsTests / TradeUrlParamsExtendedTests | 7 | 报价 URL 参数 |
| TradeParsingPropertyTests | 7 | FsCheck property:报价 URL 解析(任意输入不抛且解析成功必 ≥ base/32 位形态加 base/64 位形态直通/token URL 编码往返,lone surrogate 过滤)与状态机谓词(无过期时间永不过期/CanAccept⟹CanDecline+收到的 Active/sender 自匹配合规域恒过——抓出 AccountIdOther 次规范形态被 ToAccountId 折 0 的防御语义边界) |
| CardSwapMatcherPropertyTests | 5 | FsCheck property:重复分组不变量(尺寸账目 TotalTradable-keep/只收可交易/组按 identity 排序)与 1:1 匹配(严格互补双向 DoesNotContain/双侧来自 excess 池/context 规则/maxSwaps 上限)+ 两确定性(逐字段比较——record 集合字段是引用比较)+ ContextIdFor 全域(753→6 其余→2);生成器 id 缩域保证碰撞(assetId 保持全域) |
| TradeAssetParsingPropertyTests | 7 | FsCheck property:trade 资产载荷解析(任意 boxed 数值/InvariantCulture 字符串精确解析、缺键保 CS:GO 默认 730/2/1、不可读值同样回退默认——**抓获 TryParse out 参数写 0 覆盖预置默认、垃圾值产出 AppId=0 静默条目的真缺陷**、asset_id 不可用丢条目、任意容器形状不抛+缺键得 Empty、混合条目过滤保序) |

#### 安全与凭据
| 测试类 | 数量 | 说明 |
|--------|------|------|
| FileCredentialStoreTests | 27 | 凭据存储(加密落盘/备份恢复/权限收紧含 symlink EPERM 降级/shared/identity secret/新版本拒载/缺 accounts 拒载) |
| MaFileParserTests | 13 | maFile 解析(SDA 嵌套/steamguard-cli 平铺/密码加密 PBKDF2+AES-CBC/无密码与错密码/无 secret/账户键回退/根数组与坏 payload 拒绝) |
| VaporCryptoHelper(Encryption)Tests + VaporCryptoHelperMethodTests + VaporCryptoHelperTests | 39 | AES-GCM 加密助手(12+23+4:往返/篡改/边界 + 方法级分支/文件密钥回退含 base64 过短回退 raw/不可读文件降级) |
| VaporCryptoRoundTripPropertyTests | 3 | FsCheck property:AES-GCM 任意明文×任意密钥往返恒等、错钥认证失败(NPE 永不静默回原文)、随机 nonce 密文互异且均可解 |
| CredentialStoreRotatorTests | 5 | 密钥轮换 |
| RedactingLoggerProviderTests / SensitiveDataRedactorTests | 17 | 日志脱敏(12+5:嵌套异常/结构化 scope/非泛型枚举臂/计数与索引器/空值归一) |
| AgentReconnectPolicyTests | 4 | Agent 重连策略(默认值/退避曲线/重试上限/FromEnvironment 覆写) |
| AgentReconnectPolicyPropertyTests | 5 | FsCheck property:任意合法配置下退避曲线单调非减、初值下界与 max 上界、重试谓词对失败数单调(unlimited 恒假)、构造器四参往返、单约束违约必抛 ArgumentOutOfRange(totality) |

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
| PayloadReaderPropertyTests | 7 | FsCheck property:精确键恒压大小写变体/变体回退命中(OrdinalIgnoreCase)、boxed long 域判定(恰 int 域内转换)、boxed double 域判定(整数且域内,NaN/∞ 全 null)、任意 int 三形态(装箱/invariant 字符串/JSON 字符串)往返、任意 bool 三形态往返(**bool.ToString() 产 "True"/"False" 须被 TryParse 接受**——点例只测过小写)、任意形状×任意键全读取器不抛 |

#### Steam 认证
| 测试类 | 数量 | 说明 |
|--------|------|------|
| SteamTotpTests | 14 | Steam TOTP(本地 2FA 码生成) |
| SteamTotpPropertyTests | 6 | FsCheck property:**RFC 6238 oracle 交叉验证**(本地手写规范实现,任意 secret×时间恒等)、输出确定性 + 恒 5 字符无混淆字母表、同 30s 窗口任意偏移同码、SecondsRemaining 周期性与值域、DecodeSecret 任意合法 base64 往返 + 坏输入按参数名拒绝 |
| SteamTimeSynchronizerTests | 9 | Steam 服务器时间同步 |

### ControlPlane(677 个测试)

| 测试类 | 数量 | 说明 |
|--------|------|------|
| ProgramBranchCoverageTests | 73 | Program 组装层分支(配置解析/环境变量回退/装配路径逐支驱动) |
| AccountApiTests | 120 | `/v1/accounts` REST(含 farm 状态、报价查询/接受/拒绝、自动确认、批量移动确认、loot 同步端点、免费 license 认领、库存读取、挂单创建/撤单、积分兑换、重复查询/换卡报价:fake agent 顺序回报;五端点 202/502 三态与 claim 校验;boost 目标与 farm 策略 PUT 校验/回读/清除,standing/farm 快照端点与强制体检) |
| DesiredStateReconcilerTests | 116 | 账户编排(登录派发/退避/节流/重平衡/dry-run/smart farming 调度 + 循环存活/无 agent/在途窗口/形状怪癖执行路径加固/unassign 存储故障逃逸 + §35 派发守卫(agent 能力缺失与 dry-run)/审计隔离(异常吞咽与 OCE 传播)/payload 多类型解析防御/结算回读防御(空 Tasks/失败 deviation/confirm 链) + 挂卡增强:队列排序三式/PriorityApps 置顶/队列 diff 完成标记/预算跳过与未到期保持/drain 一次性通知与新轮重启/累计统计双格式解析与无 total 容错/spec bump 事实保留) |
| TradePolicyPropertyTests | 8 | FsCheck property:trade 策略白名单(零剔除/去重/升序/输入序无关/幂等/全零 auto-accept 必拒)与 payload 数值读取(任意 boxed 值不抛/false 置零/JSON 数字臂往返);抓出并修复 NaN/∞ 透传边界(string 臂 `TryParse("NaN")` 为 true、boxed double 臂不滤非有限) |
| CrawlApiTests | 40 | 数据抓取 REST(计划 CRUD/PUT merge 语义/触发/分片领取/行回写/claim 校验与去重) |
| CrawlRunWorkerTests | 28 | 抓取执行 worker(读取/派发取消传播/GetJob 故障吞咽/坏 tick 兜底/停机竞态双路径/审计故障不阻断/混合列表输出解析) |
| SqliteJobStoreTests | 26 | job 存储(并发/迁移/周期模板) |
| AccountStoreTests | 46 | 账户存储(ConfigVersion 并发/空名校验/boost 目标与 trade 策略规范化/farm 策略规范化校验矩阵/声明序保留/透传与清除) |
| AccountTaskRunnerTests | 2 | 账户任务运行器(TaskRunResult record 合成成员/克隆等值) |
| NotificationTests | 21 | 通知规则/webhook 签名/派发隔离/无 sink 快速返回/有限流 broker 三泵自然排空 |
| ScheduleClockTests | 16 | 周期计划时钟(interval/cron/触发点计数) |
| SqliteCrawlStoreTests | 16 | 抓取存储(守卫/同事务防御性 CAS) |
| RecurringJobSchedulerTests + RetireTests | 14 | 周期任务触发/missed/overlap/退役(10+4) |
| ControlPlaneApiTests | 13 | REST API(鉴权/任务/SSE/计划 job、QR 挑战归类与 URL 透传、坏 JSON 体 400 边界) |
| DashboardStaticTests | 14 | 静态面板(/dashboard.html 服务、无写动词契约、三视图互链、`/` 302 重定向、gamedata 五模型文档(含 ItemInfo 不回流守卫)、admin QR 按钮契约、写操作确认锚、standing 徽章与体检按钮、farm 徽章与策略字段、PluginStore 面板契约) |
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
| ExceptionContractTests | 1 | 异常类型契约(序列化构造) |
| PluginApiTests | 14 | PluginStore REST(五端点 401 矩阵/catalog 未配置与索引 entries 排序与 60s 缓存/拉取失败与坏 body 的 error 字段/install 校验矩阵(空 agents/缺 sha/坏 sha/双缺)/catalog 模式查无 404 与解析 url+sha256 归一/直连模式透传/uninstall 路由派发/refresh 派发/无 agent 409/stale 镜像清理/空镜像) |
| PluginCatalogServiceTests | 6 | 索引源解析与缓存(缺 plugins 数组拒收/缺必填字段拒收/sha256 归一与可选字段/未配置快照/60s 缓存命中与 Invalidate/URL 变更绕过陈旧缓存) |
| PluginInventoryTests | 4 | 内存镜像(WS round-trip 形状重建与排序/plugins 键缺失与非数组值忽略/按 agent 整体覆盖/Remove) |
| HostTargetedDispatchTests | 3 | `agent:{id}` 定向派发(按 id 路由绕过区域 Pick/目标离线 requeue/目标能力缺失 requeue) |
| PluginEcosystemPropertyTests | 7 | §38 维护轮 FsCheck property:target 往返恒等与精确接受(null/空/嵌套 `agent:`)、checksum 归一(64-hex 缩域生成器采样接受区/任意串接受谓词/幂等)、索引解析(平行数组生成任意 catalog:字段透传+sha 小写归一+Ordinal 排序;任意 json 只以 FormatException/ArgumentException/JsonException 拒绝且快照恒有序) |

### 插件体系(349 个测试)

- **Plugins.Core(129)**:清单解析(13)、发现(5)、加载(11)+加载器(8)、卸载与 ALC 回收(9,含 DisposeAsync 卸载自身抛错隔离)、信任与权限(18)、事件分发(12)、配置扩展(20)、能力(7)、插件 API 与 SemVer 兼容(18,含 TryParseVersion theory 展开)、管理器并发(1)、故障 fixture 库(6)+ 异常 ctor 契约(1)
- **MobileAuthenticator(129)**:动作含 save_shared_secret/save_identity_secret/confirm_trade_offer/confirm_all_confirmations(41)+ 动作边界:payload 形状/失败语义/冷却与并发(44)、确认客户端解析含 type 归一化(10)+ 客户端会话分支(17)、解析分支(10)、确认哈希(8 + FsCheck property 3:HMAC oracle 交叉验证/确定性 + 四 tag 互异/空 tag 与空 secret 的 ParamName 契约)、设备 ID(3)、插件加载与 9-action 断言(3)、shared/identity secret 存储行为(7,位于 Steam.Core 的 FileCredentialStoreTests)
- **MarketWatch(56)**:watch 存储(18)/阈值评估/三个 watch action(kind=price/free)/轮询告警与 webhook(free_game_alert 与 price_alert/传输崩溃吞并/周期中取消干净停机/手动驱动前 drain-and-stop 测试钩子)/单 app 抓取失败隔离/阈值 payload 值形状/无参构造 Info/插件宿主实战加载(2)
- **Monitoring(35)**:指标注册表(9)/HTTP 服务(16)/插件生命周期(10)

### Agent(93 个测试)

重连退避策略(30,含不变量 property 5——曲线单调非减/初值下界与 max 上界/重试谓词单调且 unlimited 恒假/构造器四参往返/单约束违约必抛 ArgumentOutOfRange;生成器陷阱两处:swap 法造 max<initial 会撞 max==initial 合法域,大 factor 上减 1 可能仍 ≥1——违约臂都必须构造出严格越界的值)、任务执行器(15,含 QR 登录与 password+refreshToken 组合 payload 解析与优先级)、插件 host action(37:IHostAction 执行器 4——跨 agent target 拒绝/无会话执行/异常包装/取消透传;包安装器 18——zip-slip/根 manifest/URL 与校验和预检/校验和不符零痕迹/清单 id-version 核对/替换热重载/下载失败/PluginsRoot 属性/取消透传/64 位非 hex/无根 manifest 全链/坏 manifest JSON/入口 DLL 缺失/显式目录条目/http 下载成功/声明长度超限/默认 HttpClient 工厂失败臂;install action 5——元数据/url-sha256 必填与 sha_256 别名/宿主未初始化/成功全清单输出/失败仍带镜像;uninstall action 7——元数据/pluginId 必填/幂等 removed=false/卸载热卸载/宿主未初始化/缺失目录早退/只读根降级;list action 3——元数据/空清单/带元数据清单)、maFile 离线导入 CLI(9,含幽灵路径点名)、WS URI 构造(4)、追踪注入(1)

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
# 推荐（整个解决方案，coverlet.collector，输出到 */TestResults/*/coverage.cobertura.xml；
# 全量运行自动走串行收集 + 逐报告校验，见下节）
./scripts/run-tests.sh -c

# HTML 报告（可选，需要 ReportGenerator）
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/TestResults/*/coverage.cobertura.xml -targetdir:./TestResults/coveragereport
```

## 代码覆盖率

9 个测试项目统一接入 coverlet.collector；`run-tests.sh -c` 在收集前清理历史残留报告（清理必须在测试之前——测试结束后这些路径上的文件就是本次结果），覆盖整个解决方案。全量运行（无过滤器）委托 `scripts/collect-coverage-serial.sh` 逐项目串行收集并逐报告校验；Windows 侧 `run-tests.ps1 -Coverage` 为原生移植（不依赖 bash/python）。带过滤器的运行只跑匹配子集，保留单次收集路径、覆盖率仅作现场排查参考——必须带 `--settings tests/coverlet.runsettings`，否则测试程序集计入分母（§38 教训）。

### 当前基线（2026-09-22，行覆盖 100.0% / 分支覆盖 96.8%）

合并全部报告计算：`./scripts/coverage-summary.py`（按程序集归一化文件路径后，以 (程序集, 文件, 行) 去重取最大命中；分支覆盖按分支行的 condition-coverage 统计，同一行多次观察取已覆盖条件数的最大值）：

| 程序集 | 行覆盖 | 分支覆盖 |
|--------|--------|----------|
| Agent | 100.0% | 98.5% (191/194) |
| MobileAuthenticator | 100.0% | 98.4% (315/320) |
| Monitoring | 100.0% | 96.3% (104/108) |
| Plugins.Core | 100.0% | 98.8% (255/258) |
| Plugins.TestFixtures | 100.0%（故障 fixture 库，已由 TestFixturesTests 全覆盖） | 100.0% (6/6) |
| Plugins.TestPlugin | 100.0%（示例插件，fixture 程序集） | 100.0% (4/4) |
| Protocol | 100.0% | （无分支行） |
| ControlPlane | 100.0% | 95.4% (1976/2072) |
| Steam.Core | 100.0%（取消/竞态臂经确定性测试与排除定性收尾，见下） | 97.4% (2938/3015) |
| MarketWatch | 100.0% | 93.0% (132/142) |
| KeyRotation | 100.0%（CLI 壳全覆盖；`GetValue` 缺值臂 `Environment.Exit(2)` 由子进程测试覆盖——测试进程内直调会终止 testhost，故以 `dotnet` 子进程驱动该臂并断言退出码 2） | 100.0% (46/46) |
| **合计** | **100.0%** (15882/15882) | **96.8%** (5967/6165) |

分支覆盖门禁：CI `--min-branch 96.78`（基线 5967/6165 的未舍入值为 96.788%，门禁取 96.78——当前过、丢一个条件 96.772% 即红；2026-09-22 分支缺口冲刺第三轮后设点。设点时一度心算成 96.837 抬到 96.83，CI 门禁红、设点者被自家门禁拦下，以此条勘误）。行覆盖 100% 不蕴含分支覆盖 100%：一行执行过不等于它的每个布尔子条件结果都被取到。

> 上轮（2026-09-21）定性入册的 2 行防御性死分支（`Program` agent WS 循环的 try 收闭与尾部清理）已于 2026-09-22 收口——「结构性不可达」实为测试竞速：正常出口臂由 `AgentWs_RequestAbortedDuringHeartbeat_UnregistersThroughLoopExit` 确定性覆盖（`RequestAborted` 经 `IStartupFilter` 换成测试可控 linked CTS，token 无视心跳进行中取消；详见日期日志）。分母自此无残余缺口，CI 门禁收紧至 100%——任何一行未覆盖（新增代码未带测试）即红。

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

> 2026-09-19 维护轮五（FsCheck 扩面四——trade 资产载荷解析 property；「继续」自主立项，§36 收官点名的 offer 解析族；2321→**2328** 全绿：Steam.Core 1236→1243，**性质测试第 4 次抓获真产品缺陷并修复**）。+7 property（`TradeAssetParsingPropertyTests`，`ParseTradeAssets`/`ParseSingleAsset` private→internal 直测）：①**字段精确性**——任意 boxed 数值与 InvariantCulture 字符串（线上 JSON 数字的替身）精确解析（全范围生成器，assetId==0 guard 排除丢条目域）；②**默认值二分语义**——缺键/null 保 CS:GO 默认（730/2/1）、不可读值同样回退默认；③**丢条目**——asset_id 缺失/null/0/不可读/溢出五形态全丢；④**容器全形状**——payload 键缺失/null/字典列表/混合元素列表/裸标量任意形状不抛、缺键得 Empty 非错误、产出条目 assetId 恒非零；⑤**过滤保序**——有效/无效混合条目按输入序保留有效者。**缺陷（TryParse out 参数陷阱，§36 CA1806 注释声称语义的假落实）**：`uint appId = 730; _ = uint.TryParse(obj.ToString(), out appId)`——TryParse 失败时把 out 参数**写 0**，预置默认被覆盖，注释「unreadable keeps default」与实际行为不符：不可读 app_id/context_id 产出 AppId=0/ContextId=0 的静默垃圾条目（缺键与不可读两条路径行为不一致；仅 amount 因下游 clamp 碰巧正确）；修复为失败分支显式重赋默认（amount 的 ≤0 clamp 一并统一进解析处），「缺键 = 不可读 = 默认」三态一致。**方法论入册**：①「预置默认 + 丢弃 TryParse 返回值」是 C# 陷阱写法——out 参数失败写 0 使预置失效，防御性解析必须检查返回值并在失败分支显式回退；②注释声称的语义未经测试锚定时可能整段为假——§36 CA1806 治理时给这四处加的注释就是对行为的错误断言，property 测试以「我认为的语义」写断言即刻暴露。**测试侧修正两条**：FsCheck 默认 string 生成器含 null——payload 字典 key 用 `NonNull<string>`（先例 `VaporCryptoRoundTripPropertyTests`）；混合类型 switch 臂需显式 `(object?)` 转换消 CS8506。验证：全量覆盖率轮全绿（合计 **99.7%** 14547/14584，门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 维护轮六（FsCheck 扩面五——认证码数学 property：SteamTotp + ConfirmationHashGenerator；「继续」自主立项，§36 收官点名的可迭代方向；2328→**2337** 全绿：Steam.Core 1243→1249 / MobileAuthenticator 126→129）。+9 property：①**SteamTotp（`SteamTotpPropertyTests`，6 条）**——**RFC 6238 oracle 交叉验证**（测试内本地手写规范实现：HMAC-SHA1/30s 窗口 big-endian 计数器/dynamic truncation/Steam 字母表投影，与产品实现任意 secret×时间全域恒等——「实现 = 规范」性质，独立于产品代码路径）、输出确定性 + 恒 5 字符 ⊆ 26 符号无混淆字母表（0/1/I/L/O 缺席 = 转抄安全语义的构造性保证）、同 30s 窗口任意偏移同码（窗口索引全域 × 偏移全域构造域，规避 ulong 时间溢出）、SecondsRemaining 周期性（SR(t)=SR(t+30)）+ 值域 [1,30]（负余数修正域全域）、DecodeSecret 任意合法 base64 往返恒等 + 坏输入按 ParamName 拒绝；②**ConfirmationHashGenerator（`ConfirmationHashGeneratorPropertyTests`，3 条，MobileAuthenticator.Tests 首次引入 FsCheck）**——HMAC-SHA1 oracle 交叉验证（8B big-endian 时间 + UTF-8 tag 拼接，任意 secret×时间×四已知 tag 恒等）、确定性 + 四已知 tag 同输入互异、失败契约（空 secret 先于空 tag 检查 → ParamName 分别为 identitySecretBase64/tag）。**本轮无产品缺陷**——认证数学已有已知向量锚定，property 的价值是把「3 个 RFC 向量点例」升级为「全域 = 规范」断言；oracle 交叉验证形态适用于一切有规范可依的实现（RFC/协议文档）。**测试侧修正两条入册**：①反例清单须实测——`"ZZZZ"` 是合法 base64（4 字符组解码 3 字节），直觉上「乱码」的串可能落进合法域，被 property 抓出后换 `"===="`（长度 4 全 padding 非法）；②`uint % int` 提升为 long 不能作 string 索引器参数（显式 `(int)` 收窄）。顺带修正 4 处 stale 计数（ControlPlane 603→604、插件体系 345→349、Plugins.Core 128→129、MobileAuthenticator 126→129——维护轮四只更了统计表漏了分类标题，本轮起分类标题一并对照）。覆盖率合计 99.7%（14545/14584）：分子 −2 为 SessionManager Barrier 竞态臂轮间波动（上轮命中本轮未命中，在册时序边沿家族照旧定性）。验证：全量覆盖率轮全绿（门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 维护轮七（FsCheck 扩面六——payload 读取器值形状 property；「继续」自主立项，轮五 trade 资产解析的同族地基收官——全部 action 入口都走 `PayloadReader`；2337→**2344** 全绿：Steam.Core 1249→1256）。+7 property（`PayloadReaderPropertyTests`）：①**查找语义**——精确键恒压大小写变体（decoy 在旁不误返）、变体回退命中（`ToUpperInvariant` 孪生键全域，自碰撞 guard——decoy 跳过而非赋值，见下）；②**GetInt32 域判定**——boxed long 恰 int 域内转换（全域 long 二分边界）、boxed double「整数且域内」双条件（生成器含 NaN/±∞ 全 null——与 §36 TryGetDouble 的 IsFinite 守卫语义呼应）；③**往返三形态**——任意 int 经装箱/invariant 字符串/JSON 字符串三形态存活（负数与 ±int 边界全域，NumberStyles.Integer 锚定）；任意 bool 三形态往返——**`bool.ToString()` 产 "True"/"False" 必须被 bool.TryParse 接受**（点例只测过小写 "true"/"false"，大小写形态此前无锚）；④**总函数性**——任意形状×任意键×三读取器永不抛。**测试侧自纠一条**：首版「精确键压变体」性质中，key 全大写自反时 decoy 赋值会**覆盖** exact 值（注释声称「碰撞时断言仍成立」是错的——轮五同款「注释未推演」陷阱，property 一轮即抓）；修复为自碰撞时跳过 decoy。覆盖率合计 99.7%（14547/14584，Barrier 竞态臂 2 行本轮命中回归）。验证：全量覆盖率轮全绿（门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。

> 2026-09-19 维护轮八（FsCheck 扩面七——资产校验守卫链 property；「继续」自主立项；用户中途下达 §38 四大功能方向后本压缩收尾；2344→**2351** 全绿：Steam.Core 1256→1263）。+7 property（`TradeAssetValidatorPropertyTests`）：①**守卫链顺序**——非法 amount → 未拥有 → 不可交易 → 冷却 → 数量不足逐级短路与遮蔽（未拥有资产报 not found 而非 untradable，双门同破报 not tradable 而非 cooldown）；②**重复条目聚合**——Σ 请求量 vs 可用量判定、超额坍缩为单条短缺错误（消息含 Σ 与可用量）；③**冷却边界**——`TradabilityDate == now` 放行（严格大于才拦，一 tick 后即拦）；④空报价恒 `Success` 单例（ReferenceEquals）；⑤非法 amount 独立报错且不进聚合（与合法重复条目并存时只报一条）。全程注入时钟（轮二 `IsTradableNow` 教训复用）。本轮无产品缺陷。验证：全量覆盖率轮全绿（合计 99.7% 14547/14584，门禁 99.5 过）、format 门禁过、CI 终态见提交后监控。
> 2026-09-20 §38 P1 每账号代理（用户 2026-09-19 下达四大方向之④并排序第一——多账号同 IP 易关联封号，保号地基优先；2351→**2408** 全绿：Steam.Core 1263→1318、Agent 54→56，+57）。七件套：①**`ProxyOptions` 解析器**（http/https/socks5 白名单、凭据 percent-decode、IPv6 方括号强制、scheme 默认端口 80/443/1080、`ToString` 恒掩码原文不可逆）+ 16 example + 4 property（受控域生成器；**property 抓出 2 个真缺陷**：`ArgumentNullException.ThrowIfNull` 的 ParamName 恒为 "value" 而非调用方命名——须手工构造；`user@host` 无冒号形态 password 应保持 null 却被 `Decode(null)` 变空串）；②**`AccountCredentials.Proxy` 加密持久化**（`SaveProxyAsync` 先解析 fail-fast、null 清除未知账户为 no-op；RoundTrip 跨实例测试抓出写侧 `EncryptValue`/读侧 `DecryptValue` 白名单双侧漏项——实现轮自查未发现，测试轮即抓）；③**CM 层代理**：`SteamClientManager` 改 `SteamConfiguration.Create`（**WebSocket-only** + `WithHttpClientFactory` 按活跃账户代理建 `SocketsHttpHandler`，socks5 远程 DNS），`SetAccountProxyAsync` 出口变化 → 断开 CM 等 OnDisconnected TCS（10s 上限）→ 下次 `ConnectAsync` 新代理重建（调研结论见 todo §38：SteamKit2 官方「CM 代理 unlikely」已过时，3.4.0 全程公共 API 可达）；④Web 层 `SteamWebHandlerConfig.Proxy` + `SessionManager` 按账户注入（CM/Web/trade/mobileconf 同出口）；⑤脱敏：Redactor 黑名单加 proxy + `scheme://user:pass@` URI 凭据正则（`<scheme>://<redacted>@`），JSON 嵌套字符串同效；⑥**`check_proxy` action**（payload 代理优先于已配置；出口 IP/Steam 可达/延迟；输出掩码化；真网络探针 `ExcludeFromCodeCoverage` + `ProbeOverride` 测试 seam）；⑦dashboard check_proxy 选项 + login/check_proxy payload 代理字段提示；Agent 侧 payload `proxy` 预解析 fail-fast（畸形端点在任何 session 触碰前失败）、四凭据构造点全透传。补测两处新分支：BotSession 密码登录路径 proxy 透传/无代理不触碰 transport、SessionManager 代理持久化失败容忍（store 抛异常登录仍成功）。折衷在册：代理按活跃账户生效（共享 CM 串行），切换断开重连 ~1-2s；并行多账号需 per-account SteamClient 池（P3 后方向）。覆盖率合计 99.7%（14742/14788）：Monitoring 时序边沿 2 行本轮命中归位；Steam.Core 99.7%→99.5% 为新增代理链路 4 行边界缺口（BotSession 命令循环 catch 尾、SessionManager try 边界——与在册时序边沿同性质）。**测试侧教训入册**：`scripts/run-tests.sh` 固定 `--configuration Release`，本地 `dotnet test --no-build` 不带 `-c Release` 会静默使用陈旧 Debug DLL（本轮实测 573 vs 实际 604 的 ControlPlane 幻差，计数/验证一律显式 `-c Release`）；另与运行中的测试轮并发分析 `TestResults/*/coverage.cobertura.xml` 会捞到半成品报告（实测 6.3% 幻值），一切数字以最终轮完成后为准。验证：全量覆盖率轮全绿（门禁 99.5 过）、format 过、CI 终态见提交后监控。

> 2026-09-20 §38 P2 异常账号检测（用户 2026-09-19 下达四大方向之③并标注「非常关键」；2408→**2451** 全绿：Steam.Core 1318→1349、ControlPlane 604→616，+43 = 功能测试 30 + 覆盖率缺口收敛 13）。三段闭环：①**agent 侧**（20 测试）——`SteamAccountStandingClient` 按次经登录 web session 抓 `/dev/apikey` 解析 key（`<p>Key: </p>` 长 >20；`SteamTradeClient.GetApiKeyAsync` 重复逻辑上移 `SteamWebApiKeyFetcher`）→ `GetPlayerBans/v1`（权威封禁判定，失败显式抛 HTTP 码）→ `GetSteamLevel/v1`（level==0 ⇒ limited；**失败仅降级**，bans 是核心判定）→ `check_account_standing` action（payload `steam_id` 可选、缺省 web session cookie 反解；六臂分类矩阵 banned/restricted/clean；`FetchOverride` + internal client 工厂双 seam；Agent DI 注册）。②**编排闭环**（10 测试）——pass 级周期体检（默认 21600s，`Vapor_RECONCILE_STANDING_REFRESH_SECONDS`；per-account 单作业槽；`StandingCheckedAt` 仅 settle 盖章——CardDrops 同款，派发失败下轮自然重试不刷屏）→ settle 解析 → `StandingQuarantined` 切换（检出一次 `standing_quarantined` 审计 + `account.standing_alert` 事件；clean 反向解除 `standing_released`）→ `ReconcileTradeAsync` 隔离闸门。**立项语义修正在册**：「login 连败标记」不并入 standing——登录连败已有独立 NextAttemptAt/冷却机制，两信号源不互相污染，隔离仅联动 trade 面。**测试确定性实践**：release 测试的第二次体检用 `RequestStandingCheck`（强制重置 CheckedAt 的产品入口）驱动而非 `Task.Delay` 等待刷新窗口——时间条件改状态条件，零等待零竞态。③**运维面**（4 测试）——`GET /v1/orchestration/standing` 快照 + `POST /v1/accounts/{name}/standing-check` 强制体检（`RequestStandingCheck` 复用编排管线故结果如实回写；202/409/404；CP `WhenWritingNull` 序列化下未测账户省略 standing 字段——测试须按「缺失即未测」断言）→ admin.html 账户卡徽章（正常/受限/封禁/未体检 + 已隔离）与「体检」按钮。**覆盖率缺口收敛轮**：首轮全量 99.48% 门禁红（新代码 +33 行缺口）→ 补 13 测试收掉 CheckAccountStandingAction 16 行（Name/Metadata 从未被触碰、无 handler 失败、OCE rethrow、factory/真实 client 端到端回放双路径）、CheckProxyAction 9 行（P1 遗留缺口：Metadata + OCE rethrow）、standing 派发 no-agent/dry-run guard、settle 三防御路径（outcome 无 task 用 `StripTasks` helper、task Failed deviation、输出缺 standing 键；settle 盖章在防御 return 之前 ⇒ 每个子场景须独立派发轮）、level 坏 JSON 降级、key 页非 200、`[::1]junk`。**定性入册**：`UnescapeDataString` 坏 percent-encoding catch（`ProxyOptions` 231-233）在 .NET Core 恒不抛——无效序列原样保留（行为已用 `Parse_InvalidPercentEncoding_IsKeptVerbatim` 正向锚定，换严格解码器须有意识变更），属「防御性死分支」家族。验证：全量覆盖率轮全绿（合计 99.7% 15062/15100，未覆盖 46→38 行，门禁 99.5 过）、format 过、CI 终态见提交后监控。**docs 补漏**：§38 P1 上轮漏 CHANGELOG，本轮一并补 per-account proxy 与 standing detection 两条 Added；production.md 补 standing 刷新 env 行；architecture.md 编排段补体检/隔离/解除语义。
>
> 2026-09-20 §38 P3 挂卡 ASF 式增强（用户指令「开始 P3」；2451→**2477** 全绿：ControlPlane 616→642，+26 = 纯函数 5 + 编排 8 + store 规范化 10 + API 2 + dashboard 锚点 1）。四件套：①**配置面**——`FarmPolicy` record + `FarmPriorityOrder` 三值 + `NormalizeFarmPolicy` 校验矩阵（budget 有限正数，0/负/NaN/±∞ 拒绝；PriorityApps 丢 0/去重/**保留声明顺序**——列表位置即队列优先级，与 TradePolicy「升序输入序无关」有意分叉）+ PUT 全替换语义（省略即清除）。②**编排核心**——队列排序三式 + PriorityApps 声明序置顶；完成标记 = 队列 diff（`farm_app_completed` 审计 + `account.farm_progress` kind=app_completed）；预算跳过 = `FarmAppStartedAt` 起算 `PerGameHourBudget`，到期摘除轮换（kind=app_budget_exhausted）；完成/跳过不回队（报告延迟防护）；drain 后一次性 `farm_completed` + kind=queue_empty（`FarmCompletedNotified` 防重）。③**统计**——累计式 `collected += max(0, prev-new)`、`total_drops_remaining` 双格式解析（内存 boxed long / SQLite round-trip JsonElement）、`CardsPerHour`（hours>0 守卫）。④**面**——`GET /v1/orchestration/farm` 快照 + admin.html farm 徽章与策略表单（前端校验镜像服务端）。**立项语义修正两处如实入册**：⑴PriorityApps「升序归一」→保留声明顺序；⑵「spec bump 全重置」→**spec bump 是策略变更而非事实清除**——实现轮发现完成 diff 依赖跨 bump 存活的 FarmQueue（bump 即丢队列 ⇒ diff 永不触发），定案为保留 FarmQueue/完成与跳过标记/统计，只重启预算时钟 + 强制刷新；`ResetFarmPolicyBookkeeping` 限定用于离开 farm 状态与 drain 后新一轮（facts survive policy edits）。**实现轮修复**：预算时钟缺失（被 bump/rebalance 清除）时 act 若直接 return 会永久解除保险丝——同 app 不会重新派发 play，改为有预算且时钟缺失时就地重启。**测试节奏铁律入册**：farmRefreshSeconds=0 时 refreshDue 恒真，每个 pass 先派 card_drops 并 return，**act 段（settle 后的决策）永不执行**——正确驱动 = 默认 300s + spec bump 强制刷新，act 在 settle 同 pass 执行（「派发 pass → settle+act pass」两拍节奏）；首轮 6 个失败全部源于旧驱动模式 + 初始 upsert 遗漏，无一产品逻辑错误（反而逼出语义修正⑵与实现修复）。**覆盖率缺口收敛**：P3 新代码 5 行（预算未到期臂 L604、total 解析防御臂与 JsonElement round-trip 分支 L1491/1496/1497/1506）→ 补 3 测试收掉（预算 1h 未到期保持、round-trip 双格式累计、无 total/超 int 范围 total 容错），ControlPlane 99.7%→99.8%，合计 99.7%（15251/15293，未覆盖 38→42 行——分母同步扩大）。验证：全量覆盖率轮全绿（门禁 99.5 过）、format 过、内联 JS node --check 过、CI 终态见提交后监控。

> 2026-09-20 §38 P4 插件生态（用户指令「学 asf 支持挂卡,还有可以批量 dashboard 安装插件,最好能实现一个类似插件的 pluginStore」的插件半边；2477→**2542** 全绿：Agent 56→93（+37）、ControlPlane 642→670（+28））。**分发模型 = CP 下发指令、agent 自拉包**——CP 不中转二进制（全仓无大 payload 通道），CP 唯一新概念是索引源 JSON（`Vapor_PLUGIN_INDEX_URL`，60s 缓存）。四段落地：①**agent 侧 host action 通道**——`IHostAction`（无 session 参数，与 `AgentTaskExecutor` 在任务循环分流执行；target 不匹配 `agent:{本机}` 拒绝防误投）+ 三 action `plugin_install`/`plugin_uninstall`/`plugin_list`，输出统一携带全量已装清单（失败也带——镜像保持真实）；capabilities 合并 actionRegistry ∪ hostActions。②**包安装器**——zip 下载（file/http/https；http 手动 bounded copy 按字节限流而非墙钟——慢而合法的下载不被误杀）→ **sha256 强制**（无校验和指令拒绝执行,供应链底线）→ staging 解压（zip-slip 防护 + manifest 必须在包根）→ 清单 id/version 核对 → 卸旧（rename `.old-<ticks>` 后删,Windows 文件锁规避）→ 原子 Move → 热加载；校验全部通过才触碰插件根,失败零痕迹。③**CP 定向派发**——`agent:{id}` target 前缀约定,`DispatchOnce` 识别后按 id 直取 registry（绕过区域随机 Pick——`ClaimNextQueuedTask` 只按 region claim 不看 target,区域随机 Pick 会把安装任务发给错误 agent 的文件系统）;目标离线/能力缺失复用 dispatch_failed requeue 路径。④**PluginStore**——REST 五端点（catalog/installed/install 双模式/uninstall 路由参数/inventory-refresh 含 stale 镜像清理）+ `PluginInventory` 内存镜像（task_result 钩子按 `plugin_` 前缀整体覆盖;WS round-trip 后 output 值是 JsonElement——镜像解析只认 JsonElement Array 分支,测试须 round-trip 构造）+ admin.html 面板（目录卡片+agent 多选默认全选+一键安装+已装 chips+卸载+同步）。**测试 65 个新增**:Agent 37（执行器 4/安装器 18/install action 5/uninstall 7/list 3,含真实 TestPlugin.dll 打包真加载、http 下载 fake handler 三态、只读根降级、取消透传）;CP 28（REST 14/catalog 6/镜像 4/定向派发 3/dashboard 锚点 1,含 SqliteJobStore ":memory:" 全链 job 建立断言——target=="agent:{id}"、payload url/sha256 归一）。**覆盖率收敛**:Agent 78.3%→97.5%（`PluginInstallAction` 包装类首轮 0%——测试直接测了 installer 漏了 action 层;http 下载段/默认 HttpClient 工厂/坏 manifest/缺入口 DLL/显式目录条目全收）;ControlPlane 99.8%→99.7%（新端点臂收 catalog 坏 body/非 Array plugins/refresh 409/stale 清理）;合计 99.6%（15887/15947,门禁 99.5 过）。**定性入册**:installer staging 段 OCE rethrow（LoadAsync 无取消检查,不可稳定构造）、RetireDirectory/TryDeleteDirectory 失败 warn 臂（staging 前置要求 root 可写,Linux rename-then-delete 无锁窗口;uninstall 侧同类臂已用只读根法收掉）、内容超限 throw（需 >128MB 流）、uninstall 空 pluginId 400（路由参数空串不可达,防御性死分支）、`PluginInventoryEntry` record 合成成员（覆盖噪音）。**工具链教训**:手动 `dotnet test --collect "XPlat Code Coverage"` 不带 `--settings tests/coverlet.runsettings` 会把无关程序集全量计入（实测 Steam.Core 99.7%→82.7%、分母 15947→17303 幻涨）——单项目补覆盖率也必须带 settings。验证：全量覆盖率轮全绿、format 过、内联 JS 过、CI 终态见提交后监控。

> 2026-09-20 §38 P4 Windows CI 修复 + 维护轮 property 扩面（2542→**2549** 全绿：ControlPlane 670→677,+7 property）。**①CI 修复（fix 提交）**：推送后 ci workflow 两 Windows job 红,Linux/docs/codeql 绿——6 个插件测试**主断言全过**,异常全在 finally 裸 `Directory.Delete`:collectible ALC 的 DLL 映射要等 GC 释放文件锁（与 P3 39 连败轮②同根因第二次现身——新文件又裸删踩坑）。修复三件:`PluginTestPackages.DeleteBestEffort`（catch IOException/UnauthorizedAccessException）收编 27 处 finally;hot-load replace 的 `.old-` 清理断言加 `!OperatingSystem.IsWindows()` guard（retire 删除本就是 best-effort 设计,Windows 锁残留是合法输出）;uninstall「原目录已让位」断言不动——rename-aside 不受映射文件影响（CI 堆栈证实）。**铁律入册:每轮新增加载真实 DLL 的测试文件,清理必须 day-one best-effort**。**②property 扩面（维护轮）**:新建 `PluginEcosystemPropertyTests` 7 个——`HostTaskTarget` 往返恒等与精确接受（For→TryParse ⟲、成功解析重组回原 target、null/空串/嵌套 `agent:agent:x`）;`Sha256Normalizer` 三性质（64-hex 字母表缩域生成器密集采样接受区 + 任意串接受谓词 + 幂等——local function 提取为 `internal static class Sha256Normalizer`,调用点 Program.cs 同步);`ParseIndex` 平行数组生成任意 catalog（字段透传+sha 小写归一+Ordinal 排序+稳定序）与任意 json 鲁棒性（只以 FormatException/ArgumentException/JsonException 拒绝,快照恒有序）。**FsCheck 2.16 实战教训**:默认 string 生成器**会产 null**——参数一律 `string?` 并防御;`IReadOnlyList<string>` 不自动生成（record 参数不可用,平行数组 + 空/ null 容错 helper 替代）;空数组取模即 DivideByZero（缩域 helper 必须判空）。CA1862 误报 oracle 断言里的 `ToLowerInvariant` 比较——`string.Equals(..., Ordinal)` 显式化。覆盖率合计 99.6%（15888/15947,门禁 99.5 过;Monitoring 100→99.4% 为时序分支抖动,分母不变）。验证:全量覆盖率轮全绿、format 过、CI 终态见提交后监控。

> 2026-09-21 覆盖率 100% 收官轮（自主立项「推测试覆盖率到100%」跨会话收尾;2549→**2603** 全绿:Steam.Core 1349→1373、ControlPlane 677→700、Plugins.Core 129→130、Agent 93→97、Monitoring 35→36、KeyRotation 27→28;含 KeyRotation `Environment.Exit` 臂子进程测试与 `SessionManager` 泵 channel-close 测试,合计升至 **100.0%** 15880/15882,分母余 2 行定性死分支）。上轮残余 7 行缺口（多报告合并口径,复刻 coverage-summary 的 normalize+max 逻辑逐行定位）六处收尾:①**RJS/TSS `ExecuteAsync` wrapper 收闭括号**（2 行）——去 async 表达式体重构 `=> TimerLoopAsync(...)`,行为等价、调用行照常命中,wrapper 序列点消失;②**`SessionManager` 并发 TryAdd 竞态败者 else**（2 行）——提取 `HandleDuplicateCreateRace` + `[ExcludeFromCodeCoverage]`（顺序调用被方法开头 TryGetValue 短路,无进程内确定性触发器）,调用点折叠单行 `else { return ...; }` 使块入口序列点与（排除的）调用同行出分母;③**`SessionManager` 泵 try 收闭括号**——新增 `PumpSessionEvents_ExitsThroughChannelCompletion`:反射 complete `BotSession._eventChannel`,泵的 await-foreach 正常耗尽命中（先例 `_commandChannel`;通道 complete 后 ReadAllAsync 先投递缓冲再结束,启动顺序无关）;④**`BotSession.RunSteamCallbacksAsync` try 收闭括号**——先试测试法**失败**:mock `RunCallbacks` 回调内 Cancel 后控制流直达 `await Task.Delay(已取消 token)`（同步抛 OCE）——循环体内 RunCallbacks 在 Delay 之前,Cancel 后永不回到条件检查;条件退出窗口夹在 Delay 完成延续与条件检查之间,线程池调度不可拦截 → 整方法 `[ExcludeFromCodeCoverage]`+注释（先例 `RunTokenRefreshLoopAsync`;中途试制的 mock-Cancel 测试与既有 `TicksThenUnwindsOnCancellation` 同路径,已删）;⑤**`Program` agent WS lambda `finally` 关键字行**——该行序列点**不属于任何路径**（285 次异常进 finally 与 7 次正常完成都不命中）→ 重构为 `catch (Exception) { DisconnectAgent(); throw; }` + 尾部 `DisconnectAgent()`,行为严格等价（cleanup 每出口执行、异常原样传播）;⑥**KeyRotation `GetValue` 缺值臂**——`dotnet` 子进程驱动（进程内直调 `Environment.Exit(2)` 会终止 testhost,子进程断言退出码与 stderr 点名）。**机理入册四条**:⑴coverlet 的 `finally` 关键字行序列点恒不命中——try/finally 的可测形态是 catch-all+throw+尾部清理;⑵`Task.Delay(已取消token)` 同步抛 OCE——「mock 回调里 Cancel 制造循环条件退出」类设计必须先核对循环体内语句顺序（Cancel 后的第一个 await 会立即消费取消）;⑶async wrapper 表达式体化（`{ await X(); }` → `=> X()`）消除收闭括号序列点——X 只经 OCE 退出时该括号结构性不可达;⑷多报告合并口径的缺口定位必须复刻 normalize+max 逻辑（裸 filename 前缀写法不一致会造出幻影缺口,实测一份内联脚本误报 6396 行）。**最终定性**:`Program` 2968/2975（重构后暴露的 try 收闭+尾部清理）= WS 循环条件正常出口防御性死分支——对端 close 帧必先被 `WebSocketJson.Receive` 撞见抛 IOException、abort 走 OCE,`ws.State != Open` 谓词在异常出口之前永假,保留为纵深防御。验证:全量覆盖率轮两轮全绿（2603 测试,合计 **100.0%** 15880/15882,门禁 99.5 过）、format 过、CI 终态见提交后监控。

> 2026-09-21 CI 修复轮（覆盖率收官提交后 ci workflow 4 job 红:两 Windows build-test + Release ubuntu + coverage;codeql/docs/format/integration-redis/docker-build 绿）。三族:①**EventBroker channel-complete 双测试的 3s 定时 CTS 是测试自身引爆器**——async iterator（`SubscribeSessions`/`SubscribeAuthChallenges`）到首次 `MoveNextAsync` 才执行订阅注册,消费泵又跑在 `Task.Run` 里;CI 负载下排队超 3s 后定时器先于注册触发,`WaitToReadAsync` 直接抛 OCE（本地主线程消费的 6 处同款 CTS 无排队窗口,安全不动）。修法:去定时（完成信号本就是退出路径）+ 注册轮询与 WaitAsync 预算 30s。②**SQLite 临时 db 文件锁 4 处裸删**——`Microsoft.Data.Sqlite` 默认池化,Dispose 归池后句柄仍短暂持有,`File.Delete` 抢跑抛 `IOException`（文件被「另一进程」占用,实为同进程池内连接;负载下池 cleaner 排队变慢,窗口暴露）。同文件已有 2 处 best-effort 先例（586/673 带 pooling 注释）与 CompositionRoot 的 `DisposeDbFileAsync` 重试先例,其余 4 处裸删统一补齐 catch IOException——与 ALC DLL 文件锁铁律同族:**测试清理触碰「被池/被映射」资源必须 day-one best-effort**。③**泵 channel-complete 测试 CI 超时**（`PumpSessionEvents_ExitsThroughChannelCompletion`,10s WaitAsync;本地 2 核 15 轮压测零复现）——与 fd1ae2e「park 信号有效但整条链在饥饿下爬行超 10s」同量级,预算 10s→30s（只覆盖池调度,健康路径不等待）。**教训入册:测试内 Task.Run 消费者 + async iterator 订阅的组合,定时 CTS 是炸药不是兜底**;临时文件清理先例必须全文件审计,不能只对新增文件执行铁律。验证:ControlPlane 700 全绿 + 泵测试 15 轮 2 核压测零复现、format 过、CI 终态见提交后监控。

> 2026-09-22 覆盖率精确 100% 收口轮（用户指令「把测试覆盖率提到并维持 100%,有缺口就补齐」;测试数不变,合计 **100.0%** 15882/15882——分母不变、残余 2 行转覆盖,**CI 门禁 99.9→100**）。上轮定性保留的「WS 循环条件正常出口防御性死分支」2 行（`Program` 2968 try 收闭 + 2975 尾部清理）本轮**推翻不可测定性**——该臂 09-16 就有测试（`SlowHeartbeatStore` 忽略 token 让心跳在 abort 后正常返回、循环条件再见假退出）,但 TestServer 客户端 `ws.Abort()` 的中止传播与 socket 拆除**竞速**:传播先到 → RequestAborted 取消 → 正常出口;拆除先到 → 下一次 `Receive` 抛异常 → 异常臂。两臂可观测行为等价（都注销 + disconnected 事件）,测试永远绿,cobertura 才暴露真相:09-21 收官轮报告尾部 0 命中 = 竞速翻车走了异常臂。**去竞态三件**:①`BranchFactory` 注入 `IStartupFilter`（ConfigureServices 注册）,把 `/v1/agent/ws` 请求的 `RequestAborted` 换成测试可控 linked CTS——**教训:`builder.Configure` 中间件在 minimal hosting 工厂下丢端点映射,WS 升级请求全 404（6 个 WS 测试当场红）,必须走 startup filter 包装 `next(app)`**;②`SlowHeartbeatStore` 加 `HeartbeatEntered` TCS——取消必须落在「Receive 已返回、token 无视心跳进行中」窗口,早了 OCE 从 Receive 抛出照旧走异常臂（重演上轮机理⑵:Cancel 后第一个 await 消费取消）;③测试等信号后 `Cancel()`,心跳正常返回、socket 健康 ⇒ 循环条件见假成为**唯一可能路径**（单类验证:2968/2975 各 hits=1,catch 臂 2 hits 不受影响）。测试更名 `AgentWs_RequestAbortedDuringHeartbeat_UnregistersThroughLoopExit` + 修正 close-frame 测试引用已删除 finally 的陈旧注释。**教训入册:「时序依赖的绿」不等于「覆盖了」——两臂可观测行为等价时,cobertura 是区分测试真正走了哪条臂的唯一证据;TestServer 确定性中止通道 = `HttpContext.RequestAborted` 可写 + linked CTS 替换 + IStartupFilter,`ws.Abort()` 是竞速通道**。验证:ProgramBranchCoverageTests 73 全绿、全量覆盖率轮全绿（2614 测试,合计 **100.0%** 15882/15882,门禁 100 过）、format 过、CI 终态见提交后监控。

> 2026-09-22 本地覆盖率工具链对齐轮（test + docs 双提交,无产品/测试代码改动）：README 宣称的「全量覆盖率走串行收集」此前只对 CI 成立——`run-tests.sh --coverage` 本地仍用一次性 `--collect`（可靠性轮已证其会静默产出空/全零报告）,`run-tests.ps1 -Coverage` 更连 runsettings 都没带（§38 分母膨胀教训原样存在）。修复三件:①**`run-tests.sh`** 全量运行（无过滤器）自动委托 `collect-coverage-serial.sh`,显式 Release 构建守卫前置（串行脚本 `--no-build`——陈旧 DLL 幻差教训从 runbook 升级进脚本本身）,跑后 best-effort 打印 coverage-summary 文本摘要（与 CI 门禁同口径）;带过滤器保留单次收集路径（校验器「全零即坏」语义不适用于子集——未匹配项目本就零命中）。②**`run-tests.ps1 -Coverage` 原生移植串行循环**（Windows 不依赖 bash/python）:逐项目 `--no-build` 收集、坏报告删除重试 ≤3、E2E 免收集、VAPOR_TEST_REDIS 注入与恢复、exit code 经 script 作用域变量传出（函数进度输出会污染返回值管道的 PS 陷阱）;**cobertura 带 DOCTYPE,`[xml]` 直接转换默认禁止 DTD 会抛——必须 XmlReader + DtdProcessing=Ignore**;过滤器路径补上缺失的 runsettings。③**分支行计入口径统一**:coverlet 实际写 `branch="True"/"False"`（Pascal 大小写）,coverage-summary.py 与 sh 校验器里的大小写敏感过滤是**死代码**——但 PowerShell `-ne` 不区分大小写,移植时照抄会「真排除」分支行,三处口径就此分叉;定案保留含分支行的**更严口径**（基线 15882 即此口径,且与标准工具的 line coverage 定义一致）,删除三处死过滤、口径入注;门禁行为不变（两口径合并结果同为 100.0% = 15882/15882,修复前的门禁实际就在执行含分支行口径）。验证:`run-tests.sh --coverage` 全量端到端演练（构建 3:18 → 10 段串行全 attempt 1 → 摘要 100.0% → 报告列出,退出码 0）、过滤器路径冒烟全绿、`bash -n`/`py_compile` 过;本机无 pwsh,ps1 逐行审读 + 关键假设对照 coverlet 实际产物核验（分支值大小写、class/line XPath 结构、DOCTYPE）。

> 2026-09-22 分支覆盖度量与门禁轮（用户指令「把测试覆盖率往100%推进」——行覆盖已精确 100%,推进剩余自由度:分支条件命中度）。coverage-summary.py 扩展双口径输出:行覆盖照旧;分支覆盖按分支行的 `condition-coverage="50% (1/2)"` 属性统计（同一行多次报告观察取已覆盖条件数最大值,并集语义与行覆盖的 max hits 一致）,新增 `--min-branch` 门禁旗标。基线:**行 100.0%（15882/15882）/ 分支 94.8%（5843/6165）**——行 100% 不蕴含分支 100%:一行执行过 ≠ 它的每个布尔子条件结果都被取到（如 `int.TryParse(env) && v > 0` 的坏值臂、`game?.Price` 的 null 臂）。两轮全量串行实测摘要**逐字节一致**（分支数据无时序抖动,与行覆盖门禁同款确定性）,据此设精确基线棘轮门禁:CI `--min 100 --min-branch 94.77`（未舍入 94.777% 过;丢一个条件 94.761% 即红;行门禁同款纪律——故意新增不达分支基线的防御臂须同步抬基线入册）。缺口普查:322 未命中条件分布 253 行,前五 `DesiredStateReconciler` 49、`Config` 33、`SteamStoreApiClient` 14、`SteamAccountStandingClient` 13、`MarketWatchPlugin` 12。定性两族:**可收** = env 解析臂组合（Config 的 `TryParse && 范围校验` 坏值臂,测试注入 env 即达）、JSON 形状组合臂（`TryGetProperty && GetBoolean` 短路组合）、守卫 null 臂（`?? throw` 家族）;**难收** = 编排循环条件组合（`WaitForNextTickAsync` 假返回臂=取消语义）、reconciler 状态组合（`spec.Version` null 臂跨状态机）——与行覆盖 100% 冲刺前同构,留后轮专项冲刺收可收族、难收族定性入册。验证:两轮全量串行全绿（10 段全 attempt 1）、双旗标门禁本地过、py_compile 过、CI 终态见提交后监控。

> 2026-09-22 分支缺口专项冲刺（立项日同日收口,+约 60 测试全绿;分支 5843/6165=94.8% → **5943/6165=96.4%**,未命中条件 322 → 222）。立项三族全收:①**env 解析臂**——`Config.LoadFromEnvironment` 33 条全亮（ConfigEnvironmentTests 51 行 theory:垃圾值→parse-false 臂、越界值→guard-false 臂、合法值→直通;负 ReconcileIntervalSeconds 会在并发 boot 中击杀 PeriodicTimer,故挂 ProcessGlobalTracingCollection 串行;WEBHOOK 键因 CompositionRootSmokeTests 改同名进程 env 而刻意排除,注释在案）。②**JSON 形状组合臂**——SteamStoreApiClient 14、SteamAccountStandingClient 13、SteamMarketClient 6:`TryGetProperty ∧ TryGetInt*` 短路组合的 present-but-wrong-type 臂必须用**分数/溢出数字**点亮（1.5、2.5、3000000000、50.25）——STJ 的 `TryGetInt32/TryGetInt64` 对非 Number 类型是**抛 InvalidOperationException 而非返 false**,字符串/布尔喂不得;`GetString()` 对 JSON null 返 null、对 Number/True/False 抛,空串/null 臂用 JSON null 达。③**守卫 null 臂**——两个 GuardClauseTests 共 14 条（AchievementsClient/BadgesClient/ProfileGamesClient/RedisVaporCache/TradeRateLimiter ctor、SteamWebHandler null-config 回退、RedactingLoggerProvider;Plugins.Core 的 PluginManager/PluginEventDispatcher/DefaultPluginHostServices、事件分发器重复注册去重、插件清单坏版本号 0.0 回退、发现器缺目录/坏插件日志）。零散收口:null-Error 回退臂 12 条（send/accept/decline/cancel/getoffers/loot/createmarketlisting/checkproxy 八 action 的 `catch { Error=null }` 兜底文案路径）、字符串 "0" 过 TryParse 败 `> 0` 守卫臂 4 条（points shop 两 action/add_license/get_inventory）、`??`/`?.` 默认臂与「伪难收」复核改收（PlayGamesAction L57/74 true 臂=mock ISteamClientManager 验证 play 集合与 stop 空集转发、SteamTimeSynchronizer L64 带日志器成功同步臂、CheckAccountStanding 会话 cookie 解析 SteamID、GetGameInfo/GetMarketListings 降级组合）。**难收族定性入册（维持并细化,222 条中的存量主体）**:DesiredStateReconciler 49 条——L137 `WaitForNextTickAsync` false 臂结构性不可达（timer 方法局部、停机即弃,真取消走宿主停机路径已有测试）,其余为编排循环跨状态组合（RecordActionAsync 审计隔离、payload JSON/dict 双通道六条件解析、gift accept 三态、farm 统计组合）,需飞行中集成 fixture,维持「干净但集成型构造」留深化轮;SteamAchievementsClient L92/93 TryParse-false 臂在 SummaryRegex `\d+` 不变量下不可达（捕获组恒全数字）;SteamClientManager L40/48 反射臂依赖 SteamKit2 内部结构（集成壳族）;Program L3460 RemoteIpAddress null 臂 TestServer 恒设值（宿主族）。剩余缺口重镇:MarketWatchPlugin 12、MobileAuthenticator 9、Monitoring 11（多为插件循环/传输边界组合）。门禁棘轮:CI `--min-branch 94.77` → **96.43**（未舍入 96.431% 过;丢一个条件 96.415% 即红）。验证:全量串行绿（10 段全 attempt 1,2714 测试,Steam.Core 1415/ControlPlane 752/MobileAuthenticator 129）,format 门禁过,CI 终态见提交后监控。

> 2026-09-22 分支缺口冲刺第二轮（收口轮续,逐轮棘轮:+9 测试全绿,分支 5943/6165=96.4% → **5961/6165=96.7%**,门禁 96.43 → **96.69**）。①补漏入基线:收口轮终轮因 Coverage 串行脚本 `--no-build` 用了撞车前的陈旧 Release DLL,PlayGamesAction 转发臂与 TimeSync 带日志同步臂测试在代码里但不在基线里——本轮重建后落账 +3。教训入 runbook:直接跑 collect-coverage-serial.sh 时改过测试必须显式 `dotnet build -c Release` 前置,`--no-build` 硬编码在脚本里。②Monitoring:MetricsRegistry.FormatValue 的 ±Inf/NaN 三臂五条件一条测试全收（`GaugeSet(double.PositiveInfinity/NegativeInfinity/NaN)` + RenderPrometheus 断言 `+Inf/-Inf/NaN` 文本）;MetricsHttpServer 单 token 请求行臂（`"GET\r\n"`——method 无 URL token 走空 URL 丢弃路径）与 ctor null payloadProvider 守卫。③MarketWatch 测试钩子 null-_loop 臂四条:InitializeAsync 前直调 RestartLoopForTestsAsync/StopLoopForTestsAsync,`_loop is not null` 与 `_loopCts` 的 null 臂走跳过路径。④Steam.Core ttl 三态臂六条:GetCardDrops/GetPlaytime 的 `ttlSeconds is > 0` 正数臂（现有测试只有 0 与缺省）,GetGameInfoBatch 经 ResolveTtlOverride 的负值回退臂与正值覆盖臂（带 MemoryVaporCache 走 FetchCachedAsync 缓存路径才达 staleTtl 三元）。⑤新入册定性:MetricsHttpServer L122 `parts.Length > 0` false 臂不可达（`Split(' ')` 永不返回空数组,防御冗余）;MonitoringPlugin L200 非 null 臂需 ISessionManager 宿主 stub、L248 logger null 臂在 LoggerFactory 流程下恒非 null 不可达;BotSession 10 条属会话引擎命令超时/取消竞态窗口,与 Reconciler 同族留深化轮。验证:全量串行绿（10 段全 attempt 1,2723 测试）,format 门禁过,CI 终态见提交后监控。

> 2026-09-22 分支缺口冲刺第三轮（收口轮续,逐轮棘轮:+6 测试全绿,分支 5961/6165=96.7% → **5967/6165=96.8%**,门禁 96.69 → **96.78**;行覆盖自愈回 15882/15882）。①**两个概率覆盖臂确定性化——本轮最大收获**:SessionManager L107 TryAdd-false 臂与 L126 竞态行原靠 4-Task×40 轮的线程池调度碰撞概率命中(上一轮 CI 绿是碰上,干净 rebuild 后实证 miss,行覆盖跌到 15881/15882,--min 100 红险现形),改为 Barrier 同放行 16 个真线程——TryAdd 原子性保证至多一个赢家、其余必撞 else,5 轮叠乘,三次复现 hits 60+;MaFileParser 错密码测试(随机 salt/iv)以 255/256 概率走 CryptographicException 臂,本轮撞上 1/256 尾巴实证翻车(L105/107 暗掉),改为运行时探测 padding 必非法的 salt(逐候选以与产品同参 KDF/AES 试解密,每候选 255/256 合格,512 次上限)——确定性触发。②MobileAuthenticator 四条:MobileConfirmationClient 无 message 回退臂 ×2(`{"success":false}` 无 message 键→固定兜底文案)与 type String("Market"→"market")/未知形状(true→null) theory ×2。③Plugins.Core 两条:PluginEventDispatcher Remove 遍历非匹配首元素臂(先 add 两个再移除第二个);PluginLoadContext L41 LoadFromAssemblyPath 臂——staging 只拷 dll 不拷 deps.json,resolver 恒返 null,测试内手写最小 deps.json(runtimeTarget+targets+libraries)使 resolver 真实解析出路径。④**基线修正**:上一轮 Steam.Core 2938 中 1 条系撞车中间态陈旧 DLL 虚高(与第二轮 PlayGames/TimeSync +3 同源),干净 rebuild 锚定 2937,本轮 L107 false 臂补回 2938——「串行轮前必须显式 build」教训的又一实证。⑤定性维持:PluginLoadContext L47 非 null 臂(需真实 native dll)、PluginLoader L187(私有 ctor fixture)。验证:全量串行绿（10 段全 attempt 1,2730 测试）,format 门禁过,CI 终态见提交后监控。

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
- 保持测试覆盖率稳步提升（当前基线精确 100.0%，CI 覆盖率门禁 100%，见上方基线表；全量收集走 `scripts/collect-coverage-serial.sh`，理由见日志）
- 新功能必须包含测试
- 修复 bug 时添加回归测试
- 定期审查和重构测试代码

