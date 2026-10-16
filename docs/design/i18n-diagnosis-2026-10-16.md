# i18n 诊断报告 — 2026-10-16

**线上现象**：`/zh/architecture/` 路径服务的内容中，中文 32 字 / 英文 31460 字，即中文站下绝大多数页面仍为英文。

---

## 一、前置校正：站点不是 VitePress，是 MkDocs Material

实测仓库无 `docs/.vitepress/` 目录，也无任何 VitePress 相关配置。实际配置为：

- 框架：MkDocs Material 9.6 + `mkdocs-static-i18n` 1.3
- 配置文件：`mkdocs.yml`
- 构建流水线：`.github/workflows/docs.yml`，推送到 `main` 自动部署 GitHub Pages

> 以下结论基于 **MkDocs + mkdocs-static-i18n 后缀式 i18n** 的运行机制。

---

## 二、根因结论

**根因 = 文档文件缺失（已提交的中文版不足）+ `fallback_to_default: true` 机制将缺失页回退到英文原文。**

具体展开：

1. **`main` 分支仅提交了 3 个 `.zh.md` 文件**（`index.zh.md` / `getting-started.zh.md` / `running.zh.md`），其余 20 个 `.zh.md` 尚在本地工作区未跟踪。live site 只能读到这 3 页。
2. **`architecture.zh.md` 根本不存在**（本地工作区也没有），`/zh/architecture/` 必然回退到英文。
3. **`mkdocs.yml` 的 `fallback_to_default: true` 是有意设计**（见 `mkdocs.yml:69` 注释），即"缺中文版就显示英文、导航不留洞"。这解释了为什么 `/zh/` 路径下能打开页面但全是英文。
4. **`reconfigure_material: true` 会把 Material 主题的 UI 字符串切换到 `zh` 语言包**，所以页脚、导航、搜索占位等有少量中文界面文字（约 32 字与实测吻合），但正文是英文回退。
5. **locales 配置本身是正确的**——不是"缺 locales 配置"，也不是"侧栏未按 locale 分组"，更不是"构建时目录映射错误"。

---

## 三、配置现状逐项（file:line）

| 配置项 | 现状 | 位置 | 是否根因 |
|---|---|---|---|
| i18n 插件是否启用 | 是，`docs_structure: suffix`，en 默认 + zh 构建 | `mkdocs.yml:71-84` | 否 |
| `fallback_to_default` | `true`（缺 zh 版就回退英文） | `mkdocs.yml:75` | 间接（机制导致现象） |
| `reconfigure_material` | `true`（主题 UI 随 locale 切换） | `mkdocs.yml:76` | 否（解释了 32 个中文字） |
| 主题默认语言 | `en` | `mkdocs.yml:17` | 否（zh locale 下被 reconfigure 覆盖） |
| `nav` 是否按 locale 分组 | 单 nav 共享（suffix 模式正常行为） | `mkdocs.yml:86` | 否（不是 bug） |
| CI 是否安装 i18n 插件 | 是，`mkdocs-static-i18n==1.3.*` | `.github/workflows/docs.yml:38` | 否 |
| 构建产物路径 | `_site/`，上传 Pages artifact | `.github/workflows/docs.yml:49-53` | 否 |

---

## 四、中文版覆盖现状

### 4.1 已提交到 main（live site 可见）—— 3 / 23 页

| 页面 | 状态 |
|---|---|
| `index.md` | ✅ `index.zh.md` |
| `getting-started.md` | ✅ `getting-started.zh.md` |
| `running.md` | ✅ `running.zh.md` |

覆盖率：**13%**（按 nav 中 23 个页面计）。

### 4.2 工作区未提交（WIP）—— 新增 17 个 `.zh.md`

`git status` 显示以下 `.zh.md` 为 untracked：

`actions.zh.md`、`consistency.zh.md`、`data-dictionary.zh.md`、`dependencies.zh.md`、`docker.zh.md`、`domain-model.zh.md`、`feature-matrix.zh.md`、`game-plugins.zh.md`、`performance.zh.md`、`plugins.zh.md`、`releasing.zh.md`、`roadmap.zh.md`、`session-engine.zh.md`、`testing.zh.md`、`troubleshooting.zh.md`、`vnext-convergence-plan.zh.md`、`审计-文档一致性.zh.md`

提交后，覆盖率可升至 ~87%。

> 其中 `roadmap.md`、`vnext-convergence-plan.md`、`consistency.md` 不在 nav 中，属于未挂出的页面。

### 4.3 连工作区都缺中文版—— 6 页

| 页面 | nav 归属 |
|---|---|
| `architecture.md` | Design / Architecture |
| `api.md` | Reference / REST API |
| `production.md` | Getting Started / Production deployment |
| `design/admin-ui.md` | Design / Admin UI redesign |
| `research/market-survey.md` | Research / Market survey |
| `research/process-boundary.md` | Research / Process boundary |

即使提交完所有 WIP，这 6 页在 `/zh/` 下仍会回退英文。

---

## 五、修复清单（按优先级）

### P0 — 立即解决"zh 站基本不可用"

- **提交工作区 17 个 `.zh.md` 文件**
  - 依据：`git ls-files docs/ | grep '\.zh\.md$'` 仅返回 3 个文件
  - 效果：zh 站覆盖率从 13% → ~87%
  - 操作：`git add docs/*.zh.md` + commit + push，触发 `docs.yml` 重新部署

### P1 — 补齐核心页面翻译，消灭高频英文回退

- **翻译 `architecture.md`**（`docs/architecture.md`，691 行 / 44 个标题）
  - 依据：用户实测 `/zh/architecture/` 为英文，是 Design 组入口页、访问量最高页之一
  - 产出：`docs/architecture.zh.md`

- **翻译 `api.md`**（`docs/api.md`）
  - 依据：Reference 组首项，API 文档是开发者核心路径
  - 产出：`docs/api.zh.md`

- **翻译 `production.md`**（`docs/production.md`）
  - 依据：Getting Started 组末环，用户从入门走到生产部署
  - 产出：`docs/production.zh.md`

### P2 — 补齐次级页面

- 翻译 `design/admin-ui.md`
- 翻译 `research/market-survey.md`
- 翻译 `research/process-boundary.md`

### P3 — 体验优化（非根因，锦上添花）

- **在回退页顶部加"未翻译"提示 banner**
  - 建议做法：mkdocs-static-i18n 的 fallback 页可用 `page.meta` 或自定义 JS 检测当前 locale 与实际内容语言不一致，显示提示条
  - 依据：目前用户进 zh 站看到全英文会以为是 bug，加提示可降低困惑

- **评估是否保留 `fallback_to_default: true`**
  - 若改为 `false`，缺中文版的页面在 zh 导航中会隐藏而不是回退
  - 取舍：导航无洞 vs. 明确告知"还没翻"
  - 依据：`mkdocs.yml:75` 当前为 `true`

- **`审计-文档一致性.md` 命名清理**
  - 现状：工作区已将原文件重命名为 `.zh.md` 并新增英文版，但 nav 仍指向中文名
  - 建议：nav 中改为英文文件名，与其余页面命名风格一致（suffix 模式更干净）
  - 依据：`mkdocs.yml:119` 当前 nav 指向 `审计-文档一致性.md`

---

## 六、一句话总结

> **不是配置坏了，是翻译没交上去。** `fallback_to_default: true` 保证了 zh 路径下每个页面都能打开，但内容是英文的；把工作区 17 个 `.zh.md` 提交部署，加上补齐 `architecture` / `api` / `production` 三页，zh 站就能基本可用。
