# P0 第三批功能设计：双击修饰键 · 鼠标侧键 · 粘贴即译 · 术语反向保护 · 看板 P50

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-20 |
| 状态 | 已实现（提交链 `191c257`…批 3 末次提交，1083 项测试全绿；截图 `artifacts/ui/batch3-*.png` 目检通过） |
| 来源 | `docs/改进建议-v2-功能与使用方式.md` 批 3（B1-1 / B1-2 / B1-4 / A2-3 / A2-4）；用户指令「除了电商包不做其余全做」 |
| 编号 | FR-038 双击修饰键划词 · FR-039 鼠标侧键映射 · FR-040 粘贴即译 · FR-041 术语反向保护 · FR-042 看板 P50 延迟 |

## 0. 红线（B5，全批适用）

- 新钩子（键盘 LL / 鼠标 XButton）**默认关**、设置开关一键全关、**隐私模式开启时绝不安装**。
- 一切新入口不给译文落定链路增加可感知延迟：双击/侧键触发即现有 Alt+S 链路；粘贴即译省掉的是一次往返。
- 钩子只观察不拦截（`CallNextHookEx` 永远透传）：侧键按下时浏览器仍会正常"前进/后退"——这是可选映射的固有代价，卡片文案如实写明。
- 不新增 NuGet、不新增网络行为。

## 1. FR-038 双击修饰键划词

- Core `KeyboardButtonHook`（`WH_KEYBOARD_LL`，与 `MouseButtonHook` 同款纪律）：事件 `KeyDown(vk, tickMs)` / `KeyUp(vk, tickMs)`，只观察。
- Core `ModifierKeyDoubleTapDetector(Func<long> nowMs)` 纯状态机（可单测）：
  1. 目标修饰键（默认 Alt，可选 Ctrl/Shift/Win）按下→抬起记为第一次；
  2. 第一次抬起后 ≤ **250ms** 内再次按下同一键 → 触发；
  3. 按住期间混入任何其他键（Alt+Tab / Alt+Space）→ 本次作废；
  4. 触发后 **500ms** 冷却；
  5. 前台窗口是本程序 → 不触发（复用钩子提供的 self 标志）。
- 触发动作固定 = 划词翻译（`TranslateSelectionAsync`），不做成可配（YAGNI，文档 B1-1 的默认候选即此）。
- 设置：`DoubleTapTranslateEnabled`（默认 false）+ `DoubleTapKey`（"alt"）；「通用」页「双击修饰键」卡片（与悬停卡同款布局 + 隐私锁定提示行）。
- 门控：`ApplyPrivacySideEffects` 扩展——键盘钩子安装条件 = `DoubleTapTranslateEnabled && !PrivacyMode`。

## 2. FR-039 鼠标侧键映射

- `MouseButtonHook` 扩展：观察 `WM_XBUTTONUP`（0x040C），事件 `XButtonUp(int button, bool foregroundIsSelf)`，button=1(X1 后退)/2(X2 前进)。
- 设置：`MouseSideButtonSelect`（X1=划词，默认 false）、`MouseSideButtonCapture`（X2=截图翻译，默认 false）。
- 鼠标钩子安装条件改为三选一：`HoverSelectEnabled || MouseSideButtonSelect || MouseSideButtonCapture`，且 `!PrivacyMode`（`ApplyPrivacySideEffects` 与设置 VM 同步改）。
- 前台是本程序时忽略；触发分别调 `TranslateSelectionAsync` / `ScreenCaptureTranslateFlow.StartAsync`。
- UI：悬停卡下方新增「鼠标侧键」卡片，说明「侧键按下时浏览器仍会执行前进/后退（本程序只观察不拦截）」。

## 3. FR-040 粘贴即译（小窗内 Ctrl+V）

- 交互：小窗**输入框为空**时按 Ctrl+V → 不粘贴，直接取剪贴板文本走完整链路（清洗→翻译）；输入框非空时保持普通粘贴（用户可能续写/编辑）。
- 实现：`QuickWindow` 输入框 `PreviewKeyDown` 拦截 Ctrl+V → 读 `Clipboard.GetText()`（异常按空处理）→ 事件 `PasteTranslateRequested(string?)` 交给 App：
  非空 → `ShowForSelection(PrepareCapturedText(text, out cleaned), cleaned)`（与划词/监听同一条清洗入口）；
  空 → 状态行「剪贴板里没有文本」。
