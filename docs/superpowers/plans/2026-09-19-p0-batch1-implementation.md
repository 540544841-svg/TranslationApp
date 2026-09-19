# P0 第一批（术语表/隐私/阅读清洗/引擎看板）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 按 spec `docs/superpowers/specs/2026-09-19-p0-batch1-glossary-privacy-clean-dashboard-design.md` 实现四项功能，Core 逻辑全部 TDD，WPF 层接线 + 设置页 UI。

**Architecture:** Core 新增 4 个纯逻辑件（GlossaryReplacer / TextCleaner / EngineStatsRepository / GlossaryTranslator 装饰器）；App 层只做门控与 UI；所有引擎调用经 TranslatorCatalog 出口统一包装饰器。

**Tech Stack:** .NET 10 / WPF / xUnit（现有 TranslationApp.Tests，975 项基线）。

**Spec:** 见上。本计划不重复 spec 规则细节，只排任务与验证。

## Global Constraints

- 不新增 NuGet 依赖；不新增网络行为。
- 每个 Core 任务先写失败测试再实现（TDD）；每任务结束 `dotnet test` 全绿并提交。
- 设置字段全部进 `AppSettings`（JSON 向后兼容，未知字段忽略的既有机制）。
- 微文案三铁律（动词开头/给出路/不甩错误码）。
- 测试运行命令：`dotnet test src/TranslationApp.Tests`（默认跳过联网用例）。

---

### Task 1: GlossaryReplacer（Core，TDD）

**Files:**
- Create: `src/TranslationApp.Core/Translation/GlossaryItem.cs`、`GlossaryReplacer.cs`
- Modify: `src/TranslationApp.Core/Settings/AppSettings.cs`（+`GlossaryJson`，默认 `"[]"`）
- Test: `src/TranslationApp.Tests/GlossaryReplacerTests.cs`

**Interfaces:**
- Produces: `GlossaryItem(string Source, string Target, bool Enabled=true)`；`static (string Text, int Hits, List<(string From,string To)> Applied) GlossaryReplacer.Apply(string translated, IReadOnlyList<GlossaryItem> items)`；`static List<GlossaryItem> GlossaryReplacer.Parse(string json)`（损坏 JSON→空表+`out bool corrupted`）。
- 规则按 spec §1.2（长词优先/ASCII 词界/CJK 子串/占位符防二次匹配/永不抛异常/上限 500）。

- [ ] Step 1: 写失败测试（≥10 项：基本替换、大小写不敏感、词界防 "AI"→"人工智能" 误伤 "OpenAI"、长词优先、二次匹配防护、CJK 子串、空表直通、禁用词条跳过、损坏 JSON、500 截断、幂等）
- [ ] Step 2: `dotnet test --filter GlossaryReplacerTests` 确认全 FAIL
- [ ] Step 3: 实现最小通过
- [ ] Step 4: 全量 `dotnet test` 绿；Commit `feat(core): 术语表后置替换纯函数`

### Task 2: TextCleaner（Core，TDD）

**Files:**
- Create: `src/TranslationApp.Core/Translation/TextCleaner.cs`
- Modify: `AppSettings.cs`（+`CleanClipboardText=true`）
- Test: `src/TranslationApp.Tests/TextCleanerTests.cs`

**Interfaces:** Produces `static string CleanForReading(string text)`，规则按 spec §3.1（六条，短文本直通，幂等）。

- [ ] Step 1: 失败测试（≥9 项：英文硬换行合并、连字符断词、句末标点不合并、列表符豁免、段落分隔保留、页眉重复行删除、短文本直通、幂等、null/空安全）
- [ ] Step 2-4: 同上循环；Commit `feat(core): 阅读清洗（断行还原）纯函数`

### Task 3: EngineStatsRepository（Core，TDD）

**Files:**
- Create: `src/TranslationApp.Core/History/EngineStatsRepository.cs`
- Modify: `src/TranslationApp.Core/History/HistoryDatabase.cs`（建表 SQL + SchemaVersion 迁移位）
- Test: `src/TranslationApp.Tests/EngineStatsRepositoryTests.cs`

**Interfaces:** Produces `enum EngineOutcome { Success, FailNetwork, FailEngine, FailKey, FailQuota, FallbackUsed }`；`void Record(string engineId, EngineOutcome outcome, string? errorSummary=null)`；`EngineStatsSummary GetSummary(string engineId, int days=7)`（含 `LastSummary`）；`void PruneOlderThan(int days=30)`。表结构照 spec §4.1。

- [ ] Step 1: 失败测试（UPSERT 累加/分列计数/7 天窗口/30 天清理/last_error 覆盖不追加/并发单测可省）
- [ ] Step 2-4: 循环；Commit `feat(core): 引擎统计仓储与建表迁移`

