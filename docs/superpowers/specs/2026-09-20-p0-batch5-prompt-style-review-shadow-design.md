# P0 第五批功能设计：AI 语境化 · 一键换说法 · 写作档案 · 每日复习 · 影子跟读

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-20 |
| 状态 | 实现中 |
| 来源 | `docs/功能升级建议-竞品调研-v1.md` P1 #8（LLM 语境化）+ `docs/改进建议-v2-功能与使用方式.md` B2-2 / B2-4 / B4-1 / B3-1「写作」档 / A2-1 词典层合并评估 |
| 编号 | FR-050 AI 语境化翻译 · FR-051 一键换说法 · FR-052 生词本每日 5 词 · FR-053 影子跟读 · FR-054「写作」场景档案 · FR-055 多词典合并策略（A2-1 评估结论） |
| 排期 | 5a = FR-050 + FR-051 + FR-054（同一条 Prompt 链，必须一起落地）；5b = FR-052 + FR-053 + FR-055 |

## 0. 红线与总约束

- **不给译文落定加 >50ms**：语境与风格都只是 Prompt 里多几行字（本地拼字符串），不新增网络往返。
- **不多送一个字的用户内容出网**：语境来源必须已经在同一引擎的出网请求里（见 §1.3），隐私模式一律不携带。
- 不新增 NuGet；不新增常驻监听；影子跟读复用现有 `System.Speech`（SAPI），不做云端 TTS。
- 纯逻辑全部下沉 Core 可单测；新增设置字段进 `AppSettings`（JSON 向后兼容）。
- 换说法/语境是 **AI 引擎专属**能力：官方引擎（Bing/Google/腾讯/百度/Azure/DeepL）无对应通道，UI 直接不显示按钮，不做「假装支持」。

## 1. FR-050 AI 语境化翻译（v1 #8）

### 1.1 通道

`ITranslator` 不动（六家引擎零改动）。新增可选接口：

```csharp
public readonly record struct TranslationDirective(string? ContextBefore, TranslationStyle Style)
{
    public static readonly TranslationDirective None = new(null, TranslationStyle.None);
    public bool IsEmpty => ContextBefore is null && Style == TranslationStyle.None;
}

/// 引擎是否接受调用方附加的 Prompt 指令（当前只有 AI 引擎）。
public interface IPromptDirectiveTranslator
{
    Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage,
        TranslationDirective directive, CancellationToken cancellationToken = default);
}
```

`GlossaryTranslator` 转发（术语替换照旧作用于 `text`，`directive` 原样递给 inner），
`TranslatorCatalog` 加 `static bool SupportsDirectives(ITranslator t) => t is IPromptDirectiveTranslator`。

### 1.2 Prompt 构造

`LlmPrompt.Build(customPrompt, source, target, text, directive)`：

- 内置模板：追加两行（各自仅在有值时）——
  - 语境：`上一段原文（仅供理解上下文，不要翻译它，也不要输出它）：{...}`，
    语境文本**截尾 600 字符**（`MaxContextChars`，Token 预算与"别把上下文当正文"双重考虑）；
  - 风格：见 §2.2。
- 自定义 Prompt：只替换 `{context}` / `{语境}` 与 `{style}` / `{风格}` 占位符；
  **用户没写占位符就不追加**——尊重用户自定的 Prompt 结构，不偷偷往里塞内容（文档写明）。

### 1.3 语境来源（隐私纪律）

语境 = 与当前请求**同语言对**的最近一条历史原文（`HistoryRepository.LastSourceBefore(targetLanguage, currentSource)`），
即"连续翻译时上一段就是你刚译过的那段"。约束：

- 仅 AI 引擎、仅 `!PrivacyMode`（隐私模式历史本就不入库 → 自然无源）；
- 命中记录须在 30 分钟内（`ContextMaxAgeMinutes`，跨小时的上下文对连贯性无益、只增加出网字符）；
- 降级到备用引擎时不带 directive（备用多为官方引擎）；
- 日志只记「语境 {n} 字符」，绝不记内容。

## 2. FR-051 一键换说法（B2-2）

### 2.1 交互

小窗译文区下方一行轻按钮（仅 `SupportsDirectives(当前引擎)` 且有译文时显示）：
**更口语 / 更正式 / 更简短**。点已激活的那个 = 取消（回到无风格）。
点击 = 用同一份原文带 `Style` 重新请求，成功即**替换**译文，状态行「已换说法：更正式」。
换说法结果**不重复写历史**（避免污染 TM 回填的候选池），也不自动入生词本；
「重新机器翻译」按钮（FR-045）与风格指令互不干扰：TM 命中时同样可换说法。

### 2.2 风格指令（Core 常量，可单测）

`TranslationStyle { None, Colloquial, Formal, Concise }` + `LlmPrompt.StyleInstruction(style)`：

| 值 | 追加指令 |
|---|---|
| Colloquial | `风格要求：用口语化、自然的表达，避免书面腔与生硬直译。` |
| Formal | `风格要求：用正式、书面的表达，措辞严谨，避免口语与缩略说法。` |
| Concise | `风格要求：在忠实原意的前提下尽量简短，删去冗余修饰与重复。` |

### 2.3 持久化

`AppSettings.TranslationStyle`（string，默认 `"none"`，取值同枚举名）。小按钮点击即写；
「写作」档案可钉住它（§4）。重启后仍是上次的风格——与 `TargetLanguage` 同类语义。

## 3. FR-052 生词本每日 5 词（B4-1，默认关）

- `DailyReviewEnabled`（默认 false）。开启后**每天首次呼出小窗**时，结果区上方出现一条复习行：
  `今日复习 1/5：apple → 苹果`，右侧「下一个」「今天到这」。
