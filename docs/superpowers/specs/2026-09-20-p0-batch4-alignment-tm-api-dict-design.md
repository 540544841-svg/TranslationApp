# P0 第四批功能设计：段落对齐 · 复制策略 · TM 回填 · 本地 API · 批量 OCR · 钉图视觉 · mdx 词典

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-20 |
| 状态 | 实现中 |
| 来源 | `docs/改进建议-v2-功能与使用方式.md` 批 4（B2-1 / B2-3，A1 已剔除）+ `docs/功能升级建议-竞品调研-v1.md` P1（#7 TM / #9 批量 OCR / #10 HTTP API / #11 mdx / #12 原位覆盖视觉）+ A2-2（TM 回填可视化，并入 #7） |
| 编号 | FR-043 段落对齐 · FR-044 复制策略 · FR-045 TM 相似句回填 · FR-046 本地 HTTP API · FR-047 批量 OCR · FR-048 钉图原位覆盖视觉增强 · FR-049 mdx 词典（最小可行） |

## 0. 红线与总约束

- 不给译文落定链路加 >50ms：TM 查询是本地 SQLite（候选 ≤200 条，预筛后线性比对，实测预算 <5ms）；其余均为译后/旁路功能。
- 剪贴板相关（复制策略、批量导出）沿用「备份-还原-自写抑制」；不新增常驻监听。
- HTTP API 只绑 127.0.0.1 + token 鉴权（DPAPI 存储），**默认关**，隐私模式开启时不监听；不新增外网行为。
- 不新增 NuGet：批量 OCR 仅图片（PDF 文本抽取需第三方库，明确不做并在 UI 说明）；mdx 仅支持 SQLite 后端的 v3 格式（v2 二进制索引格式解析成本过高，导入时明确报「暂不支持该版本」）。
- 全部纯逻辑下沉 Core 可单测；设置字段进 `AppSettings`（JSON 向后兼容）。

## 1. FR-043 段落对齐视图（B2-1）

- Core `ParagraphAligner`（纯函数）：
  - `Split(text)`：按空行切段（无空行时按单换行），返回段数组；
  - `Align(source, translated)`：两侧段数相等且 ≥3 → 返回逐段配对；否则返回 null（无法对齐）。
- 接入：小窗翻译成功后，若原文 ≥3 段且 `Align` 成功 → 结果区出现「对照」切换按钮（整块译文 ⇄ 逐段对照）；逐段对照 = 每对「原文段（灰）/译文段」堆叠滚动。钉图路径不动（沿用 BlockOverlay）。
- 偏好：`AlignViewPreferred`（bool，默认 false=默认整块，本次会话内切换不持久化）。
- 手动输入与划词路径一致（对齐只看段数，不区分来源）。

## 2. FR-044 复制策略（B2-3）

- 交互收窄（WPF 无长按惯例）：小窗「复制译文」按钮保持左击=只复制译文；其右侧新增「▾」小按钮弹菜单三项——**只复制译文 / 只复制原文 / 原文+译文**。
- 原文+译文格式：`原文\n\n译文`（listing 场景粘贴后可自行删改）；复制后状态行「已复制：译文+原文」。
- 全部走现有 `CopyResult` 的剪贴板写入路径（含自写抑制），无新监听。Core 不需要新逻辑（拼接一行代码放 VM），但提供 `ClipboardFormats.Build(source, translated, mode)` 纯函数以便单测与钉图工具条复用。

## 3. FR-045 TM 相似句回填（v1 #7 + A2-2 可视化）

- Core `TmMatcher`（纯函数）：输入 `当前原文 + 候选 (原文,译文,createdAt) 列表` → 命中结果（最佳候选 + 相似度）或 null。
  - 相似度 = 归一化 Levenshtein（1 - dist/maxLen），先按长度差 >15% 预筛；
  - 阈值 `MinSimilarity = 0.92`；原文完全相等 = 1.0 直接命中；
  - 候选 ≤200 条，纯 CPU <5ms。
- 存储：`HistoryRepository.SearchSimilarCandidates(source, targetLanguage, limit=200)`——SQL 先取同语言对最近 500 条（不取全部），C# 端比对。
- 接入：`QuickTranslateViewModel` 翻译链最前（隐私模式无历史自然不命中）：命中 → 直接回填 ResultText、状态行「TM 命中 98% · 来自 3 天前的记录」+「重新机器翻译」按钮（本次强制跳过 TM，会话级）。**不新增落定延迟**（本地查询替代网络请求，只会更快）。
- 设置：`TmReuseEnabled`（默认 true）；「翻译」页卡片说明「相同/近似句子优先复用历史译文（相似度 ≥92%），点『重新机器翻译』可强制重译；隐私模式下无历史可复用」。

## 4. FR-046 本地 HTTP API（v1 #10）

- Core `LocalApiServer`：`HttpListener` 绑 `http://127.0.0.1:{port}/`（默认 46610，可配 1024~65535）；**默认关**。
  - `POST /api/translate`：body `{"text","source"?,"target"?}`（source 缺省 auto、target 缺省当前设置）→ `{"translated","engine","glossaryHits"}`；
  - `GET /api/status` → `{"app":"速译","version":1,"engines":[已配置 id 列表]}`（不含任何用户数据）；
  - 鉴权：请求头 `X-Auth: <token>`；token 首次启用时 `RandomNumberGenerator` 生成 32 hex，**DPAPI 加密**存 `LocalApiTokenEncrypted`；不匹配回 401；
  - 翻译调用走 `TranslatorCatalog`（与手动翻译同一引擎与术语链），并发上限 `SemaphoreSlim(2)`，超时 15s；
  - 仅 127.0.0.1：防火墙面为零回环；CORS 头 `Access-Control-Allow-Origin: *` 仅限该本机端口的预检放行（浏览器扩展场景需要；token 仍是唯一防线，文档写明）。
