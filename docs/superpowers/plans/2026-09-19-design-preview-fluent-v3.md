# 速译 v3.0 Fluent 预览稿 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 按设计文档产出 `design-preview/` 纯静态高保真交互原型（5 页、浅深双主题、file:// 双击可看）。

**Architecture:** 每窗口一页 HTML；共享层四件套（tokens/base/pages CSS + preview/icons JS）承载主题、组件与演示逻辑；页面间相对链接互跳，hash 路由切分区。

**Tech Stack:** 手写 HTML/CSS/vanilla JS，零依赖零构建，无外部资源（图标为内嵌 SVG）。

**Spec:** `docs/superpowers/specs/2026-09-19-ui-redesign-fluent-preview-design.md`（布局与内容清单以 spec §4–§5 为准，本计划不重复其色值表）

## Global Constraints

- 零依赖：不引任何 CDN/npm/字体文件；字体栈 `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`。
- 直接以 `file://` 打开必须工作：经典 `<script>`（无 ES modules）、相对路径、`fetch()` 禁用；`localStorage` 包裹 try/catch。
- 颜色只允许引用 `tokens.css` 变量，页面 CSS 中禁止出现 `#RRGGBB` 字面量（遮罩/选区等"双主题同值"令牌除外，它们本身就是令牌）。
- 双主题：`<html data-theme>` + `localStorage('sv-theme')` + 默认 `prefers-color-scheme`；每页右上角主题按钮。
- 动效 ≤200ms，缓动 `cubic-bezier(0.1,0.9,0.2,1)`；`prefers-reduced-motion: reduce` 时全部禁用非必要动画。
- 125%/150% 缩放无横向滚动。
- 每个任务结束：浏览器走查该任务列出的验收断言 → `git commit`。验收截图目录 `design-preview/screenshots/` 加入 `.gitignore`。

## 文件结构

```
design-preview/
  index.html      总览：品牌头、5 页入口卡、令牌展示、组件状态矩阵、v2/v3 差异表
  quick.html      翻译小窗（含对比模式、状态演示条）
  settings.html   设置主窗（NavigationView + 7 分区 hash 路由）
  pin.html        钉图窗（悬浮工具条）
  capture.html    截图选区全流程 + 任务栏/托盘菜单/气泡
  assets/tokens.css   全部 CSS 变量（light/dark 两块 + 同值令牌）+ 噪点/材质基元
  assets/base.css     reset、字阶、窗口 chrome、组件库（按钮/输入/开关/下拉/卡片/键帽/toast/滚动条/徽标/滑杆/分段控件/列表行）
  assets/pages.css    五个页面各自的布局样式（按页分节注释）
  assets/icons.js     `window.Icons = { name: '<svg…>' }` + `icon(name, cls)` 挂载器
  assets/preview.js   主题切换、toast、模拟词典 translate()、假延迟、键帽录制器、hash 路由、演示状态机
```

**共享 API 约定（各页依赖，签名冻结）：**
- `Preview.initTheme()` — 页尾调用；读取/应用主题并给 `.js-theme-toggle` 绑定切换。
- `Preview.toast(msg)` — 底部居中气泡，2s 自动消失。
- `Preview.translate(text) -> Promise<{text, engine, fallback?}>` — 命中 `Preview.dict` 返回译文；未命中返回按规则构造的占位译文并带 `demo:true`；首次调用固定模拟一次 `fallback:'Bing'`（Google→Bing 降级演示）；600ms 假延迟。
- `Preview.recorder(el)` — 把 `.js-hotkey` 键帽框变为"点击→录制→按键回填/冲突提示"演示。
- `Preview.route(sections, defaultSec)` — settings 用：hash `#general` 等 ↔ 导航选中态 ↔ 分区显隐。
- 图标名：`copy star pin sound swap chevron compare close search trash tune cloud monitor keyboard image text`（`Icons.copy` 等，值为 svg 字符串；`icon(name)` 返回 DOM）。

---

### Task 1: 共享层 + 最小骨架

**Files:**
- Create: `design-preview/assets/tokens.css` `base.css` `pages.css`(空壳) `icons.js` `preview.js`
- Create: `design-preview/index.html`（仅品牌头 + 主题按钮，供验证共享层）
- Modify: `.gitignore`（追加 `design-preview/screenshots/`）

**Interfaces:** Produces 上述全部共享 API（后续任务只调用不再改签名；发现 bug 可修实现）。