### Task 4: TranslationResult 扩展 + GlossaryTranslator 装饰器（Core，TDD）

**Files:**
- Create: `src/TranslationApp.Core/Translation/GlossaryTranslator.cs`
- Modify: `ITranslator.cs`（`TranslationResult` +`int GlossaryHits=0` +`IReadOnlyList<(string,string)>? GlossaryApplied=null`——record 默认值使既有构造点不破）、各测试构造点回归修正
- Modify: `src/TranslationApp.Core/Translation/TranslatorCatalog.cs`（出口包装饰器）
- Test: `src/TranslationApp.Tests/GlossaryTranslatorTests.cs`

**Interfaces:** Consumes Task1/3；Produces `sealed class GlossaryTranslator(ITranslator inner, Func<IReadOnlyList<GlossaryItem>> glossary, EngineStatsRepository? stats=null, Func<bool> privacyMode)`：TranslateAsync 成功后应用替换并回填 Hits，异常按 ErrorType 记统计（装饰器内 catch-then-rethrow）；`stats=null` 或 privacy 开 → 不记录。

- [ ] Step 1: 失败测试（成功路径替换+计数、异常透传类型不变、隐私关不记统计、测试连接标记位——装饰器提供 `SkipStats` 环境开关或 EngineCardViewModel 直连内层实例，择简实现）
- [ ] Step 2-4: 循环；Commit `feat(core): 引擎装饰器统一接入术语替换与统计`

### Task 5: App 层接线（门控 + 状态行 + 取词清洗）

**Files:**
- Modify: `src/TranslationApp.App/ViewModels/QuickTranslateViewModel.cs`（结果徽标「术语 ×N」tooltip 明细；划词路径 `TextCleaner` + 状态行「已清洗换行」；历史 Add 处隐私门控）
- Modify: `src/TranslationApp.App/Services/ScreenCaptureTranslateFlow.cs`、`Windows/PinWindow.xaml.cs`（命中徽标透传；历史写入门控）
- Modify: `src/TranslationApp.App/App.xaml.cs`（托盘菜单「隐私模式」勾选+即时生效+气泡；ClipboardMonitor 启停门控；--verbose 与隐私互斥提示）
- Modify: DI 注册处（TranslatorCatalog 装配装饰器时注入 settings.GlossaryJson 解析缓存 + stats repo）
- Test: 既有 VM 测试回归 + 新增隐私门控断言（可在 Core 侧测的部分不放 App）

- [ ] Step 1: 全量 `dotnet build` 找编译暴露点逐个接线
- [ ] Step 2: 术语解析缓存（settings 变更时失效，避免每次翻译 parse JSON）
- [ ] Step 3: `dotnet test` 全绿；手动冒烟 `dotnet run` 一次翻译
- [ ] Step 4: Commit `feat(app): 术语/清洗/隐私门控接入翻译链路`

### Task 6: 设置页 UI（术语表新页 + 三处开关 + 看板行）

**Files:**
- Modify: `src/TranslationApp.App/Windows/MainWindow.xaml`（导航 +「术语表」页：词条列表 ItemsControl + 添加行 + banner + 空态；通用区隐私模式卡；翻译区阅读清洗卡；引擎卡展开区统计行）
- Modify: `src/TranslationApp.App/ViewModels/SettingsViewModel.cs`（+Glossary 集合/命令/保存 JSON、PrivacyMode、CleanClipboardText、StatsSummary 加载）
- Modify: `build/verify-ui.ps1` 的 `$navClickY` 数组（导航多一项，spec v2.0 §8 的既有约束）

- [ ] Step 1: VM + XAML 实现（复用现有 SettingsCard/Nav 样式，无新令牌）
- [ ] Step 2: `dotnet test` 绿 + `build/verify-ui.ps1` 截图人工核对（术语表页浅/深两主题）
- [ ] Step 3: Commit `feat(app): 术语表页、隐私/清洗开关与引擎看板`

### Task 7: 文档与验收收口

- [ ] spec 状态改「已实现」；README「当前状态」追加 FR-031~034 段落（术语表/隐私模式/阅读清洗/引擎看板各 2-3 行，链接 spec）
- [ ] 全量 `dotnet test` + `publish.ps1` 体积门禁确认
- [ ] Commit `docs: P0 第一批落地记录`

## 验收清单

- [ ] 术语命中：译文尾部「术语 ×N」，禁用词条不生效，损坏 JSON 黄条提示
- [ ] 隐私模式：历史零写入、监听停、统计停、日志停；生词本收藏仍可用
- [ ] 阅读清洗：PDF 断行样例合并正确，手动输入不被清洗，可关
- [ ] 看板：翻译后计数增长、降级计 fallback_used、测试连接不计数、30 天后旧行消失
- [ ] 基线：975+ 项测试全绿，无新依赖，联网用例仍默认跳过