- 设置：`PasteTranslateEnabled`（默认 **true**）；「翻译」页「粘贴即译」卡片，说明「小窗输入框为空时 Ctrl+V 直接翻译剪贴板内容（含阅读清洗）；关闭后 Ctrl+V 恢复普通粘贴」。
- 不新增剪贴板监听器，读一次即止（B5：不新增常驻监听）。

## 4. FR-041 术语反向保护

- 规则：`GlossaryReplacer.Apply(translated, items, sourceText)` 新增可选参数**原文**；某词条替换前，若其 `Target` 已出现在原文中（忽略大小写、子串匹配）→ 判为冲突，**跳过该词条**并计入 `Conflicts`（`GlossaryResult` 增加 `IReadOnlyList<GlossaryReplacement> Conflicts`）。
- 动机（v2 A2-3）：Target 撞日常词时后置替换会把不该换的换掉；原文里已存在 Target 说明该词对当前内容不可靠，宁可不换。
- `GlossaryTranslator` 装饰器把 `TranslateAsync` 收到的原文传入（唯一 chokepoint，调用点零改动）。
- `TranslationResult` 增加 `int GlossaryConflicts`（默认 0，record 加字段编译期暴露构造点）。
- UI：小窗徽标文案扩展为「术语 ×N · 冲突跳过 ×M」（M=0 时不显示后半句），tooltip 列出冲突词条；设置页词条列表不做静态标灰——冲突判定依赖当次原文，静态无依据（如实收窄文档表述）。

## 5. FR-042 引擎看板 P50 延迟

- 存储：`EngineStats` 表加列 `Latencies TEXT NOT NULL DEFAULT ''`——逗号分隔的成功请求耗时样本（ms），每引擎×日**上限 200 条**（超出丢最旧）；`HistoryDatabase.Initialize` 用 `PRAGMA table_info` 检测缺列则 `ALTER TABLE ADD COLUMN`（旧库自动迁移）。
- 记录：`GlossaryTranslator` 装饰器 `Stopwatch` 计时，仅**成功**且未 `SuppressStats` 时把 latencyMs 传给 `EngineStatsRepository.Record`（只记耗时数字，不破「绝不存用户文本」）。
- 汇总：`EngineStatSummary` 增加 `double? P50Ms`（近 N 天全部样本取中位数；无样本 = null）。
- UI：引擎卡统计行追加「· P50 0.8s」（<1s 显示 ms，否则一位小数秒）；「近 7 天未使用」文案不变。

## 6. 设置字段汇总（AppSettings，全部向后兼容）

| 字段 | 默认 | FR |
|---|---|---|
| `DoubleTapTranslateEnabled` | false | 038 |
| `DoubleTapKey` | `"alt"` | 038 |
| `MouseSideButtonSelect` | false | 039 |
| `MouseSideButtonCapture` | false | 039 |
| `PasteTranslateEnabled` | true | 040 |

## 7. 测试计划

- `ModifierKeyDoubleTapDetectorTests`：窗口内/超窗、按住混键作废、冷却、目标键过滤、self 前台。
- `GlossaryReplacerTests` 扩展：Target 在原文 → 跳过 + Conflicts 记录；sourceText=null → 行为与批 1 完全一致（回归）。
- `EngineStatsRepositoryTests` 扩展：latency 样本追加/200 截断/P50 中位数（奇偶样本数）/无样本 null/旧库 ALTER 迁移。
- `AppSettingsTests` 扩展：5 个新字段默认值 + 旧 JSON 兼容。
- 装饰器：TranslateAsync 传原文 → 冲突计数进 `TranslationResult.GlossaryConflicts`。

## 8. 边界与不做

- 双击键不做组合键扩展（双击 Alt 之外的键 = 单选一键）；不做「双击触发截图」。
- 侧键不做可重映射到任意动作（两枚开关覆盖 90% 需求）。
- 粘贴即译不做「输入框非空时替换全部内容」；不做富文本/图片剪贴板。
- P50 不做 P95/直方图（列宽与口径先满足"能直连里最快的"选型需求）。
