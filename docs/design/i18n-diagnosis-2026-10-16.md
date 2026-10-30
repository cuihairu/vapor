English | 简体中文

# i18n Diagnosis Report — 2026-10-16

**Live symptom**: of the content served under the `/zh/architecture/` path, 32 characters are Chinese vs. 31460 characters English — i.e. the vast majority of pages on the Chinese site are still in English.

---

## 1. Preliminary correction: the site is not VitePress, it is MkDocs Material

Inspection confirms the repository has no `docs/.vitepress/` directory and no VitePress-related configuration of any kind. The actual setup is:

- Framework: MkDocs Material 9.6 + `mkdocs-static-i18n` 1.3
- Config file: `mkdocs.yml`
- Build pipeline: `.github/workflows/docs.yml`; pushes to `main` auto-deploy GitHub Pages

> The conclusions below are based on the mechanics of **MkDocs + mkdocs-static-i18n suffix-style i18n**.

---

## 2. Root-cause conclusion

**Root cause = missing documentation files (not enough Chinese versions committed) + the `fallback_to_default: true` mechanism falling missing pages back to the English original.**

Broken down:

1. **Only 3 `.zh.md` files are committed on the `main` branch** (`index.zh.md` / `getting-started.zh.md` / `running.zh.md`); the other 20 `.zh.md` files are still untracked in the local working tree. The live site can only read these 3 pages.
2. **`architecture.zh.md` does not exist at all** (not even in the local working tree), so `/zh/architecture/` inevitably falls back to English.
3. **`fallback_to_default: true` in `mkdocs.yml` is intentional design** (see the comment at `mkdocs.yml:69`): "if the Chinese version is missing, show English — leave no holes in the navigation". This explains why pages under `/zh/` open fine but are entirely in English.
4. **`reconfigure_material: true` switches the Material theme's UI strings to the `zh` language pack**, so the footer, navigation, search placeholder, etc. carry a small amount of Chinese interface text (about 32 characters, matching the measurement), while the body content is the English fallback.
5. **The locales configuration itself is correct** — this is not "missing locales config", not "the sidebar is not grouped by locale", and certainly not "a directory mapping error at build time".

---

## 3. Configuration status item by item (file:line)

| Config item | Current state | Location | Root cause? |
|---|---|---|---|
| i18n plugin enabled | Yes, `docs_structure: suffix`, en default + zh build | `mkdocs.yml:71-84` | No |
| `fallback_to_default` | `true` (a missing zh version falls back to English) | `mkdocs.yml:75` | Indirect (the mechanism produces the symptom) |
| `reconfigure_material` | `true` (theme UI switches with locale) | `mkdocs.yml:76` | No (explains the 32 Chinese characters) |
| Theme default language | `en` | `mkdocs.yml:17` | No (overridden by reconfigure under the zh locale) |
| `nav` grouped by locale | Single shared nav (normal behavior in suffix mode) | `mkdocs.yml:86` | No (not a bug) |
| CI installs the i18n plugin | Yes, `mkdocs-static-i18n==1.3.*` | `.github/workflows/docs.yml:38` | No |
| Build output path | `_site/`, uploaded as a Pages artifact | `.github/workflows/docs.yml:49-53` | No |

---

## 4. Chinese version coverage status

### 4.1 Committed to main (visible on the live site) — 3 / 23 pages

| Page | Status |
|---|---|
| `index.md` | ✅ `index.zh.md` |
| `getting-started.md` | ✅ `getting-started.zh.md` |
| `running.md` | ✅ `running.zh.md` |

Coverage: **13%** (counting the 23 pages in the nav).

### 4.2 Uncommitted in the working tree (WIP) — 17 new `.zh.md` files

`git status` shows the following `.zh.md` files as untracked:

`actions.zh.md`, `consistency.zh.md`, `data-dictionary.zh.md`, `dependencies.zh.md`, `docker.zh.md`, `domain-model.zh.md`, `feature-matrix.zh.md`, `game-plugins.zh.md`, `performance.zh.md`, `plugins.zh.md`, `releasing.zh.md`, `roadmap.zh.md`, `session-engine.zh.md`, `testing.zh.md`, `troubleshooting.zh.md`, `vnext-convergence-plan.zh.md`, `审计-文档一致性.zh.md`

Once committed, coverage can rise to ~87%.

> Of these, `roadmap.md`, `vnext-convergence-plan.md`, and `consistency.md` are not in the nav; they are unlisted pages.

### 4.3 Missing a Chinese version even in the working tree — 6 pages

| Page | Nav location |
|---|---|
| `architecture.md` | Design / Architecture |
| `api.md` | Reference / REST API |
| `production.md` | Getting Started / Production deployment |
| `design/admin-ui.md` | Design / Admin UI redesign |
| `research/market-survey.md` | Research / Market survey |
| `research/process-boundary.md` | Research / Process boundary |

Even after all WIP is committed, these 6 pages will still fall back to English under `/zh/`.

---

## 5. Fix checklist (by priority)

### P0 — Immediately fix "the zh site is basically unusable"

- **Commit the 17 `.zh.md` files sitting in the working tree**
  - Evidence: `git ls-files docs/ | grep '\.zh\.md$'` returns only 3 files
  - Effect: zh site coverage goes from 13% → ~87%
  - Action: `git add docs/*.zh.md` + commit + push, which triggers `docs.yml` to redeploy

### P1 — Translate the core pages to eliminate the highest-traffic English fallbacks

- **Translate `architecture.md`** (`docs/architecture.md`, 691 lines / 44 headings)
  - Evidence: the user observed `/zh/architecture/` in English; it is the Design group's entry page and one of the most visited pages
  - Output: `docs/architecture.zh.md`

- **Translate `api.md`** (`docs/api.md`)
  - Evidence: first item of the Reference group; API docs are a core developer path
  - Output: `docs/api.zh.md`

- **Translate `production.md`** (`docs/production.md`)
  - Evidence: the final step of the Getting Started group; users walk from onboarding all the way to production deployment
  - Output: `docs/production.zh.md`

### P2 — Translate the secondary pages

- Translate `design/admin-ui.md`
- Translate `research/market-survey.md`
- Translate `research/process-boundary.md`

### P3 — Experience polish (not the root cause, nice to have)

- **Add an "untranslated" notice banner at the top of fallback pages**
  - Suggested approach: with mkdocs-static-i18n, fallback pages can use `page.meta` or custom JS to detect a mismatch between the current locale and the actual content language, and show a notice bar
  - Evidence: users who enter the zh site and see all English currently assume it is a bug; a notice reduces confusion

- **Evaluate whether to keep `fallback_to_default: true`**
  - If switched to `false`, pages missing a Chinese version would be hidden from the zh navigation instead of falling back
  - Trade-off: no holes in the navigation vs. explicitly saying "not translated yet"
  - Evidence: `mkdocs.yml:75` is currently `true`

- **Naming cleanup for `审计-文档一致性.md`**
  - Current state: the working tree has renamed the original file to `.zh.md` and added an English version, but the nav still points at the Chinese name
  - Suggestion: point the nav at the English filename so it matches the naming style of the other pages (cleaner in suffix mode)
  - Evidence: `mkdocs.yml:119` currently points the nav at `审计-文档一致性.md`

---

## 6. One-sentence summary

> **The configuration is not broken — the translations were never shipped.** `fallback_to_default: true` guarantees every page under the `/zh/` path opens, but the content is English; commit and deploy the 17 `.zh.md` files from the working tree, plus complete the three pages `architecture` / `api` / `production`, and the zh site becomes basically usable.
