using System;
using System.Collections.Generic;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 在线翻译（照上游 <c>translationservice</c> / <c>translationjob</c> 的「按行对齐」语义）：
/// 端点校验、请求体、响应解析、行数对账、语言判定。
/// </summary>
/// <remarks>
/// 网络那层<b>不在自测里跑</b>（要真端点）：仓库外 <c>capture-overlay-smoke</c> 起一个本地
/// <c>HttpListener</c> 当 OpenAI 兼容端点，端到端断言「点翻译 → 画布出译文 → 复制译文」。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void TranslationCases()
    {
        TranslationEndpointCases();
        TranslationRequestCases();
        TranslationParseCases();
        TranslationLanguageCases();
    }

    private static void TranslationEndpointCases()
    {
        Check.True(TranslationClient.ValidateEndpoint("https://api.openai.com/v1", out string ok1),
            "https 端点合法");
        Check.Equal("", ok1, "合法端点没有错误文案");
        Check.True(TranslationClient.ValidateEndpoint(" http://127.0.0.1:1234/v1 ", out _),
            "两端空白会被去掉，http 也认");
        Check.True(TranslationClient.ValidateEndpoint("http://localhost:8080/v1/chat/completions", out _),
            "写全 /chat/completions 也合法");

        Check.False(TranslationClient.ValidateEndpoint("", out string empty),
            "空端点不合法");
        Check.True(empty.Contains("端点"), "空端点的提示提到端点");
        Check.False(TranslationClient.ValidateEndpoint("ftp://example.com/v1", out string scheme),
            "ftp 不合法");
        Check.True(scheme.Contains("http"), "协议错的提示提到 http");
        Check.False(TranslationClient.ValidateEndpoint("api.openai.com/v1", out _),
            "相对地址不合法");
        Check.False(TranslationClient.ValidateEndpoint("https://example.com/v1#frag", out string frag),
            "带 #fragment 不合法");
        Check.True(frag.Contains("fragment"), "fragment 的提示说清原因");

        // 地址拼装：写没写 /chat/completions 都认，尾部斜杠无关
        Check.Equal("https://api.openai.com/v1/chat/completions",
            TranslationClient.ChatUrl("https://api.openai.com/v1"), "端点补 /chat/completions");
        Check.Equal("https://api.openai.com/v1/chat/completions",
            TranslationClient.ChatUrl("https://api.openai.com/v1/"), "尾部斜杠不会拼出双斜杠");
        Check.Equal("https://api.openai.com/v1/chat/completions",
            TranslationClient.ChatUrl("https://api.openai.com/v1/chat/completions"),
            "已经写全就不重复拼");

        // 是否「配好了」
        Check.False(TranslationConfig.Empty.IsReady, "空配置不算就绪");
        Check.True(TranslationConfig.Normalize("https://x/v1", "gpt-4o-mini", "", null, null, 0).IsReady,
            "端点 + 模型就够，密钥可以留空");
        Check.False(TranslationConfig.Normalize("https://x/v1", "", "", null, null, 0).IsReady,
            "缺模型不算就绪");
        Check.Equal(30000, TranslationConfig.Normalize("https://x/v1", "m", "", null, null, 0).TimeoutMs,
            "超时 0 → 默认 30 秒");
        Check.Equal(1000, TranslationConfig.Normalize("https://x/v1", "m", "", null, null, 5).TimeoutMs,
            "超时低于 1 秒被夹到 1000");
        Check.Equal(120000, TranslationConfig.Normalize("https://x/v1", "m", "", null, null, 9999999)
            .TimeoutMs, "超时高于 2 分钟被夹到 120000");
        Check.Equal(TranslationLanguages.DefaultTarget,
            TranslationConfig.Normalize("https://x/v1", "m", "", "不认识的语言", null, 0).Target,
            "认不出的目标语言回落到默认");
        Check.Equal(TranslationLanguages.Auto,
            TranslationConfig.Normalize("https://x/v1", "m", "", null, "乱写", 0).Source,
            "认不出的源语言回落到自动");
    }

    private static void TranslationRequestCases()
    {
        TranslationConfig config = TranslationConfig.Normalize("https://api.example.com/v1",
            "gpt-4o-mini", "sk-test", "zh-CN", "en", 15000);

        var lines = new[] { "Hello", "", "World \"quoted\"" };
        string body = TranslationClient.BuildBody(config, lines);

        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        Check.True(root["model"]!.GetValue<string>() == "gpt-4o-mini", "请求体带模型名");
        Check.Equal(0, root["temperature"]!.GetValue<int>(), "请求体固定 temperature=0（翻译不需要发散）");
        Check.False(root["stream"]!.GetValue<bool>(), "请求体不是流式");

        System.Text.Json.Nodes.JsonArray messages = root["messages"]!.AsArray();
        Check.Equal(2, messages.Count, "两条消息：系统 + 用户");
        Check.Equal("system", messages[0]!["role"]!.GetValue<string>(), "第一条是系统提示");

        string system = messages[0]!["content"]!.GetValue<string>();
        Check.True(system.Contains("简体中文"), "系统提示里写明目标语言的中文名");
        Check.True(system.Contains("English"), "系统提示里写明源语言");
        Check.True(system.Contains("JSON 字符串数组"), "系统提示要求 JSON 数组");
        Check.True(system.Contains("元素个数与输入相同"), "系统提示要求行数一致");

        // 用户内容就是行数组本身（含空行与引号转义）
        string user = messages[1]!["content"]!.GetValue<string>();
        System.Text.Json.Nodes.JsonArray? arr = System.Text.Json.Nodes.JsonNode.Parse(user)
            as System.Text.Json.Nodes.JsonArray;
        Check.True(arr != null, "用户内容是一个 JSON 数组");
        Check.Equal(3, arr!.Count, "数组项数 = 输入行数");
        Check.Equal("Hello", arr[0]!.GetValue<string>(), "第一行原样");
        Check.Equal("", arr[1]!.GetValue<string>(), "空行原样保留");
        Check.Equal("World \"quoted\"", arr[2]!.GetValue<string>(), "引号原样（不被二次转义掉）");
        Check.True(body.Contains("简体中文"), "请求体本身是 UTF-8 中文，不是 \\uXXXX 转义");

        // 自动判语言时系统提示不写死源语言
        TranslationConfig auto = config with { Source = TranslationLanguages.Auto };
        string autoBody = TranslationClient.BuildBody(auto, lines);
        Check.True(autoBody.Contains("按内容判断"), "源语言 auto 时让模型自己判断");
    }

    private static void TranslationParseCases()
    {
        // 1) 标准 OpenAI 响应：choices[0].message.content 是一个 JSON 数组字符串
        const string openAi =
            "{\"id\":\"x\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":\"[\\\"\\u4f60\\u597d\\\",\\\"\\u4e16\\u754c\\\"]\"}}]}";
        Check.Equal("[\"你好\",\"世界\"]", TranslationClient.ExtractContent(openAi) ?? "",
            "取 choices[0].message.content");

        // content 是分段数组（新版多模态格式）
        const string parts =
            "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[\\\"a\\\"]\"}]}}]}";
        Check.Equal("[\"a\"]", TranslationClient.ExtractContent(parts) ?? "", "content 是分段数组时拼起来");

        // 旧式 text
        Check.Equal("hi", TranslationClient.ExtractContent("{\"choices\":[{\"text\":\"hi\"}]}") ?? "",
            "兼容旧式 choices[0].text");

        Check.True(TranslationClient.ExtractContent("") == null, "空响应没有正文");
        Check.True(TranslationClient.ExtractContent("not json") == null, "非 JSON 响应没有正文");
        Check.True(TranslationClient.ExtractContent("{\"choices\":[]}") == null, "没有 choices 项时没有正文");

        // 2) 围栏
        Check.Equal("[\"a\"]", TranslationClient.StripFence("```json\n[\"a\"]\n```"), "去掉 ```json 围栏");
        Check.Equal("[\"a\"]", TranslationClient.StripFence("  [\"a\"]  "), "没有围栏就只 trim");

        // 3) 行数对账
        Check.True(TranslationClient.TryParseLines("[\"你好\",\"世界\"]", 2,
            out IReadOnlyList<string> two, out string e1), "两行 JSON 数组解出两行");
        Check.Equal("你好", two[0], "第一行");
        Check.Equal("世界", two[1], "第二行");
        Check.Equal("", e1, "成功时没有错误文案");

        Check.False(TranslationClient.TryParseLines("[\"只有一行\"]", 2, out _, out string mismatch),
            "行数对不上算失败");
        Check.True(mismatch.Contains("对不上"), "行数不符的提示说「对不上」");

        // 围栏 + 数组
        Check.True(TranslationClient.TryParseLines("```json\n[\"甲\",\"乙\"]\n```", 2,
            out IReadOnlyList<string> fenced, out _), "围栏里的数组也认得");
        Check.Equal("乙", fenced[1], "围栏里的第二行");

        // 按行切（模型回了纯文本，每行一句）
        Check.True(TranslationClient.TryParseLines("第一句\n第二句", 2,
            out IReadOnlyList<string> plain, out _), "纯文本按行切也能对上");
        Check.Equal("第二句", plain[1], "纯文本第二行");

        // 带序号的行
        Check.True(TranslationClient.TryParseLines("1. 第一句\n2. 第二句", 2,
            out IReadOnlyList<string> numbered, out _), "行首序号会被去掉");
        Check.Equal("第一句", numbered[0], "序号去掉后的第一行");
        Check.Equal("第二句", numbered[1], "序号去掉后的第二行");

        // 只有一行：整段就是译文（哪怕带换行）
        Check.True(TranslationClient.TryParseLines("Hello\nWorld", 1,
            out IReadOnlyList<string> single, out _), "只有一行时整段就是译文");
        Check.Equal("Hello World", single[0], "单行的换行折成空格");

        Check.False(TranslationClient.TryParseLines("   ", 2, out _, out string empty),
            "空正文算失败");
        Check.True(empty.Contains("空内容"), "空正文的提示说「空内容」");
        Check.False(TranslationClient.TryParseLines("[\"a\"]", 0, out _, out string none),
            "没有要翻译的内容算失败");
        Check.True(none.Contains("没有要翻译"), "零行的提示说「没有要翻译」");

        // 数组里不是字符串：退化成 JSON 文本（不丢内容）
        Check.True(TranslationClient.TryParseLines("[1,2]", 2, out IReadOnlyList<string> nums, out _),
            "非字符串项按 JSON 文本收下");
        Check.Equal("1", nums[0], "数字项转成文本");

        // strip numbering 单独的边界
        Check.Equal("正文", TranslationClient.StripNumbering("3、正文"), "中文顿号序号");
        Check.Equal("正文", TranslationClient.StripNumbering("- 正文"), "破折号列表记号");
        Check.Equal("2026 年", TranslationClient.StripNumbering("2026 年"),
            "纯数字开头但没有标点的行不动");

        // 状态码翻人话
        Check.True(TranslationClient.Describe(System.Net.HttpStatusCode.Unauthorized, "")
            .Contains("密钥"), "401 的提示提到密钥");
        Check.True(TranslationClient.Describe(System.Net.HttpStatusCode.NotFound, "")
            .Contains("路径"), "404 的提示提到路径");
        Check.True(TranslationClient.Describe(System.Net.HttpStatusCode.TooManyRequests, "")
            .Contains("限流"), "429 的提示提到限流");
        Check.True(TranslationClient.Describe(System.Net.HttpStatusCode.InternalServerError, "boom")
            .Contains("boom"), "5xx 带上响应体片段");
    }

    private static void TranslationLanguageCases()
    {
        Check.Equal("zh-CN", TranslationLanguages.Detect("这是一段中文"), "汉字 → 简体中文");
        Check.Equal("ja", TranslationLanguages.Detect("これは日本語です"), "假名 → 日语");
        Check.Equal("ko", TranslationLanguages.Detect("안녕하세요"), "谚文 → 韩语");
        Check.Equal("ru", TranslationLanguages.Detect("Привет мир"), "西里尔 → 俄语");
        Check.Equal("en", TranslationLanguages.Detect("Hello world"), "拉丁 → 英语");
        Check.Equal(TranslationLanguages.Auto, TranslationLanguages.Detect("123 456 !!!"),
            "只有数字与标点时判不出来");
        Check.Equal(TranslationLanguages.Auto, TranslationLanguages.Detect(""), "空文本判不出来");

        Check.Equal("zh-CN", TranslationLanguages.Normalize("zh-cn"), "语言键大小写不敏感");
        Check.Equal(TranslationLanguages.DefaultTarget, TranslationLanguages.Normalize(null),
            "null 回落到默认目标");
        Check.Equal(TranslationLanguages.Auto, TranslationLanguages.NormalizeSource(null),
            "源语言默认 auto");

        Check.Equal("简体中文", TranslationLanguages.LabelOf("zh-CN"), "标签是中文名");
        Check.Equal("English", TranslationLanguages.LabelOf("en"), "英语标签");
        Check.True(TranslationLanguages.PromptOf("en").Contains("en"), "提示词写法带语言 code");

        Check.Equal("a\nb", TranslationLanguages.Join(new[] { "a", "", "b" }),
            "多行拼接跳过空行");
        Check.Equal(9, TranslationLanguages.All.Count, "语言目录 9 门");
    }
}
