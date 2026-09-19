# P0 第一批功能设计：术语表 · 隐私模式 · 阅读清洗 · 引擎看板

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-19 |
| 状态 | 已实现（提交链 `5bdef4b`…`225101c`，1018 项测试全绿） |
| 来源 | `docs/功能升级建议-竞品调研-v1.md` P0 清单（#1 #2 #4 #6）；#3 Anki 直推、#5 悬停取词为第二批 |
| 已确认决策 | 术语表=统一后置替换；隐私模式=本地留痕全关（网络请求不禁）；本批只做 4 项 |

## 0. 总原则

- 四项功能全部**纯本地**，不新增任何网络行为；不破坏"快到无感"：术语替换与文本清洗为同步纯函数，目标 <1ms/千字。
- 全部走现有模式：设置字段进 `AppSettings`（JSON 向后兼容）、逻辑进 Core 纯函数/类（可单测）、UI 进设置页现有分区。
- 现有 975 项测试全绿为回归门禁；新功能按 TDD 先写 Core 单测。

## 1. 术语表（统一后置替换）

### 1.1 数据模型

```csharp
// Core/Translation/GlossaryItem.cs
public sealed record GlossaryItem(string Source, string Target, bool Enabled = true);
```
`AppSettings.GlossaryJson`（string，序列化的 `List<GlossaryItem>`，默认 `"[]"`）。
选 JSON 字符串而非对象数组：沿用 AppSettings 现有扁平字段风格，JsonSettingsStore 零改动。

### 1.2 替换算法（Core 纯函数 `GlossaryReplacer`）

- 输入：译文 + 启用词条；输出：替换后文本 + 命中数。
- **仅作用于译文**（后置替换），不碰原文。
- 规则：
  1. 按 `Source` 长度降序处理（长词优先，防"AI"抢先命中"AI Agent"）；
  2. 字面匹配：ASCII 词边界感知（`Word` 前后不得再接字母数字，用 `Regex.Escape` + `\b` 半角词界；CJK 无词界直接子串匹配）；大小写不敏感匹配、保留 Target 原样写入；
  3. 已替换片段不再二次匹配（占位符法：命中即替换为 `\u0000{idx}\u0000`，全部完成后回填）；
  4. 词条上限 500 条，超出设置页拒绝保存并提示；空 Source/Target 词条保存时丢弃。
- 失败语义：替换永远不抛异常（内部 catch 返回原文 + 命中 0），术语表错误绝不能弄坏翻译结果。

### 1.3 接入点

新增装饰器 `GlossaryTranslator : ITranslator`（Core），包裹每个引擎实例：
- DI 注册处（TranslatorCatalog）统一包一层，**所有调用点（小窗/截图钉图/对比/测试连接）零改动即生效**；
- 对比模式每栏独立替换（符合 FR-020"每栏显示该引擎真实结果"——术语替换属于该引擎结果的后处理，可接受，需在结果栏显示命中徽标）；
- `TranslationResult` 增加 `int GlossaryHits` 字段（record 加字段，编译期暴露所有构造点，逐个补默认 0）；
- 小窗/钉图/对比栏：命中数 >0 时译文行尾显示灰色小字「术语 ×N」（tooltip 列出替换明细 `源→目标`）。

### 1.4 设置 UI

设置导航新增第 8 项「**术语表**」（生词本之后、高级之前）：
- 列表行：源词 | → | 目标词 | 启用开关 | 删除；底部「添加词条」；
- 顶部说明条（banner-info）：「术语表在译文生成后统一替换，对全部引擎生效；最多 500 条」；
- 空状态：「还没有术语 / 把产品名、专有名词的固定译法加进来」。

## 2. 隐私模式（本地留痕全关）

### 2.1 开关与状态

- `AppSettings.PrivacyMode`（bool，默认 false）；「通用 → 启动与常驻」下新增卡片：标题「隐私模式」，说明「不写翻译历史、暂停剪贴板监听、日志不落盘；翻译请求本身仍会发送（否则无法翻译）」——**把边界写死在文案里**，避免误解为离线模式。
- 托盘右键菜单新增可勾选项「隐私模式」，勾选即时生效并气泡确认一次「隐私模式已开启：不再写入历史」。
- 开启时小窗状态栏固定前缀「🔒 隐私模式」（用现有文字状态行，不加图标资源）。

### 2.2 作用面（全部现有代码路径加门控，不新建服务）

| 留痕 | 门控点 | 行为 |
|---|---|---|
| 翻译历史 | QuickTranslateViewModel / ScreenCaptureTranslateFlow 调 `IHistoryRepository.Add` 处 | 跳过写入（生词本为**用户显式动作**，不受隐私模式影响——收藏是主动意图，保留） |
| 剪贴板监听 | App 启动与设置变更处 `ClipboardMonitor.Start` | 不启动/立即停止；设置页开关置灰并标注「隐私模式开启中」 |
| 引擎看板统计 | §4 的 `EngineStats` 记录入口 | 不记录（统计也是留痕） |
| 详细日志 | `--verbose` 文件日志写入处 | 关闭文件日志；启动时若两者同时开启，气泡提示「隐私模式下不写日志」 |
| 设置/密钥落盘 | 不动 | 属功能配置，非行为留痕 |

