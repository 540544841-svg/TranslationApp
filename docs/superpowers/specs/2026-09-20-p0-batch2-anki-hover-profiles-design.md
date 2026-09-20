# P0 第二批功能设计：Anki 直推 · 悬停取词 · 场景档案

| 项目 | TranslationApp（速译） |
|---|---|
| 日期 | 2026-09-20 |
| 状态 | 已实现（提交链 `7558c0b`…本批末次提交，1061 项测试全绿；截图 `artifacts/ui/batch2-*.png` + 运行时冒烟 `build/verify-batch2-runtime.ps1` PASS） |
| 来源 | `docs/功能升级建议-竞品调研-v1.md` #3/#5 + `docs/改进建议-v2-功能与使用方式.md` B1-3/B3-1；用户指令「除了电商包不做其余全做」，本批为排期「批 2」 |
| 已确认决策 | A1 电商包全部剔除；批 2 = Anki 直推 + 悬停取词 + 场景档案（先做 阅读/隐私 两档）；写作档依赖批 4 的 B2-2「更正式」，届时补 |
| 编号 | FR-035 Anki 直推 · FR-036 悬停取词 · FR-037 场景档案 |

## 0. 总原则与红线（B5）

- 三项功能都不给**译文落定链路**增加可感知延迟：Anki 推送在收藏动作之后异步执行；悬停图标只出现一个窗口，取词仍走现有 Ctrl+C 链路；档案切换是纯内存字段复制 + 一次落盘。
- 涉及全局钩子的只有 FR-036：默认关闭、设置开关一键全关、**隐私模式开启时不装钩子**。
- 不新增 NuGet 依赖、不新增外网行为（AnkiConnect 仅访问 127.0.0.1 固定端口 29537）。
- 剪贴板纪律沿用现有「备份-还原-自写抑制」（`ClipboardCapturer` + `ClipboardMonitor.Suppress`），不新增常驻监听。
- 留痕口径与批 1 一致：Anki 直推是**用户显式收藏动作的延伸**（同生词本收藏），不受隐私模式门控；日志绝不写用户文本。
- 回归门禁：现有 1018 项测试全绿；新功能按 TDD 先写 Core 单测（可测部分全部下沉 Core）。

## 1. FR-035 Anki 直推

### 1.1 Core：`AnkiConnectClient`（新目录 `Core/Anki/`）

- `AnkiRequestBuilder`（纯静态）：构造 JSON-RPC 请求体——`{"action":...,"version":6,"params":{...}}`；
  动作三个：`version`（连通性探测）、`addNote`（单条）、`addNotes`（批量，逐条返回）。
  笔记载荷：`{"deckName":…,"modelName":…,"fields":{正面字段:原文, 背面字段:译文},"tags":["速译","源语言-目标语言"]}`。
- `AnkiConnectClient`：构造注入 `HttpClient`（单测用 handler 桩，零网络）。固定基址 `http://127.0.0.1:29537`；超时 4s；
  响应解析：`{"result":…,"error":…}`——`error` 非 null 视为失败；`addNotes` 的 `result` 为逐条 guid/null，**null 计为「跳过（重复或无效）」**，只统计数量。
  失败摘要只保留异常类型/HTTP 状态，不保留响应正文（Anki 报错会回显字段内容 = 用户文本）。
- 生产 `HttpClient` 必须 `UseProxy=false`：不经 `HttpClientProvider`（那是引擎代理作用域），本机端口被系统代理（Clash）转发过一次就会假失败。

### 1.2 设置字段（AppSettings，全部向后兼容）

| 字段 | 默认 | 说明 |
|---|---|---|
| `AnkiEnabled` | false | 直推总开关（含「收藏即推」与批量按钮的可用性） |
| `AnkiDeck` | `"生词本"` | 目标牌组名（以 Anki 内实际名称为准） |
| `AnkiModel` | `"基本"` | 笔记模板名 |
| `AnkiFrontField` / `AnkiBackField` | `"正面"` / `"背面"` | 字段名；正面=原文、背面=译文 |
| `AnkiPushOnFavorite` | true | 收藏生词时顺带推送（仅 `AnkiEnabled` 时生效） |

### 1.3 接入点

- `QuickTranslateViewModel.ToggleFavorite` 的**新增**分支：先照旧写生词本（同步，行为不变），随后 fire-and-forget 推送；
  推送完成/失败只更新状态行文本（「已加入生词本 · 已推送 Anki」/「已加入生词本 · Anki 未推送（连接不上，Anki 需开着且有 AnkiConnect 插件）」）。推送失败不影响收藏结果，可设置页批量补推。