- 选题为纯函数 `DailyReviewSelector.Pick(count, today, wordsPerDay)`：
  用「距 epoch 的天数」做轮转起点，取 `(day*5 + k) % n` 的下标序列 →
  **同一天稳定 5 个词、不做新表、不加列、不改 schema**（v2 文档要求的"最轻验证"，
  不是复习算法：不统计熟悉度，明确写进已知边界）。
- 当天进度记在 `AppSettings`（`DailyReviewDate` + `DailyReviewIndex`），换天自动归零；
  生词不足 5 个时全部给完就收起。生词本为空 → 复习行显示一句引导文案，不报错。
- 复习行是**只读展示**：不劫持输入框、不改变任何取词/翻译行为，隐私模式下照常工作（词就在本机，不上网）。

## 4. FR-054「写作」场景档案（B3-1 追加）

- `ProfileOverrides` 键集合从 8 扩到 9：新增 `string? Style`（同步 `FromSettings` / `ApplyTo` / `DeviatesFrom` 三处，类注释一并改）。
- 内置档案新增 `Writing`：`Engine=llm`、`Style=formal`、`CleanClipboardText=false`、`GlossaryEnabled=true`。
  **不钉住目标语言**——"写作"对俄语/英语用户含义不同，钉住会让切换档案变成改语言（稀疏语义的既定原则）。
- 顺序：阅读 → 隐私 → 写作（托盘子菜单、Alt+P 循环、设置页卡片自动跟随内置列表）。

## 5. FR-053 影子跟读（B2-4）

- Core `SentenceSplitter.Split(text)`：按句末标点（`。！？!?…；;` + 换行）切句，保留标点，
  合并 ≤2 字符的碎句，最长 200 字符强切；纯函数、单测覆盖中英混排。
- 小窗结果区新增「跟读」轻按钮（`CanSpeak` 为真且有译文时可用）：
  进入跟读模式 → 译文按句竖排，当前句高亮（左侧强调条 + 前景提亮），
  SAPI 逐句 `Speak` 并等 `SpeakCompleted`，句间停顿 `ShadowPauseMs`（默认 600ms）；
  再点一次 / `Esc` / 关闭窗口 = 停止并退出。跟读模式与「对照」视图互斥（同一区域两种竖排）。
- 实现分层：`SentenceSplitter` + 停顿/进度决策在 Core 可单测；
  SAPI 事件驱动循环放 `TranslationApp.App/Services/ShadowReadingRunner.cs`
  （订阅现有 `ITtsService`，不新建 SpeechSynthesizer、不抢 `SetOutputToDefaultAudioDevice`）。
- 边界：只做译文跟读（跟读原文对"贴 listing"用户无意义）；不做逐句复读、不做变速（SAPI 语速开关已有，跟读不重复造）。

## 6. FR-055 多词典合并策略（A2-1 评估结论）

FR-049 已实现「按列表顺序取首个命中」，够用且可预期（用户能靠排序控制优先级）。
本轮只补两件小事，不做词条级 merge：

- 词典卡标注命中来源（`词典 · 长语林`），让用户知道是哪本给的；
- 同名不同内容冲突时导入已自动加 ` (2)`（FR-049），列表里可分辨。

词条并集/交叉释义拼接**不做**：mdx 的 `Definition` 常是整页 HTML，拼接后可读性反而下降，
且会让"哪个词哪本词典给的"变得不可解释——记录为 A2-1 的评估结论，避免后续重复讨论。

## 7. 设置字段汇总

| 字段 | 默认 | FR |
|---|---|---|
| `LlmContextEnabled` | true | 050 |
| `TranslationStyle` | "none" | 051 |
| `DailyReviewEnabled` | false | 052 |
| `DailyReviewDate` | "" | 052 |
| `DailyReviewIndex` | 0 | 052 |
| `ShadowReadingEnabled` | false | 053（默认隐藏跟读按钮，语音卡里一键开） |
| `ShadowPauseMs` | 600 | 053 |

## 8. 测试计划

- `LlmPromptTests` 追加：语境追加/截尾 600、自定义 Prompt 无占位符则不追加、风格三档指令文案、语境与风格同时存在时的顺序。
- `GlossaryTranslatorTests` 追加：实现 `IPromptDirectiveTranslator` 的 inner 被转发（directive 原样到达）、未实现的 inner 走原路径。
- `LlmTranslatorTests` 追加：请求体 user 消息仍是原文本体（语境只进 system prompt）。
- `HistoryRepositoryTests` 追加：`LastSourceBefore` 同语言对 / 超龄 / 隐私库空 / 与当前输入相同则不取。
- `DailyReviewSelectorTests`：同一天稳定、跨天轮转、n<5、n=0。
- `SentenceSplitterTests`：中英文句末标点、引号收尾、碎句合并、200 字符强切、空文本。
- `ProfileServiceTests` 追加：`Style` 参与稀疏覆盖与偏离判定；内置「写作」档可切换且不动目标语言。
- 回归门禁：现有 1139 项全绿。

## 9. 边界与不做

- 语境不做"整篇文档滚动上下文"（会话级一段足够；多段拼接会把 Token 打爆）。
- 换说法不做自定义指令输入框（设置页 AI Prompt 已是自定义入口，不重复）。
- 每日复习不做熟悉度/遗忘曲线（那是 B4 复习闭环的正题，需要新表与新交互，单独立项）。
- 影子跟读不做原文跟读、不做变速与单句循环。
- 多词典不做词条 merge（见 §6）。
