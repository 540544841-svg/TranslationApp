namespace TranslationApp.Core.Settings;

/// <summary>
/// 应用设置（阶段 0 最小集，后续阶段按需扩充字段；
/// JSON 反序列化忽略未知字段，旧版本配置可向后兼容读取）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>配置结构版本，供后续升级迁移使用。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>源语言（auto = 自动检测）。</summary>
    public string SourceLanguage { get; set; } = "auto";

    /// <summary>目标语言，默认简体中文（内部语言码 = Google 语言码）。</summary>
    public string TargetLanguage { get; set; } = "zh-CN";

    /// <summary>
    /// 当前翻译引擎。默认 bing：非官方接口、零配置，且在国内网络可直连
    /// （google 非官方接口在部分网络环境不可达，仅作为可切换备选）。
    /// </summary>
    public string Engine { get; set; } = "bing";

    /// <summary>输入翻译热键，如 Alt+D。</summary>
    public string HotkeyInputTranslate { get; set; } = "Alt+D";

    /// <summary>划词翻译热键，如 Alt+S。</summary>
    public string HotkeySelectTranslate { get; set; } = "Alt+S";

    /// <summary>启动时显示托盘气泡提示。</summary>
    public bool ShowStartBalloon { get; set; } = true;

    /// <summary>界面主题：system（跟随系统，默认）/ light / dark。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 翻译小窗**默认宽度**（DIP，含阴影留白的窗口尺寸；范围 320~900）。
    /// <b>语义（阶段 5 批 3 起）</b>：每次呼出都以它为基准，按内容自适应只会在此基础上加宽
    /// （上限 640，默认值本身可越过该上限）；拖拽边缘不写回本字段，只有小窗右键「设为默认尺寸」
    /// 或在设置里手填才会改它（FR-026 / 14.2）。
    /// </summary>
    public double QuickWindowWidth { get; set; } = 420;

    /// <summary>
    /// 翻译小窗**默认高度**（DIP，范围 240~900）。每次呼出以它为基准，自适应只在此基础上按内容增高
    /// （上限为 `min(0.80 × 工作区高, 640)`），内容少时不缩到比它更小（FR-026 / 14.2）。
    /// </summary>
    public double QuickWindowHeight { get; set; } = 320;

    // ==================== FR-026 小窗按内容自适应尺寸 ====================

    /// <summary>
    /// 小窗尺寸模式：<c>auto</c>（按内容自适应，默认）/ <c>manual</c>（固定使用默认宽高）。
    /// <b>语义（阶段 5 批 3 起）</b>：本字段只由设置页的开关决定，**不再因拖拽边缘而改变**
    /// （拖拽只影响本次窗口，不跨次保留）；值名沿用 <c>manual</c> 以兼容已落盘配置（FR-026 / 14.2）。
    /// </summary>
    public string QuickWindowSizeMode { get; set; } = Layout.WindowSizePolicy.AutoMode;

    /// <summary>自适应改尺寸时用 140 ms 高度缓动动画；实测卡顿则置 false（FR-026 / 14.2.4）。</summary>
    public bool QuickWindowAdaptiveAnimation { get; set; } = true;

    // ==================== FR-017 剪贴板监听 ====================

    /// <summary>是否监听剪贴板：复制文本后自动弹出翻译小窗（默认关闭）。</summary>
    public bool ClipboardMonitorEnabled { get; set; }

    // ==================== FR-016 朗读 ====================

    /// <summary>划词翻译后自动朗读原文（默认关闭）。</summary>
    public bool AutoSpeakAfterSelect { get; set; }

    // ==================== FR-018 网络代理 ====================

    /// <summary>是否启用代理。</summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>代理作用范围：googleOnly（仅 Google 引擎）/ all（全局）。</summary>
    public string ProxyMode { get; set; } = "googleOnly";

    public string ProxyHost { get; set; } = "";

    /// <summary>代理协议：http（默认）或 socks5。FR-018 要求两者都支持。</summary>
    public string ProxyScheme { get; set; } = "http";

    public int ProxyPort { get; set; } = 7890;

    public string ProxyUserName { get; set; } = "";

    /// <summary>代理密码（DPAPI 密文，见 SecretStore；绝不明文落盘）。</summary>
    public string ProxyPasswordEncrypted { get; set; } = "";

    // ==================== FR-024 官方引擎：腾讯云 TMT ====================

    /// <summary>腾讯云 SecretId（非密钥的标识，明文便于排障）。</summary>
    public string TencentSecretId { get; set; } = "";

    /// <summary>腾讯云 SecretKey（DPAPI 密文）。</summary>
    public string TencentSecretKeyEncrypted { get; set; } = "";

    /// <summary>腾讯云区域（X-TC-Region，默认广州）。</summary>
    public string TencentRegion { get; set; } = "ap-guangzhou";

    // ==================== FR-024 官方引擎：百度翻译 ====================

    /// <summary>百度 APPID（与密钥配对使用，非密钥，明文）。</summary>
    public string BaiduAppId { get; set; } = "";

    /// <summary>百度翻译密钥（DPAPI 密文）。</summary>
    public string BaiduAppKeyEncrypted { get; set; } = "";

    // ==================== FR-024 官方引擎：Azure Translator ====================

    /// <summary>Azure Translator 订阅密钥（DPAPI 密文）。Azure 无独立的明文标识字段。</summary>
    public string AzureSubscriptionKeyEncrypted { get; set; } = "";

    /// <summary>Azure 资源区域（如 eastasia）；单服务资源可留空（留空时不发 Region 请求头）。</summary>
    public string AzureRegion { get; set; } = "";

    // ==================== FR-024 官方引擎：DeepL ====================

    /// <summary>DeepL Authentication Key（DPAPI 密文）。</summary>
    public string DeepLApiKeyEncrypted { get; set; } = "";

    /// <summary>
    /// DeepL 端点选择：true = 免费端点 api-free.deepl.com，false = 付费端点 api.deepl.com。
    /// 默认按 Key 是否以 ":fx" 结尾自动判定（Key 后缀优先），本字段用于人工覆盖；
    /// 判定规则见 <see cref="Translation.DeepLTranslator.ResolveEndpointUrl"/>。
    /// </summary>
    public bool DeepLUseFreeEndpoint { get; set; } = true;

    // ==================== FR-022 AI（LLM，OpenAI 兼容）引擎 ====================

    /// <summary>
    /// AI 引擎接口地址（13.7 默认值）。落库为「用户填写的原始形态」，
    /// 请求前按 <see cref="Translation.LlmTranslator.NormalizeEndpoint"/> 归一化到 /chat/completions。
    /// </summary>
    public string LlmBaseUrl { get; set; } = Translation.LlmTranslator.DefaultBaseUrl;

    /// <summary>AI 引擎 API Key（DPAPI 密文）。</summary>
    public string LlmApiKeyEncrypted { get; set; } = "";

    /// <summary>模型名（13.7 默认 deepseek-chat）。</summary>
    public string LlmModel { get; set; } = Translation.LlmTranslator.DefaultModel;

    /// <summary>自定义系统 Prompt；空 = 使用内置 Prompt（13.3.2）。</summary>
    public string LlmPrompt { get; set; } = "";

    /// <summary>采样温度（13.7 默认 0.2）。</summary>
    public double LlmTemperature { get; set; } = Translation.LlmTranslator.DefaultTemperature;

    // ==================== FR-020 引擎结果对比 ====================

    /// <summary>
    /// 术语表（P0 批 1 / spec §1.1）：<c>List&lt;GlossaryItem&gt;</c> 的 JSON 序列化结果，默认空表。
    /// 存 JSON 字符串而非对象数组：沿用本类扁平字段风格，JsonSettingsStore 零改动。
    /// 读写解析统一走 <see cref="Translation.GlossaryReplacer.Parse"/> / <c>Serialize</c>。
    /// </summary>
    public string GlossaryJson { get; set; } = "[]";

    /// <summary>
    /// 隐私模式（P0 批 1 / spec §2）：开启后不写翻译历史、暂停剪贴板监听、引擎统计不记录、
    /// --verbose 日志不落盘；翻译请求本身仍会发送（否则无法翻译）。生词本收藏为用户主动动作，不受影响。
    /// </summary>
    public bool PrivacyMode { get; set; }

    /// <summary>
    /// 阅读清洗（P0 批 1 / spec §3）：划词 / 剪贴板取到的多行文本合并 PDF 硬换行后再翻译，
    /// 默认开启；手动输入小窗的文本不清洗。
    /// </summary>
    public bool CleanClipboardText { get; set; } = true;

    /// <summary>对比引擎 Id 列表（逗号分隔，最多 3 个）；空 = 自动（当前引擎 + 首个其它已配置引擎）。</summary>
    public string CompareEngineIds { get; set; } = "";

    /// <summary>对比结果是否写入历史（13.7 默认写入）。</summary>
    public bool CompareIncludeInHistory { get; set; } = true;

    // ==================== FR-028 引擎失败自动降级 ====================

    /// <summary>引擎失败时自动改用备用引擎（默认开启）。只降一级、只作用于单引擎翻译路径（FR-028）。</summary>
    public bool EnableEngineFallback { get; set; } = true;

    /// <summary>
    /// 备用引擎 Id（默认 <c>bing</c> 非官方接口：零配置、国内可直连）。
    /// 该引擎未配置时视为「没有备用引擎」，不降级、直接报错（FR-028 / 14.4.1 边界 ②）。
    /// </summary>
    public string FallbackEngineId { get; set; } = "bing";

    // ==================== FR-021 OCR 截图翻译 ====================

    /// <summary>截图翻译热键（13.7 默认 Alt+O）。</summary>
    public string HotkeyCaptureTranslate { get; set; } = "Alt+O";

    /// <summary>OCR 识别语言；auto = 跟随系统语言偏好（13.7 默认 auto）。</summary>
    public string OcrLanguage { get; set; } = Capture.OcrLanguages.Auto;

    /// <summary>识别后自动翻译（13.7 默认开启）。</summary>
    public bool OcrAutoTranslate { get; set; } = true;

    /// <summary>遮罩透明度，映射到 Color.Capture.Scrim 的 alpha（13.7 默认 0.55）。</summary>
    public double OcrScrimOpacity { get; set; } = 0.55;

    // ==================== FR-027 截图翻译：钉图 ====================

    /// <summary>
    /// 截图识别后的处理方式：<c>pin</c>（钉图原位替换，默认）/ <c>text</c>（旧链路：文本进小窗）/ <c>both</c>
    /// （先钉图再开小窗）。这是 14.3.8 对既有行为**唯一的一处默认变更**，<c>text</c> 下 FR-021 行为不变。
    /// </summary>
    public string OcrOutputMode { get; set; } = Capture.OcrOutputMode.Pin;

    // ==================== FR-029 OCR 引擎升级路径（14.3.12） ====================

    /// <summary>
    /// 识别前预处理（FR-029-1 / 14.3.12.4）：<c>auto</c>（默认，原图识别先行，命中触发条件才对增强图重跑）/
    /// <c>on</c>（总是增强，仍受像素上限与择优约束）/ <c>off</c>（禁用，纯原图）。未知值按 auto 读取。
    /// </summary>
    public string OcrPreprocess { get; set; } = Capture.OcrPreprocess.ModeAuto;

    /// <summary>
    /// 云端 OCR 引擎（FR-029-2 预留字段，落地批实现）：off（默认，纯本地零上云）/ tencent / azure。
    /// 本批仅落字段不加 UI，识别链路不读取。
    /// </summary>
    public string OcrCloudEngine { get; set; } = "off";

    /// <summary>
    /// 本地 OCR 识别引擎（FR-030 / 14.9.3）：<c>windows</c>（默认，Windows.Media.Ocr，现状行为）/
    /// <c>paddle</c>（PaddleOCR ONNX PP-OCRv5 mobile，识别更准尤其斜体/小字、慢 2~4 倍；模型已内置，
    /// 初始化失败或识别异常时自动回退 windows 并一次性提示）。未知值按 windows 读取。
    /// </summary>
    public string OcrLocalEngine { get; set; } = Capture.OcrEngineNames.Windows;

    /// <summary>
    /// paddle 模型会话是否常驻内存（FR-030 / 14.9.3）：<c>false</c>（默认，空闲 5 分钟自动释放，
    /// 下次识别重建约 0.5s；保住常驻 <120MB 口径）/ <c>true</c>（常驻，常驻期内存口径放宽为 <200MB）。
    /// </summary>
    public bool OcrPaddleResident { get; set; }

    /// <summary>钉图时默认显示译文；false = 先显示原文，点工具条「显示译文」才翻译并原位替换（14.6 默认 true）。</summary>
    public bool OcrInPlaceReplace { get; set; } = true;

    /// <summary>每格滚轮的等比缩放倍数（14.6 默认 1.1；Shift + 滚轮用 1 + (step − 1) × 0.2 的精细步进）。</summary>
    public double PinZoomStep { get; set; } = 1.1;

    /// <summary>最小缩放倍数（14.6 默认 0.25）。</summary>
    public double PinZoomMin { get; set; } = 0.25;

    /// <summary>最大缩放倍数（14.6 默认 4.0；实际还受所在工作区尺寸约束，保证钉图始终完整可见）。</summary>
    public double PinZoomMax { get; set; } = 4.0;

    /// <summary>新钉图的初始不透明度（0.3~1.0）；单张调整不写回本字段（14.6 默认 1.0）。</summary>
    public double PinOpacity { get; set; } = 1.0;

    /// <summary>同时存在的钉图张数上限（14.6 默认 5）；达到即拒绝新钉图并提示，不静默关闭已有钉图。</summary>
    public int PinMaxCount { get; set; } = 5;

    /// <summary>单张钉图像素上限（14.6 默认 4,000,000）；超过则等比缩小后钉图并提示，不拒绝。</summary>
    public int PinMaxPixelsPerImage { get; set; } = 4_000_000;

    /// <summary>全部钉图像素总量上限（14.6 默认 12,000,000）；达到即拒绝新钉图并提示。</summary>
    public int PinMaxTotalPixels { get; set; } = 12_000_000;

    /// <summary>
    /// 钉图工具条 2.5 s 后淡出到 35% 透明（**已停用**，v1.2 修复批 ④）：工具条已移到截图下方常驻条带，
    /// 永不遮挡图片文字，自动淡出失去意义。字段与设置页开关暂保留以兼容旧配置，不再影响钉图行为。
    /// </summary>
    public bool PinToolbarAutoFade { get; set; } = true;
}