- [ ] Step 1: 按 spec §4 写 `tokens.css`：light/dark 两套变量（值照抄 spec §4.1 表），加 `--ring`(焦点环 2px)、`--reveal`(悬停高光 `rgba(255,255,255,0.06/0.04)` 按主题)、Mica 背景渐变、2% 噪点 data-URI、`--dur:160ms`、`--ease:cubic-bezier(0.1,0.9,0.2,1)`。
- [ ] Step 2: 写 `base.css`：reset、body 应用 `--window` 材质、字阶类 `.t-caption/.t-body/.t-subtitle/.t-title`、窗口壳 `.win`（标题条 `.win-bar` 32px + 圆角 8px + Level8 阴影 + `.win-stroke`）、组件：`.btn .btn-accent .btn-tonal .btn-ghost .btn-icon`（5 状态 + `:focus-visible` 焦点环）、`.input .textarea`、`.switch`（40×20 Fluent 胶囊）、`.select`（自绘下拉：按钮 + `.menu` 弹层）、`.card .card-hover`（SettingsCard：左图标+标题/描述、右控件插槽）、`.expander`、`.chip`（徽标）、`.kbd`（键帽）、`.segmented`、`.slider`、`.toast`、`.list-row`、细滚动条 `::-webkit-scrollbar` 6px、`prefers-reduced-motion` 总开关。
- [ ] Step 3: 写 `icons.js`（16 个 24px 线性 SVG，stroke 用 `currentColor`）与 `preview.js`（共享 API；`Preview.dict` 先放 6 条：`Hello→你好`、`Serendipity→意外发现美好事物的运气`、`The quick brown fox jumps over the lazy dog→敏捷的棕色狐狸跳过懒狗`、`Machine learning→机器学习`、`Where is the station?→车站在哪里？`、`Concurrency→并发`）。
- [ ] Step 4: 写 `index.html` 最小版（品牌头 + 主题按钮）+ `.gitignore` 追加。
- [ ] Step 5: 浏览器打开验证断言：点击主题按钮两套色板整页换肤且刷新后记忆；控制台零报错；`file://` 直开正常。
- [ ] Step 6: Commit `feat(design-preview): 共享令牌、组件库与演示脚本骨架`。

### Task 2: quick.html 翻译小窗

**Files:** Create `design-preview/quick.html`；修改 `pages.css` 追加 `/* quick */` 节

- [ ] Step 1: 按 spec §5.2 写页面：模拟桌面背景（渐变+两张假窗口）、420px 小窗（自绘标题条：`译`标 + 中央语言胶囊 `自动▾ ⇄ 中文▾` + 复制/☆/×）、输入区、发丝线、译文区（空态/结果态 fade-up）、引擎徽标行（对比/朗读/钉图文本按钮）、28px 状态栏。页面右下角放「演示控制台」（按钮切换：空/输入后/加载中/结果/降级/错误/对比 七态直达）。
- [ ] Step 2: 接共享 API：Enter/按钮 → `Preview.translate()` 流程；语言胶囊点 ⇄ 互换（120ms 旋转）+ 两侧 `▾` 开语言下拉（各 8 语言）；复制 toast「已复制译文」；☆ 点亮常亮；「对比」→ 双列卡片（Bing/Google 两栏假结果）；朗读钮仅做按下态。
- [ ] Step 3: 验证断言：词库命中显示译文、未命中显示占位译文并带「示例数据」灰徽标；首次翻译出现一次「Google 连不上，已用 Bing 重试」灰字；错误态红字+「重试」可回到结果；两主题下 420px 窗内无横向溢出；控制台零报错。
- [ ] Step 4: Commit `feat(design-preview): 翻译小窗页`。

### Task 3: settings.html 壳 + 前 3 分区（通用/热键/翻译）

**Files:** Create `design-preview/settings.html`；`pages.css` 追加 `/* settings */`

- [ ] Step 1: 960×640 窗口：左 260px NavigationView（搜索框示意 + 7 项导航 + 选中 pill 与 3×16 accent 指示条）、右内容区（`.t-subtitle` 页头 + 卡片区）、hash 路由 `#general|#hotkey|#translate|#engine|#history|#vocab|#advanced` 接 `Preview.route()`。
- [ ] Step 2: 通用=主题 segmented（跟随系统/浅色/深色，即时生效联动全站）、开机自启 switch、托盘气泡 switch、小窗默认尺寸两个 number input +「记住位置」switch。热键=3 个 `.js-hotkey` recorder（划词/输入/截图，预填 `Alt+S`、`Alt+D`、`Alt+A`，录制中键帽变 accent 描边「请按键…」，演示冲突提示红条）。翻译=当前引擎 select（7 项，Bing/Google 带「免密钥」chip）、备用引擎顺序列表（上移钮示意）、对比引擎多选、源语言/目标语言 select、TTS switch + 划词自动朗读 switch。内容清单照 spec §5.3 对应三条。
- [ ] Step 3: 验证断言：7 个 hash 直达可切；主题 segmented 改变全站色板；recorder 按键回填生效；控件无写死色。
- [ ] Step 4: Commit `feat(design-preview): 设置窗壳与通用/热键/翻译分区`。

### Task 4: settings.html 后 4 分区（引擎/历史/生词本/高级）

**Files:** Modify `design-preview/settings.html`、`pages.css`

