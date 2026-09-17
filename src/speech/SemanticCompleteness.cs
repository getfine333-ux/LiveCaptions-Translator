using System.Text;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.speech;

// Structural complement requirements are independent of ASR punctuation votes.
// This deliberately covers explicit constructions rather than guessing whether
// every Chinese clause is a grammatical sentence or waiting on a network model.
public static class SemanticCompleteness
{
    public static bool RequiresComplement(string value)
    {
        string core = value.Trim().TrimEnd('。', '.', '！', '!', '？', '?', '，', ',', '；', ';', '：', ':', '…');
        return Regex.IsMatch(core,
            @"(?:(?:改造|转化|转换|变|做|加工|塑造|设计)(?:成|为)(?:了)?|(?:称|定义|视|看|归类)(?:为|作)(?:了)?|" +
            @"(?:取决于|依赖于|等于|意味着|包括|例如|比如|所谓的|相当于)|" +
            @"(?:输入|输出|映射|投影|聚焦|传递|送)(?:到|至|给)(?:了)?|交给(?:了)?)$");
    }

    // Once a later boundary is confirmed, remove only demonstrably internal
    // false stops. Alignment continues to use the original decoder snapshot.
    public static string RepairInternalStops(string value)
    {
        var result = new StringBuilder();
        char quoteEnd = '\0';
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (quoteEnd != '\0')
            {
                result.Append(c);
                if (c == quoteEnd) quoteEnd = '\0';
                continue;
            }
            quoteEnd = c switch { '“' => '”', '「' => '」', '『' => '』', '"' => '"', _ => '\0' };
            if (c is '。' or '.' && i > 0 && value[i - 1] is >= '\u3400' and <= '\u9fff' &&
                value[(i + 1)..].Any(char.IsLetterOrDigit) && RequiresComplement(value[..i])) continue;
            result.Append(c);
        }
        return result.ToString();
    }
}
