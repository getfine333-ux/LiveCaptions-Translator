using System.IO;
using System.Text;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class TranscriptExporter
    {
        public static void ExportToTxt(List<TranslationRecord> records, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Meeting Transcript");
            sb.AppendLine();

            string lastDatePart = string.Empty;

            foreach (var record in records)
            {
                if (!record.IsError && string.IsNullOrEmpty(record.TranslatedText))
                    continue;

                string timePart = record.Timestamp;
                if (DateTime.TryParse(record.Timestamp, out DateTime dt))
                {
                    timePart = dt.ToString("HH:mm:ss");
                    string datePart = dt.ToString("yyyy-MM-dd");
                    if (datePart != lastDatePart)
                    {
                        sb.AppendLine($"# Date: {datePart}");
                        sb.AppendLine();
                        lastDatePart = datePart;
                    }
                }

                sb.AppendLine($"## {timePart}");
                sb.AppendLine();
                sb.AppendLine("EN:");
                sb.AppendLine(record.SourceText);
                sb.AppendLine();
                sb.AppendLine("ZH:");
                if (record.IsError)
                    sb.AppendLine($"[翻译失败] {record.TranslatedText}");
                else
                    sb.AppendLine(record.TranslatedText);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        public static void ExportToMarkdown(List<TranslationRecord> records, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 会议记录 / Meeting Transcript");
            sb.AppendLine();

            string lastDatePart = string.Empty;

            foreach (var record in records)
            {
                if (!record.IsError && string.IsNullOrEmpty(record.TranslatedText))
                    continue;

                string timePart = record.Timestamp;
                if (DateTime.TryParse(record.Timestamp, out DateTime dt))
                {
                    timePart = dt.ToString("HH:mm:ss");
                    string datePart = dt.ToString("yyyy-MM-dd");
                    if (datePart != lastDatePart)
                    {
                        sb.AppendLine($"## 📅 {datePart}");
                        sb.AppendLine();
                        lastDatePart = datePart;
                    }
                }

                sb.AppendLine($"### ⏱ {timePart}");
                sb.AppendLine();
                sb.AppendLine("**EN:**");
                sb.AppendLine($"> {record.SourceText}");
                sb.AppendLine();
                sb.AppendLine("**ZH:**");
                if (record.IsError)
                    sb.AppendLine($"> [翻译失败] {record.TranslatedText}");
                else
                    sb.AppendLine($"> {record.TranslatedText}");
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        public static void ExportReviewTemplate(List<TranslationRecord> records, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("请将以下英文会议 transcript 整理为中文学习复盘。");
            sb.AppendLine();
            sb.AppendLine("要求：");
            sb.AppendLine("1. 总结会议主题和背景。");
            sb.AppendLine("2. 按议题分段整理，不要逐字翻译。");
            sb.AppendLine("3. 提取关键术语，保留英文原词，并给出中文解释。");
            sb.AppendLine("4. 提取重要结论。");
            sb.AppendLine("5. 提取 Action Items。");
            sb.AppendLine("6. 标出我需要继续学习的知识点。");
            sb.AppendLine("7. 如果 transcript 中有明显识别错误，请根据上下文纠正。");
            sb.AppendLine("8. 最后生成一版 5 分钟快速复习版。");
            sb.AppendLine();
            sb.AppendLine("以下是 transcript：");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var record in records)
            {
                if (record.IsError) continue;

                string timePart = record.Timestamp;
                if (DateTime.TryParse(record.Timestamp, out DateTime dt))
                    timePart = dt.ToString("HH:mm:ss");

                sb.AppendLine($"[{timePart}]");
                sb.AppendLine($"EN: {record.SourceText}");
                sb.AppendLine($"ZH: {record.TranslatedText}");
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        public static void ExportToJsonl(List<TranslationRecord> records, string filePath)
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false
            };

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            foreach (var record in records)
            {
                string json = System.Text.Json.JsonSerializer.Serialize(record, options);
                writer.WriteLine(json);
            }
        }
    }
}