- 生词本页「**全部直推 Anki**」：对全表 `addNotes` 分批（每批 50），进行中按钮禁用 + 状态文本「推送中 n/N」，结束报「新增 x · 跳过 y（重复）· 失败 z」。重复项由 Anki 端拒重，本端不另存推送状态（不加表不加列）。

### 1.4 设置 UI（生词本 Tab 内新增卡片）

卡片「Anki 直推」：启用开关（ToggleSwitch）；牌组/模板/正面字段/背面字段四个文本框；「测试连接」按钮（`version` → 显示「AnkiConnect 已连接（版本 n）」或失败原因）；「全部直推」按钮与状态文本。
说明条：「需桌面版 Anki 开着且装有 AnkiConnect 插件（默认端口 29537）；只访问本机，不经代理、不上外网」。

## 2. FR-036 悬停取词（选中 → 光标旁浮图标 → 点击翻译）

### 2.1 Core：决策逻辑与钩子分离

- `HoverTriggerLogic`（纯类，可单测，注入时钟）：输入鼠标按下/抬起事件（位置、时刻）与前台窗口归属标志，输出 `ShowIcon` / `Ignore`。规则：
  1. 按下与抬起位移 ≥ 12 DIP（拖拽选择才触发，单击不算）；
  2. 按下→抬起 ≤ 3s（长按右键菜单等不算）；
  3. 抬起时前台窗口不是本进程（小窗/设置内划字不弹）；
  4. 冷却 800ms：一次展示后短时内再选不再弹（防跟手刷屏）；
  5. 纯函数无副作用，钩子层只搬运事件。
- `MouseButtonHook`（`Core/SystemIntegration/`）：`WH_MOUSE_LL` 低级钩子，仅观察不拦截（回调直接 `CallNextHookEx`，返回值不吞事件）；message-only 窗口模式与 `ClipboardMonitor` 同款；`Start()/Stop()` 幂等；`MouseUp` 事件在钩子线程转交决策层。
- 已知坑（v1 §4 表）：UIPI——以管理员权限窗口为目标时 SendInput 取词本会失败，与现有 Alt+S 同等表现，不另作处理；高分屏物理像素定位复用 `ScreenInterop`（FR-025 经验）。

### 2.2 App：浮标窗口 `HoverBadgeWindow`

- 28px 圆形图标窗：`Topmost` + `ToolWindow` + `ShowActivated=false` + 无边框透明，跟随抬起时的光标位置右下 16px（物理像素换算）；
- 隐藏时机：被点击 / 5s 超时 / 任意键鼠再操作 / 焦点窗口变化，四者取先；
- 点击 → 调 App 现有 `TranslateSelectionAsync()`（同 Alt+S 链路：模拟 Ctrl+C 取词 → 清洗 → 小窗）。**不新建第二条取词/翻译链路**；
- 已有一个浮标时移动位置复用同一窗口实例（不叠窗）。

### 2.3 开关与门控

- `AppSettings.HoverSelectEnabled`（默认 **false**）：「通用」页新增卡片「悬停取词」——开关 + 说明「选中文字后光标旁浮出图标，点击才翻译；需要常驻鼠标钩子，可在游戏/远程桌面场景一键关闭」。
- 隐私模式联动：`ApplyPrivacySideEffects` 增加一条——隐私开 → `MouseButtonHook.Stop()`；隐私关且 `HoverSelectEnabled` → Start。在设置页开启悬停但当前处于隐私模式时，开关可开但钩子不装，卡片标注「隐私模式开启中，钩子未启用」（与 FR-017 剪贴板开关同一措辞模式）。
- 生效路径：App 启动、设置开关变更、隐私开关变更三处统一走 `ApplyHoverSideEffects(services, …)`（与批 1 `ApplyPrivacySideEffects` 同构）。

## 3. FR-037 场景档案（先做 阅读/隐私 两档）

### 3.1 数据模型（Core 新文件 `Settings/ProfileModels.cs`）

档案 = 对固定键集的一份**稀疏覆盖**（只记录该档案指定的键，未指定的键切换时不动）：

```csharp
public sealed class ProfileOverrides
{
    public string? Engine { get; set; }
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public bool? CleanClipboardText { get; set; }
    public bool? PrivacyMode { get; set; }
    public bool? GlossaryEnabled { get; set; }
    public bool? ClipboardMonitorEnabled { get; set; }
    public bool? HoverSelectEnabled { get; set; }
}
public sealed class AppProfile(string Name, ProfileOverrides Overrides);
```

`AppSettings` 新增：

| 字段 | 默认 | 说明 |
|---|---|---|
| `GlossaryEnabled` | true | 术语表全局开关（false = 词条保留但不参与替换） |
| `ActiveProfile` | `""` | 当前档案名；空 = 标准档（不设任何覆盖） |
| `CustomProfilesJson` | `"[]"` | 用户自定义档案列表（`List<AppProfile>` 序列化），上限 10 个 |

