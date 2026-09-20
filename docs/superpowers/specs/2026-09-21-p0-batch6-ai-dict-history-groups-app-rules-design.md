# P0 第六批功能设计：AI 词典层 · 历史会话分组 · 按应用语言对 · 首次上手卡

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-21 |
| 状态 | 实现中 |
| 来源 | `docs/改进建议-v2-功能与使用方式.md` 剩余可排期项：A2-1 · B4-2 · B3-2 · B3-3（A1 电商包已剔除；v1 P2 #13~16 属"先做可行性探针，不排期"） |
| 编号 | FR-056 AI 词典释义层 · FR-057 历史按会话分组 · FR-058 按前台应用记忆语言对 · FR-059 首次运行 30 秒上手卡 |
| 排期 | 6a = FR-056 + FR-057；6b = FR-058 + FR-059 |

## 0. 红线与总约束

- 不给译文落定加 >50ms：AI 词典是**译后旁路**的第二次请求（与 mdx 词典同一时机），历史分组是纯内存整理，
  语言对规则是本地字符串匹配，上手卡只在首次启动后单独成窗。
- AI 词典会**花用户的 API 额度** → 默认关，设置页写明"每次查词多发一次请求"。
- 前台进程名只用于本机规则匹配：**绝不写日志、绝不出网、绝不入历史**（B5 红线精神）。
- 不新增 NuGet；纯逻辑下沉 Core 可单测；新设置字段进 `AppSettings`（JSON 向后兼容）。

## 1. FR-056 AI 词典释义层（A2-1）

- 定位：v2 文档里 A2-1 是 mdx（#11）的"轻量前置版"——**不导词典库也能给词头 + 音标 + 简明释义**。
  两者共用小窗同一张「词典卡」，mdx 命中优先（离线、免费、权威），没命中且开启 AI 词典时才发第二次请求。
- Core `AiDictionaryParser`（纯函数）：把模型返回解析成 `AiDictionaryEntry(Wordhead, Phonetic, IReadOnlyList<string> Senses)`。
  - 期望 `{"wordhead":"abandon","phonetic":"/əˈbændən/","senses":["v. 放弃；抛弃","n. 放任"]}`；
  - 容忍 ```json 代码围栏、前后散文、单引号、尾部逗号（解析失败 → null，卡片不显示，绝不显示半截 JSON）；
  - `senses` 最多留 6 条、单条 ≤160 字符（防模型写小作文把小窗撑爆）。
- Core `LlmPrompt.BuildDictionaryRequest(word, targetLanguage)`：独立短提示，要求"只输出 JSON、不要解释"。
- 通道：`LlmTranslator` 增加 `TranslateDictionaryAsync(word, targetLanguage, ct)`（复用同一 HTTP/超时/错误映射，
  但**不计入引擎看板**：它不是用户感知的"翻译请求"，混进统计会让 P50 与失败率失真）。
- 门控：`AiDictionaryEnabled`（默认 false）+ 输入是单词（≤32 字符且无换行）+ 当前引擎是 AI + mdx 未命中。
- 隐私：不额外加门（该词本来就在同一次出网请求里），但**不写历史**（词典答案不是译文）。

## 2. FR-057 历史按会话分组（B4-2）

- Core `HistoryGrouper.Group(records, windowMinutes = 30)`（纯函数）：按时间倒序的记录切组，
  相邻两条间隔 >30 分钟即断组；组标签 `HH:mm–HH:mm · N 条 · 首条原文前 24 字`。
  单条自成一组（不打"1 条"的折）。
- 设置页「历史」页加「按会话分组」开关（`HistoryGroupedView`，默认 true——分组只是折叠，不丢信息）：
  开 = 组标题 + 可展开列表；关 = 现有平铺列表。搜索关键字时**自动退回平铺**（跨组搜索结果按相关性排，分组无意义）。
- 只改视图层：`HistoryRepository` 与导出/删除逻辑零变化（删除仍按单条 Id）。

## 3. FR-058 按前台应用记忆语言对（B3-2）

- Core `AppLanguageRules`（纯逻辑）：
  - `Match(rules, processName)` → 命中的语言对（进程名不区分大小写，去 `.exe`）；
  - `Learn(rules, processName, source, target, maxRules = 20)` → 新规则置顶、同名覆盖、超上限丢最旧；
  - `Normalize(processName)`（空/超长/非法字符 → 丢弃）。
- 存储：`AppSettings.AppLanguagePairs`（`List<AppLanguagePair{Process,SourceLanguage,TargetLanguage}>`）。
- 行为（`AppLanguageMemoryEnabled`，默认 false）：
  - **呼出小窗时**：命中规则 → 用规则里的源/目标语言覆盖本次会话（只改会话，不改全局默认，避免"设置页看着没动却行为变了"的困惑）；
  - **用户在会话内改目标语言时**：把该前台程序 + 新语言对写进规则（这就是"记住"，不需要额外按钮）；
  - 设置页「翻译」页列出已记住的规则，可逐条删除 + 总开关。
- 红线：进程名只在本机比较与存进 settings.json；日志只记"命中/未命中"，绝不记进程名与语言对内容。

## 4. FR-059 首次运行 30 秒上手卡（B3-3）

- 独立小窗 `OnboardingWindow`（不是小窗内嵌卡片——内嵌会被"秒开秒关"的使用节奏挤掉）：
  三个键帽（Alt+D 输入翻译 / Alt+S 划词翻译 / Alt+O 截图翻译，文案取当前设置里的真实热键）
  + 一句「现在就试：选中任意一段字，按 Alt+S」+「知道了」按钮。
- 出现时机：`AppSettings.OnboardingShown == false` 时，启动完成后延后 1.2s 弹出一次；点「知道了」或 `Esc`
  即置 true 并持久化；设置页「通用」留一个「再看一次上手卡」按钮（改了热键后用户会想看第二遍）。
- 不做向导、不做多页、不收集任何数据。

## 5. 设置字段汇总

| 字段 | 默认 | FR |
|---|---|---|
| `AiDictionaryEnabled` | false | 056 |
| `HistoryGroupedView` | true | 057 |
| `AppLanguageMemoryEnabled` | false | 058 |
| `AppLanguagePairs` | 空表 | 058 |
| `OnboardingShown` | false | 059 |

## 6. 测试计划

- `AiDictionaryParserTests`：干净 JSON、代码围栏、前后散文、单引号、尾逗号、senses 超限截断、解析失败 → null。
- `LlmPromptTests` 追加：词典请求提示只要求 JSON、含目标语言与词头。
- `HistoryGrouperTests`：空表、单条、30 分钟边界（恰好 30 分不断组、31 分断）、乱序输入先排序、组标签文案。
- `AppLanguageRulesTests`：大小写与 `.exe` 归一、命中优先级（先匹配者）、学习覆盖同名、超上限丢最旧、非法进程名丢弃。
- 回归门禁：现有 1194 项全绿。

## 7. 边界与不做

- AI 词典不做图片/例句卡片、不做多义词排序优化（模型给什么就显示什么，超出条数截断）。
- 历史分组不做"项目命名/标签"（那是另一件事：需要新表与用户输入）。
- 按应用记忆不做窗口标题级匹配（标题会变、且更贴近"读取用户在看什么"，进程名已经够用）。
- 上手卡不做多页向导、不做联网检查。
