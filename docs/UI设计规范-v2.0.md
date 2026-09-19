# 速译 UI 设计规范 v2.0「琥珀流光」

| 项目 | TranslationApp（速译） |
|---|---|
| 平台 | Windows 10 1809+ / Windows 11，WPF (.NET 10) |
| 日期 | 2026-09-17 |
| 设计来源 | Apple Human Interface Guidelines + Google Material Design 3 + 品牌资产方法论（独特资产系统） |
| 品牌资产清单 | `docs/brand-asset-sheet.html`（2026-09-14 用户确认：琥珀流光独占色 / 琥珀闪电图标 / 「译光闪落」动效签名） |
| 落地位置 | `src/TranslationApp.App/Themes/`（令牌与控件样式） |

---

## 1. 品牌核心（v2.0 新增）

**人群与场景**：25–40 岁效率型 Windows 用户（开发者 / 涉外职场 / 学生），高频极短会话——
每天几十次呼出、每次几秒即走。工具即用即走，唯一体验承诺是「快到无感」。

**品牌人格**：快（闪电般的即时）· 准（可靠的专业）· 轻（无打扰的克制）。

**唯一记忆点**：按一下热键，译文像一道琥珀光，落到鼠标旁边。

**品类色卡与独占色落位**：翻译品类主色带被蓝色垄断（Google `#4285F4` / Microsoft `#0078D4` /
百度 `#2932E1` / 腾讯 `#0052D9` / 有道 `#0C8BFC` / DeepL `#0F2B46`），Grammarly 占绿。
v1.0 的 `#2C6BED` 蓝放进该色带毫无区分度（独占性测试不合格）。v2.0 落位**完全空档的暖色系——琥珀**：
荧光笔划重点（= 划词的品类语义）+ 中文语境「黄＝快」（美团式速度联想）双重语义同构。

**七类资产**（详见 brand-asset-sheet.html）：

| # | 资产 | 落地 |
|---|---|---|
| 1 | 图形符号「琥珀闪电」 | `Assets/app.ico`（`build/make-icon.ps1` 生成）：琥珀渐变圆角方块 + 白色粗闪电，右倾姿态，笔画占比 ≈20%，16px 可读 |
| 2 | 独占色「琥珀流光」 | `Color.Primary` 族（见 §2） |
| 3 | 形状语言「一道斜切的光」 | 14° 斜切琥珀光带：不确定进度条、导航选中指示条、侧栏品牌光带、译光闪落 |
| 4 | 动效签名「译光闪落」 | 译文落定瞬间光痕扫过译文区（420ms 减速），失败/对比模式不播 |
| 5 | 启动仪式「首呼点亮」 | 托盘气泡「速译已就位」（WPF 气泡为系统原生，动画形态后续批次落地） |
| 6 | 语言人格「干脆的动词句」 | 微文案三铁律：动词开头说结果；永远给出路；不卖萌不甩错误码。「已复制译文」「Google 连不上，已用 Bing 重试」「输入文字，按 Enter 翻译」 |
| 7 | 声音触感 | 本轮不做（托盘工具静音是礼貌，光痕即签名） |

**触点分布**：峰值 = 译文落定（签名动效）；高频 = 进度条流光 / 选中态 / 收藏星 / 焦点环；
入口 = 托盘图标 / 启动气泡 / 截图选区框。

---

## 2. 色彩系统（v2.0 重制）

颜色**只允许**通过语义令牌引用（`Brush.*` / `Color.*`），禁止在页面里写死色值。
浅深两套色板的键名完全一致，运行时整本替换即可换肤。

### 2.1 主色三角色（重要变化）

琥珀一个色相拆成三个角色，解决「填充色当文字用对比不足」：

| 令牌 | 浅色 | 深色 | 用途 |
|---|---|---|---|
| `Brush.Primary` | `#D97A00` | `#FFB03A` | **交互填充**：主按钮、开关轨道、对勾底、焦点环、滑块、导航指示条。白底非文字对比 3.1:1（深色 9:1） |
| `Brush.PrimaryText` | `#A05E03` | `#FFC46B` | **文字/图标级琥珀**：选中导航字、链接、徽标字、点亮态图标、录制中提示。白底 4.9:1、容器底 4.6:1（AA 正文级） |
| `Brush.OnPrimary` | `#231303` | `#2B1A00` | **琥珀填充之上的文字**（白字不够 3:1，用暖近黑）6.6:1 |
| `Brush.PrimaryContainer` | `#FFF2DF` | `#3D2B0E` | 色调按钮底、导航选中底、开关关闭滑轨容器 |
| `Brush.PrimaryHover / Pressed` | `#C56D00` / `#B06000` | `#FFC05E` / `#F09A1E` | 主按钮交互 |
| `Brush.PrimaryContainerHover` | `#FFE7C2` | `#4C3612` | 色调按钮悬停 |

