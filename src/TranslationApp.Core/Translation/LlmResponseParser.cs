using System.Net;
using System.Text.Json;

namespace TranslationApp.Core.Translation;

/// <summary>
/// AI（LLM，OpenAI 兼容）响应解析与错误归类（13.3.1）。
/// 成功：<c>{"choices":[{"message":{"role":"assistant","content":"你好"}}]}</c>，只取 content 并 trim，不做任何改写。
/// 失败：各家兼容实现不一，错误文案可能在 <c>error.message</c>、<c>message</c> 或 <c>error</c> 字符串里。
/// </summary>
public static class LlmResponseParser
{
    /// <summary>
    /// 余额类关键词（13.3.1：message 含余额类关键词 → QuotaExceeded）。
    /// 部分兼容实现用 400 而非 402 表达欠费，故不能只看状态码。
    /// </summary>
    private static readonly string[] BalanceKeywords =
    [
        "insufficient", "balance", "quota", "credit", "arrears", "余额", "欠费", "额度", "充值",
    ];

    /// <summary>
    /// 解析成功响应，返回译文。取不到 choices[0].message.content（含空 content）视为引擎异常，
    /// 文案提示检查接口地址与模型名（13.3.1）。
    /// </summary>
    public static string Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new TranslationException(TranslationErrorType.Engine, EngineDetailMessage("返回空响应"));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TranslationException(TranslationErrorType.Engine, EngineDetailMessage("响应格式异常"), ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new TranslationException(TranslationErrorType.Engine, EngineDetailMessage("响应缺少 choices"));
            }

            var message = choices[0].TryGetProperty("message", out var messageElement) ? messageElement : default;
            if (message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                throw new TranslationException(TranslationErrorType.Engine, EngineDetailMessage("响应缺少 content"));
            }

            // 13.3.1：只 trim 首尾空白，不做任何改写
            var text = content.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw new TranslationException(TranslationErrorType.Engine, EngineDetailMessage("返回空译文"));
            }

            return text;
        }
    }

    /// <summary>取错误正文里的 message（取不到返回 null）。</summary>
    public static string? TryReadErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                {
                    return nested.GetString();
                }
            }

            return root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>错误文案是否属于「余额 / 额度」类（13.3.1）。</summary>
    internal static bool IsBalanceError(string? message) =>
        message is not null
        && BalanceKeywords.Any(keyword => message.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 状态码归类（13.3.1）：401 / 403 → InvalidKey；402 / 429 → QuotaExceeded；
    /// message 含余额类关键词 → QuotaExceeded；其余（400 / 404 / 5xx）→ Engine。
    /// </summary>
    internal static TranslationErrorType MapStatus(HttpStatusCode status, string? message)
    {
        var code = (int)status;
        if (code is 401 or 403)
        {
            return TranslationErrorType.InvalidKey;
        }

        if (code is 402 or 429 || IsBalanceError(message))
        {
            return TranslationErrorType.QuotaExceeded;
        }

        return TranslationErrorType.Engine;
    }

    /// <summary>构造带分类与用户可读文案的异常（文案不含 Key）。</summary>
    internal static TranslationException CreateError(HttpStatusCode status, string? message)
    {
        var errorType = MapStatus(status, message);
        var detail = string.IsNullOrWhiteSpace(message) ? "" : $"（{message}）";

        var text = errorType switch
        {
            TranslationErrorType.InvalidKey =>
                $"API Key 无效或未授权{detail}，请检查密钥（HTTP {(int)status}）",
            TranslationErrorType.QuotaExceeded =>
                $"额度用尽或触发限流{detail}，请检查账号余额（HTTP {(int)status}）",
            _ =>
                $"AI 引擎异常{detail}（HTTP {(int)status}）：请检查接口地址与模型名",
        };

        return new TranslationException(errorType, text);
    }

    /// <summary>引擎异常的细节文案（统一带上排障指引）。</summary>
    private static string EngineDetailMessage(string detail) =>
        $"AI 引擎{detail}：请检查接口地址与模型名";
}