### 3.2 内置档案（代码常量，不落盘、不可改删）

| 档案 | 覆盖内容 | 说明 |
|---|---|---|
| `阅读` | Engine=bing、源=auto、目标=zh-CN、清洗=开、术语=开、隐私=关 | 「复制即读」顺手链路；剪贴板监听**不强制开**（行为开关不代用户决定） |
| `隐私` | 隐私=开、剪贴板监听=关、悬停取词=关、术语=开 | 本地留痕全关 + 不装钩子；引擎不改（无离线引擎可指，诚实标注「翻译请求本身仍会发送」） |

自定义档案：把**当前**这 8 个键的现值快照存为一个档案（名称必填、去重、≤20 字符）。

### 3.3 切换语义（`ProfileService`，Core 纯逻辑 + App 注入落盘回调）

- `Apply(settings, profile)`：逐键复制覆盖值 → App 层 `ISettingsStore.Save` → 复用批 1 副作用（`ApplyPrivacySideEffects`，本批扩展它同时处理悬停钩子启停）；
- 循环热键：`AppSettings.HotkeySwitchProfile`（默认 `"Alt+P"`，走现有 `HotkeyManager.TryRegister("profile", …)` 与热键输入框），顺序 标准 → 阅读 → 隐私 → 自定义 1..n → 标准；每次切换气泡「已切换到档案「阅读」」；目标语言/引擎变化后，小窗下次打开自然读新值（不重开已显示的窗口）；
- 注册失败（Alt+P 被占）与现有三个热键同处理：气泡提示，功能其余部分不受影响；
- 当前生效设置若被用户手动改动（未切档案），状态行不假装还在档案内：`ActiveProfile` 保持显示但设置页档案卡标注「（当前设置已偏离）」。判定 = 档案的每个覆盖键现值是否仍等于档案值。

### 3.4 UI

- **托盘**：「设置」项上方新增子菜单「场景档案」——标准/阅读/隐私/自定义…，Radio 勾选当前项，点击即切换（`ContextMenu.Opened` 时同步勾选态，与 privacyItem 同款做法）。
- **设置页「通用」Tab 新增卡片「场景档案」**：当前档案名 + 偏离标注；内置两档切换按钮；「把当前设置存为档案」（名称输入 + 保存）；自定义档案列表（行 = 名称 | 应用 | 删除）。
- 「翻译」页术语表联动：术语表 Tab 顶部加一个全局启用开关（`GlossaryEnabled`），关闭时列表仍可编辑，行尾徽标「术语 ×N」自然不再出现。
- 导航仍为 8 页，不改 `build/verify-ui.ps1` 的 nav 数组。

## 4. 装饰器/门控小改

- `GlossaryTranslator` 的词条函数在 DI 处包一层：`() => settings.GlossaryEnabled ? cache.Items() : []`（空表 = 直通零开销），Core 装饰器本身零改动。
- `TranslationResult.GlossaryHits` 等签名不动。

## 5. 测试计划（xUnit，先行编写）

| 组 | 覆盖 |
|---|---|
| `AnkiRequestBuilderTests` | addNote/addNotes/version 请求体字段与嵌套结构；tags 生成；字段名含特殊字符 JSON 转义 |
| `AnkiConnectClientTests` | 桩 handler：error 非空 → 失败；addNotes 逐条 null → 跳过计数；HTTP 非 200 → 失败摘要不含响应正文 |
| `HoverTriggerLogicTests` | 位移/时长/冷却/前台归属四类边界各正反例；不依赖真实时钟（注入） |
| `ProfileServiceTests` | 稀疏覆盖只动指定键；内置两档键值正确；自定义快照/上限 10/名称去重；循环顺序含空 ActiveProfile；偏离判定 |
| `AppSettingsTests` 扩展 | 新字段默认值与 JSON 往返；旧配置文件（无新字段）读取不炸 |
| 装饰器联动 | GlossaryEnabled=false → 命中 0 且译文原样 |

## 6. 边界与不做

- Anki 直推不装插件检测向导（文案给足路径即可）；不做卡片媒体（图片/发音）——正面/背面纯文本。
- 悬停取词不做「选中即译」（那是 FR-017 剪贴板监听的职责）；不读 UI Automation 选区（跨应用兼容性差，维持 Ctrl+C 方案）。
- 场景档案不做每档案独立热键、不做配置导入导出；「写作」档等 B2-2 落地后追加为内置定义即可（零迁移）。
- 隐私模式下 Anki 直推可推送（显式动作延伸，同生词本），文档与卡片文案如实写明。