**规则**：凡是「琥珀当文字/图标用」一律 `PrimaryText`；「琥珀当底/描边用」一律 `Primary`；
填充上的文字一律 `OnPrimary`。禁止再用 `Primary` 做浅底上的前景色。

### 2.2 中性与语义色

中性灰阶沿用 v1.0 冷灰不动（让琥珀独占强调位）。语义色调整：

- **警告改橙红系**（`#C2410C` / 容器 `#FDEEE6` / 描边 `#F2CDBC`；深色 `#F79055` / `#3A2717` / `#5E3D26`）
  ——与品牌琥珀拉开色相，避免「满屏琥珀分不清警告」。
- 错误 `#C0342D`、成功 `#1B7F4F` 沿用 v1.0。

### 2.3 动效签名流光（新增，两主题同值）

`Brush.Signature.Streak`：水平渐变 `#00FFB03A → #FFB03A(25%) → #FFB03A(75%) → #00FFB03A`。
中段 50% 实心、两端渐隐——任意时刻截屏都有一段清晰可见的光。光效是品牌资产，**不随主题变化**。

### 2.4 截图遮罩与内容覆盖层（不变式保留）

与 v1.0 相同：`Color.Capture.*` 与 `Color.Overlay.*` 共 7 枚令牌在浅/深两套主题取值一致
（遮罩是深色场景、覆盖层属于图片内容，都不跟主题）。唯一变化：
`Color.Capture.Selection` `#4C8DFF` → **`#FFB03A`**（琥珀在 55% 黑上对比约 11:1，
亮内容上约 2.6:1，均优于旧蓝的 6:1 / 1.5:1），且选区框成为品牌独占色的入口触点。

### 2.5 对比度（WCAG）

| 组合 | 对比度 | 结论 |
|---|---|---|
| TextPrimary / Window（浅） | ≈ 15:1 | AAA |
| OnPrimary / Primary（浅） | ≈ 6.6:1 | AA ✓ |
| PrimaryText / Window（浅） | ≈ 4.9:1 | AA ✓ |
| PrimaryText / PrimaryContainer（浅） | ≈ 4.6:1 | AA ✓ |
| Primary / Window（浅，非文字） | ≈ 3.1:1 | ✓（UI 组件 3:1） |
| Primary / Window（深，非文字+文字） | ≈ 9:1 | AAA |
| Capture.Selection / 55% 黑 | ≈ 11:1 | ✓ |

---

## 3. 字体与字阶（沿用 v1.0）

- `Font.App` = `Segoe UI, Microsoft YaHei UI`；`Font.Icon` = `Segoe Fluent Icons, Segoe MDL2 Assets`。
- 字阶 Caption 11 / Small 12 / Body 13 / Content 14 / Title 15 / Headline 19；字重 Normal / Medium / Semibold。
- 新增用法：状态行降级提示保持中性灰（翻译已成功，警告色留给失败时刻——琥珀 10% 纪律）。

## 4. 间距、圆角与尺寸（沿用 v1.0）

4pt 体系 `Space.XS/S/M/L/XL` = 4/8/12/16/24；圆角 6/8/10/12/14；高度 26/30/34。
新增固定值：导航指示条 3×16（CornerRadius 2，SkewX -14°）；侧栏品牌光带 22×3（SkewX -14°）；
进度光带宽 96。

## 5. 层级与阴影（沿用 v1.0）

`Shadow.Window`（28/6/270/0.16）与 `Shadow.Popup`（18/4/270/0.14）不变；窗口 1px 描边不变。

---

## 6. 组件规范（v2.0 变更点）

全部控件模板保留 v1.0 的状态层体系（悬停 Hover / 按下 Pressed / 聚焦 Primary 描边不加粗）。
v2.0 变更：