- [ ] Step 1: 引擎=顶部说明卡 + 7 引擎卡（Bing/Google 显示「免密钥可用」绿 chip；腾讯/百度/Azure/DeepL 密钥 `.input type=password` + 显示/隐藏钮 + 「测试连接」→ 1s loading → success chip「已配置」+ 随机一张可达性警告条示例；AI 卡=端点/模型/温度 slider/提示词 textarea 的 expander 展开项 + 「使用免费版端点」switch）。
- [ ] Step 2: 历史=搜索 input（即时过滤假数据 8 条 list-row：原文/→译文/引擎 chip/时间/☆/删除）+ 空状态「暂无翻译记录…」；生词本=6 词条行 + 删除 + 导出 CSV/Anki `.btn-tonal` + 空状态；高级=代理卡片组（启用 switch、地址/端口、认证折叠项、作用范围 segmented 三值）、OCR 语言 select、模型常驻 switch、钉图缩放 stepper、遮罩不透明度 slider（右侧实时百分比）、重置设置 ghost 红字按钮。
- [ ] Step 3: 验证断言：历史搜索过滤生效且清空后回全量；密码显隐切换；测试连接状态机完整；两主题全分区渲染无溢出。
- [ ] Step 4: Commit `feat(design-preview): 设置窗引擎/历史/生词本/高级分区`。

### Task 5: pin.html 钉图窗

**Files:** Create `design-preview/pin.html`；`pages.css` 追加 `/* pin */`

- [ ] Step 1: 模拟桌面背景上钉一张「界面截图」（CSS 画英文段落卡片，320×260）；图上叠半透明原文/译文浮层（空格键或按钮切换，120ms 淡入）；底部 acrylic 胶囊工具条：`− 100% + | 透明度slider | 原/译 | 复制 | 朗读 | ☆ | ×`；页面提供「冻结悬停态」示意开关（常隐/常显工具条）。
- [ ] Step 2: 缩放钮改变截图 scale(1→1.5)；透明度 slider 改整窗 opacity(0.4→1)；复制 toast。
- [ ] Step 3: 验证断言：工具条 blur 生效、静止演示钮工作、双主题正常。
- [ ] Step 4: Commit `feat(design-preview): 钉图窗页`。

### Task 6: capture.html 截图选区 + 托盘

**Files:** Create `design-preview/capture.html`；`pages.css` 追加 `/* capture */`

- [ ] Step 1: 全屏模拟桌面（壁纸渐变 + 两张假窗，其中含英文段落）；页面加载即进入「待框选」态：55% 遮罩、十字光标、按住拖拽出选区（accent 2px 描边、四角+四边 8 手柄、左上角尺寸徽标 `W×H`）；松手在选区下方出亚克力小工具条（识别并翻译 / 复制图片 / 取消(×)）。
- [ ] Step 2: 「识别并翻译」→ 遮罩淡出 → 选区右下角落出 mini quick 窗（复用 Task 2  markup 结构，预填假 OCR 文本并自动走 `Preview.translate()` 演示）；右下角任务栏条（48px 高，时间/托盘图标区）：点「译」图标弹 acrylic 托盘菜单（5 项，各项右列 `.kbd` 热键示意，hover 高亮）+ 「速译已就位」气泡样例（右上角，可关闭）。
- [ ] Step 3: 验证断言：拖拽选区跨浏览器可用（pointer events）、取消回「待框选」、联动小窗动画流畅、双主题正常。
- [ ] Step 4: Commit `feat(design-preview): 截图选区与托盘页`。

### Task 7: index 总览页 + 全局验收

**Files:** Modify `design-preview/index.html`、`pages.css`（`/* index */` 节）

- [ ] Step 1: 填充总览：5 页入口卡（各卡内嵌一个 `transform:scale()` 的 iframe 缩略或 CSS 示意图 + 打开链接）、令牌展示区（色板 swatch 网格、字阶样例、间距/圆角示意）、组件状态矩阵（按钮五态 × 四型 + 开关/下拉/输入/键帽/徽标样例）、spec §3 的 v2→v3 差异表。
- [ ] Step 2: 全局走查：5 页 × 2 主题逐页截图到 `design-preview/screenshots/`；核对 spec §5 每页元素清单勾稽表（列出缺失项并补齐）；`prefers-reduced-motion` 模拟一遍；125%/150% 缩放检查；三处空状态文案齐全（历史/生词本/小窗）。
- [ ] Step 3: 修完走查发现项；`README.md`「界面设计」节顶部加一行：v3.0 预览稿入口 `design-preview/index.html`（WPF 未落地）。
- [ ] Step 4: Commit `feat(design-preview): 总览页与全局验收修复` + 按需追加验收修复 commit。

## 验收清单（整体）

- [ ] 双击 `design-preview/index.html` 起，5 页全可互跳、双主题记忆、控制台零报错。
- [ ] CSS 无写死色（grep `#[0-9A-Fa-f]{6}` 仅 tokens.css 命中）。
- [ ] spec §5 元素清单逐项可指认。
- [ ] WPF 源码与测试零改动（`git diff` 范围仅 design-preview/、README、.gitignore、docs/）。
