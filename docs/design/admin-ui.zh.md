简体中文 | [English](../../design/admin-ui/)

# 管理 UI 重设计（原型）

**状态：仅原型 —— 线上页面未改动。**
`src/Vapor.ControlPlane/wwwroot/` 下的三个后端页面
（`admin.html` / `dashboard.html` / `gamedata.html`）保持现有外观，
直到运维者审阅这些原型并批准重设计。

## 目标

用 **Steam 风格默认主题** 替换当前的浅绿主题：深蓝灰背景
（近 `#1b2838` / `#171a21`）、卡片布局、克制的蓝色高亮
（近 `#66c0f4`）、浅灰正文（近 `#c7d5e0`）。

主题层是**令牌化 CSS 变量**：Steam 为默认主题，其他主题可切换，日后新增主题
永远不需要改动页面结构。

## 页面覆盖

原型复现三个页面的**真实布局** —— 菜单、表格、状态卡、筛选栏与表单
均照实际页面而来，不是一套通用配色。展示的全部数据均为
示例数据。

| 原型 | 真实页面 | 覆盖内容 |
| --- | --- | --- |
| [`prototypes/admin.html`](prototypes/admin.html) | `admin.html` | Hero + 流状态 + API Key 框 + 跨页导航；5 张统计卡（任务 / 运行中 / 已完成 / Agent / 会话）；16 个面板：创建任务、任务列表、作业详情、Agent 概览、活动会话、认证挑战、账户管理、交易与确认、仓库归集、市场与认领、爬虫计划管理、配置管理、插件管理、脚本仓库、脚本编排、事件日志 |
| [`prototypes/dashboard.html`](prototypes/dashboard.html) | `dashboard.html` | Hero + 流状态 + 跨页导航；6 张统计卡（账户 / Agent / 会话 / 进行中 / 已完成 / 失败）；7 个面板：内部状态总览、账户、作业、审计日志、Agent、会话、实时事件 |
| [`prototypes/gamedata.html`](prototypes/gamedata.html) | `gamedata.html` | Hero + 跨页导航；5 个面板：数据来源与缓存层级（真实缓存表）、字段字典（真实 camelCase 键）、采集计划、轮次记录、结果浏览（筛选栏 + 分页器） |

枢纽页 [`prototypes/index.html`](prototypes/index.html) 链接到全部三个原型，
并一眼呈现主题系统。

## 主题令牌结构

`prototypes/theme.css` 分为两层：

- **结构令牌**（与主题无关）：`--space-*`、`--radius-*`、
  `--font-*`、`--fs-*`、`--dur-*`。
- **语义令牌**（`--t-*`）：`--t-bg`、`--t-sunken`、`--t-card`、
  `--t-card-raised`、`--t-input-bg`、`--t-fg`、`--t-fg-muted`、`--t-fg-subtle`、
  `--t-brand`、`--t-brand-strong`、`--t-brand-soft`、`--t-on-brand`、
  `--t-hero-grad`、`--t-border`、`--t-border-strong`、`--t-success`、
  `--t-warning`、`--t-danger`、`--t-info`、`--t-table-stripe`、`--t-shadow`。

目前内置四套主题；切换只是经由 `<html data-theme="...">` 覆写 `--t-*` 块
（见 `prototypes/theme.js`，持久化于 localStorage）：

| 主题 | `data-theme` | 观感 |
| --- | --- | --- |
| Steam（默认） | `steam` | 深蓝灰 `#171a21` / `#1b2838`，强调色 `#66c0f4`，文字 `#c7d5e0` |
| Midnight | `midnight` | 近黑中性色，低饱和蓝 |
| Daylight | `daylight` | 浅色背景、深蓝文字、同族蓝色强调色 |
| Ember | `ember` | 暖色暗底、琥珀色强调 |

`prototypes/components.css` 只消费令牌 —— 每个组件（hero、统计卡、面板、
字段栅格、表格、列表、筛选器、分页器、事件日志、主题选择器）都与主题无关，
因此新增一个主题只需再加一个 `[data-theme="..."]` 块。

## 截图

Steam 默认主题（全部四个页面）：

![Admin console](prototypes/screens/admin.png)

![Dashboard](prototypes/screens/dashboard.png)

![Game data](prototypes/screens/gamedata.png)

![Prototype hub](prototypes/screens/index.png)

## 文件地图

| 文件 | 角色 |
| --- | --- |
| `prototypes/index.html` | 枢纽页：总览 + 主题色板 + 令牌表 |
| `prototypes/admin.html` | 管理控制台原型（16 个面板） |
| `prototypes/dashboard.html` | 只读仪表盘原型（7 个面板） |
| `prototypes/gamedata.html` | 游戏数据字典原型（5 个面板） |
| `prototypes/theme.css` | 结构令牌 + 4 套语义主题（默认 steam） |
| `prototypes/components.css` | 组件层（令牌驱动） |
| `prototypes/theme.js` | 主题切换器（`data-theme` + localStorage） |
| `prototypes/screens/*.png` | 上述四个页面的整页截图 |

## 预览

直接在浏览器打开 `prototypes/index.html`（无需构建步骤），
或浏览文档站上的 `design/prototypes/index.html`。
主题选择器固定在每个页面的右上角。

## 遵守的约束

- 不复制任何 Steam 素材、图标或字体文件 —— 仅借鉴配色与布局气质；
  所有视觉均由 CSS 自绘。
- 原型是静态产物；无需为它们编写后端测试。
- 本阶段不改动 `src/Vapor.ControlPlane/wwwroot/` 下的线上页面。