| 组件 | 变更 |
|---|---|
| 不确定进度条 | 胶囊往复 → **斜切流光带**：`Brush.Signature.Streak` 96px 光带，SkewX -14°，1.4s `SineEase EaseInOut` 往复。动画挂在 **`EventTrigger Loaded`** 上——属性触发器（IsIndeterminate / Visibility）的 EnterActions 依赖「条件跳变」，模板应用时条件已成立不会启动（v2.0 实测踩坑）；Loaded 必然触发一次，控件隐藏时跳过渲染，时钟开销可忽略 |
| 导航项 NavItem | 选中态 = PrimaryContainer 底 + **PrimaryText** 字/图标 + 左侧 3×16 斜切琥珀指示条（形状语言落位） |
| 色调按钮 Button.Tonal | 文字 `Primary` → **`PrimaryText`**（AA） |
| 下拉展开态 | 文字 `Primary` → `PrimaryText`；选中项对勾 → `PrimaryText` |
| 链接按钮 Button.Link | 悬停文字 `Primary` → `PrimaryText` |
| 热键录制框 | 录制中提示文字 `Primary` → `PrimaryText` |
| 引擎徽标（已配置） | 文字 `Primary` → `PrimaryText` |
| 钉图切换按钮（激活态） | 图标 `Primary` → `PrimaryText` |
| 小窗点亮态图标（图钉/对比/收藏） | `Primary` → `PrimaryText` |
| 开关 / 复选框 / 主按钮 / 滑块 | 模板不变，令牌自动换琥珀（OnPrimary 自动适配深字） |
| 侧栏品牌区 | 「速译」标题 + 22×3 斜切光带 + 副标题「划词 · 截图 · 即译」 |
| 空状态 | 历史：「暂无翻译记录 / 翻译过的内容会自动收进这里（保留最近 5000 条）」；生词本：「还没有收藏的词条 / 翻译时在小窗点 ☆ 即可加入，可导出 CSV / Anki」；小窗输入框占位「输入文字，按 Enter 翻译」——用 `ItemsControl.HasItems` 触发器与输入框 Text 触发器实现，不依赖 VM |

**加载指示**：进度条语义不变（3px 线性、不引起布局跳动），视觉升级为品牌流光。

## 7. 动效规范（v2.0 变更点）

| 场景 | 时长 | 缓动 | 说明 |
|---|---|---|---|
| **译光闪落（签名）** | 420ms | `CubicEase EaseOut` | 光痕 X −80 → 译文区宽 +80；透明度 80ms 亮起、260ms 起熄灭 160ms。触发：`ResultText` 到达且非空、无错误、非对比模式；`SystemParameters.ClientAreaAnimation=false` 时跳过 |
| 进度条流光 | 1.4s 循环 | `SineEase EaseInOut` | X −120 → 420，`EventTrigger Loaded` 启动 |
| 其余（小窗呼出 160ms / 隐藏 100ms / 状态层 120ms / 开关 180ms / 主题切换即时） | | | 沿用 v1.0 |

原则不变：只在「状态变化」上用动效；除循环动画外 ≤ 200ms；签名动效是唯一例外（420ms，
只押在译文落定这个峰值时刻，重复建立条件反射）。

## 8. 界面布局（沿用 v1.0 结构，v2.0 增补）

- **翻译小窗**：结构不变（语言栏 → 输入区 → 译文区 → 状态区）；新增输入占位文案、
  译文落定光痕层（`SignatureStreak`，不参与命中与布局）、加载态流光进度条。
- **设置窗口**：侧栏新增品牌区（标题+光带+副标题）；导航项带斜切指示条；
  历史/生词本卡片新增空状态。⚠️ 侧栏头部增高 ≈24px——`build/verify-ui.ps1` 的
  `$navClickY` 已同步 +26px（@134 起），改动侧栏头部布局时必须同步该数组。

## 9. 实现方案（沿用 v1.0 架构）

资源字典组织、`StaticResource`/`DynamicResource` 取舍、DWM 深色标题栏、阴影失效补偿
均沿用 v1.0（见 git 历史或 v1.0 文档第 9 章）。新增令牌清单：
`Color.PrimaryText`、`Brush.PrimaryText`、`Brush.Signature.Streak`、`Duration.Signature`、`Icon.Warning`。

## 10. 验收清单

- [x] 浅色 + 深色两套主题下全部页面渲染正常（`build/verify-ui.ps1` 截图核对）
- [x] 无一处蓝紫残留（独占性：琥珀放进品类色带一眼可分）
- [x] 琥珀三角色使用正确：文字用 PrimaryText、填充用 Primary、填充上文字用 OnPrimary
- [x] 加载态流光可见且在移动（`quick-error-loading.png` / `loading2.png` 两帧位移对照）
- [x] 空状态：历史 / 生词本 / 小窗输入占位三处齐备
- [x] 选中导航 = 琥珀容器底 + PrimaryText + 斜切指示条
- [x] 应用图标 = 琥珀闪电（托盘 16px 可读）
- [x] 975 项单元测试全绿（交互与功能行为不变）
- [x] 全部界面颜色走语义令牌（含遮罩/覆盖层固定色不变式）

**待人工确认项**：

- [ ] 「译光闪落」实机手感（截图无法呈现动态光痕，建议 `publish\TranslationApp.exe --verbose` 实译几次观察）
- [ ] 启动仪式「首呼点亮」的气泡动画形态（本轮文案先落地，动画形态留待后续批次）
- [ ] 150% / 200% DPI 与多显示器下的琥珀观感