## 3. 阅读清洗（论文断行还原）

### 3.1 算法（Core 纯函数 `TextCleaner.CleanForReading`）

处理**原文**（翻译前），仅对多行文本生效：
1. 行尾是字母/逗号/连字符（非句末标点 `。！？.!?;:;` 且非 `：`）且下一行首字符为**小写字母或非 CJK 起始** → 判为硬换行，合并（连字符结尾去连字符直连，其余补空格）；
2. 连续空行 → 保留为段落分隔（`\n\n`）；
3. 行首列表符号（`- • · * 1. (1) ①`）行不参与合并；
4. 页眉页脚噪声：与首行完全相同且出现 ≥3 次的短行（≤40 字符），删除；
5. 幂等：清洗结果再清洗不变（单测断言）；
6. 短文本（<80 字符或 ≤1 行）原样返回——划词单词场景零开销。

### 3.2 接入点与开关

- 划词取词（QuickTranslateViewModel 取词成功后）与剪贴板监听自动翻译路径应用；手动输入小窗**不清洗**（用户输入即意图）。
- `AppSettings.CleanClipboardText`（bool，默认 **true**）；「翻译」页新增卡片，说明「合并复制文本里的硬换行，修复 PDF/论文断行；关闭则原样翻译」。
- 清洗改变了文本（前后不等）时，小窗状态行追加「已清洗换行」（让用户知道原文被动过）。

## 4. 引擎看板（近 7 天成败统计）

### 4.1 存储

`HistoryDatabase` 新表（同库，`SchemaVersion` 迁移沿用现有建表模式）：

```sql
CREATE TABLE IF NOT EXISTS engine_stats (
  engine_id TEXT NOT NULL,
  day TEXT NOT NULL,          -- yyyy-MM-dd，本地日期
  success INTEGER NOT NULL DEFAULT 0,
  fail_network INTEGER NOT NULL DEFAULT 0,
  fail_engine INTEGER NOT NULL DEFAULT 0,
  fail_key INTEGER NOT NULL DEFAULT 0,
  fail_quota INTEGER NOT NULL DEFAULT 0,
  fallback_used INTEGER NOT NULL DEFAULT 0,
  last_error TEXT NOT NULL DEFAULT '',   -- 最近一次失败摘要（不含用户文本内容）
  PRIMARY KEY (engine_id, day)
);
```
- Core 新类 `EngineStatsRepository`：`Record(engineId, outcome, errorSummary?)`（UPSERT 累加）、`GetLast7Days(engineId)`、清理 >30 天旧行。
- `last_error` 只存错误摘要（HTTP 状态/异常类型），**绝不存用户文本**——隐私模式下整个记录入口被 §2 门控关闭。

### 4.2 记录点

`GlossaryTranslator` 装饰器顺带记录（同一 chokepoint）：成功 +1；抛 `TranslationException` 按 `ErrorType` 计入对应 fail 列；FR-028 降级成功时主引擎计 fail、并给主引擎 `fallback_used +1`。「测试连接」按钮结果不计数（非真实翻译）。

### 4.3 UI

「引擎」页每张引擎卡（含 Bing/Google）展开区顶部加统计行：
`近7天：成功 42 · 失败 3（网络 2 / 配额 1）· 已自动降级 2`，失败 >0 时下方小字「最近失败：HTTP 429（09-18）」。零数据时显示「近 7 天未使用」。纯文本 + 语义色，不做图表。

## 5. 边界与错误处理

- 术语表 JSON 损坏（手改配置）：读取失败按空表处理，设置页黄条提示「术语表无法读取，已按空表运行」，保存时覆盖修复。
- 隐私模式与对比历史写入（`CompareIncludeInHistory`）：隐私优先，一律不写。
- 钉图 `Ctrl+R` 重试路径同样经过装饰器，术语/统计行为一致。
- 清洗后超出 3000 字符上限的截断逻辑不变（清洗只会变短）。
- 术语替换与降级链：降级后由备用引擎结果重新替换（装饰器在引擎外层，天然满足）。

## 6. 测试与验收

- Core 单测（新增约 30-40 项）：GlossaryReplacer（词界/长词优先/二次匹配/损坏 JSON/500 上限）、TextCleaner（六规则+幂等+短文本直通）、EngineStatsRepository（UPSERT 累加/7 天窗口/30 天清理/隐私门控由上层测）、TranslationResult 新字段回归。
- 手动验收：`build/verify-ui.ps1` 截图核对新页面；实机走一遍 PDF 论文划词（清洗）、术语命中徽标、隐私模式开关全链路、看板计数增长。
- 全量 `dotnet test` 绿 + 体积门禁不破（无新依赖）。

## 7. 明确不做（本批）

- 术语表不做大小写/词形还原（leaps/leap 归一化）、不做反向（译文→原文）替换。
- 隐私模式不做"仅本地引擎"硬约束、不改托盘图标资源（第二批若做悬停取词再一起看图标）。
- 看板不做图表、不做导出、不跨重启保留 >30 天。
- Anki 直推、悬停取词 → 第二批，待本批落地后另立 spec。