- 隐私模式：开启时不监听（与钩子同纪律）；`ApplyPrivacySideEffects` 扩展。
- 设置页「高级」：启用开关、端口、token 显示（掩码+「复制」+「重新生成」）、说明「供脚本/浏览器扩展调用；仅本机可访问」。

## 5. FR-047 批量 OCR（v1 #9，仅图片）

- Core `BatchOcrRunner`：输入文件列表 + `Func<string /*imagePath*/, Task<string?> /*文本*/> recognizer`（注入现有 OcrService 包装）→ 逐张识别，产出 `(path, ok, text)` 列表；单张失败不中断；进度回调。
- App：「高级」页「批量识别」按钮 → 多选图片（png/jpg/bmp/jpeg/webp/tiff）→ 后台逐张（复用 paddle/windows 引擎路由）→ 完成后 SaveFile 导出 **md**（`## 文件名` + 文本）或 txt；结果行报「成功 n / 失败 m」。
- 不做 PDF（无依赖约束，UI 文案写明「PDF 请先转图片」）；不做文件夹监视。

## 6. FR-048 钉图原位覆盖视觉增强（v1 #12）

- Core `TextRenderStyleSampler`（纯函数，输入 `BgraImage` + 文字块矩形）：
  - 背景色 = 块内出现频率最高的颜色簇（量化 4bit/通道）；
  - 文字色 = 与背景色差（感知近似）最大的簇；
  - 字号 ≈ 块高 × 0.72（与现渲染基线对齐，常量可调）；
  - 全等色像素占比 <30%（复杂背景）→ 返回 null = 维持现有半透明底样式（宁可不换，不画花）。
- 接入：钉图原位替换（`OcrInPlaceReplace`）绘制译文时用采样样式；采样在识别线程完成，不加交互延迟。
- 表格线保留（Paddle 结构识别）不在本批：仅做颜色/字号匹配（如实收窄 v1 #12）。

## 7. FR-049 mdx 离线词典（最小可行）

- 支持面：仅 **MDX v3 且后端为 SQLite** 的词典文件（`.mdx` 魔数 `MDX\x00\x03` + zlib 头解析后内嵌 SQLite 流）；其余版本/格式导入时提示「暂不支持该格式（当前支持 MDX v3-SQLite）」。
- Core `MdxDictionaryReader`：打开只读连接（`Microsoft.Data.Sqlite`，File Is Shared）→ 词表 `Term` 表按词头精确 + 前缀查询 → 取 `Definition` 文本；RTF 定义用内置极简 `RtfStripper`（跳过控制字/组，保留文本）转纯文本；无样本文件时单测用自造 SQLite 模拟库（不依赖真实 mdx）。
- 设置页「词典」区（高级页内）：导入（复制到 `%AppData%\TranslationApp\dicts\`）、列表（名称/词条数/删除）、测试查询框。
- 接入：划词结果为**单词**（≤32 字符且无换行）且词典已启用时，小窗译文上方显示「词典」卡（词头 + 释义前 300 字，点击展开全文）；多词典取首个命中。生词本收藏时把词典义并入背面字段（Anki 卡更完整）。
- 工作量兜底：若真实 mdx 解析联调受阻，交付止于「导入诊断 + 自造格式支持 + UI 全链路」，UI 明示支持范围（诚实降级，不做假解析）。

## 8. 设置字段汇总

| 字段 | 默认 | FR |
|---|---|---|
| `TmReuseEnabled` | true | 045 |
| `LocalApiEnabled` | false | 046 |
| `LocalApiPort` | 46610 | 046 |
| `LocalApiTokenEncrypted` | "" | 046 |
| `DictionariesEnabled` | true | 049 |

## 9. 测试计划

- `ParagraphAlignerTests`：段数匹配/不匹配、≥3 段门槛、空行与单换行切分、幂等。
- `ClipboardFormatsTests`：三模式拼接。
- `TmMatcherTests`：完全相等、92% 边界、长度差预筛、空候选、多候选取最佳。
- `LocalApiServerTests`：token 校验（401/200）、请求体解析、并发闸、非 127.0.0.1 绑定拒绝（用真实 listener 绑随机端口测）。
- `BatchOcrRunnerTests`：注入桩识别器——成功/失败混合、进度回调次数、异常不中断。
- `TextRenderStyleSamplerTests`：纯色背景+深色字命中、复杂背景返回 null、字号估算。
- `MdxDictionaryReaderTests`：自造 SQLite 模拟库的查询/前缀/RTF 剥离；非支持魔数报明确错误。
- 回归门禁：现有 1083 项全绿。

## 10. 边界与不做

- 段落对齐不做逐句（段 = 空行分隔）；对齐失败静默回整块。
- TM 回填不做模糊引擎（Levenshtein 够用）；不把 TM 结果写回统计。
- HTTP API 不做 OCR 端点（先 translate/status 两个）、不做 TLS/远程访问。
- 批量 OCR 不做 PDF、不做目录监视、不做 OCR 结果入库历史（导出文件即走）。
- mdx 不做 v2 二进制格式、不做图片词条渲染（RTF→纯文本）。
