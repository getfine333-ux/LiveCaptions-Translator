using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public class JsonlLogger : IDisposable
    {
        private StreamWriter? _writer;
        private string _filePath;
        private int _sequence = 0;
        private readonly object _lock = new();
        private bool _disposed = false;
        private readonly string _saveDir;
        private readonly string _sessionId;
        private int _fileIndex = 0;
        private const long MAX_FILE_SIZE = 10 * 1024 * 1024; // 10MB

        public string SessionId { get; }
        public int RecordCount => _sequence;

        public JsonlLogger(string saveDir, string sessionId)
        {
            SessionId = sessionId;
            _saveDir = saveDir;
            _sessionId = sessionId;
            Directory.CreateDirectory(saveDir);
            _filePath = Path.Combine(saveDir, $"{sessionId}.jsonl");
            _writer = new StreamWriter(_filePath, append: true, encoding: new UTF8Encoding(true))
            {
                AutoFlush = true
            };
        }

        public void Log(TranslationRecord record)
        {
            if (_disposed) return;

            lock (_lock)
            {
                try
                {
                    // Check file size and rotate if needed
                    CheckAndRotateFile();

                    record.Sequence = ++_sequence;
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    };
                    string json = JsonSerializer.Serialize(record, options);
                    _writer?.WriteLine(json);
                    _writer?.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[JsonlLogger] Write error: {ex.Message}");
                    DiagLog.Write($"[JsonlLogger] Write error: {ex.Message}");
                }
            }
        }

        private void CheckAndRotateFile()
        {
            try
            {
                var fileInfo = new FileInfo(_filePath);
                if (fileInfo.Exists && fileInfo.Length >= MAX_FILE_SIZE)
                {
                    _writer?.Flush();
                    _writer?.Dispose();

                    _fileIndex++;
                    _filePath = Path.Combine(_saveDir, $"{_sessionId}_{_fileIndex}.jsonl");
                    _writer = new StreamWriter(_filePath, append: true, encoding: new UTF8Encoding(true))
                    {
                        AutoFlush = true
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JsonlLogger] File rotation error: {ex.Message}");
            }
        }

        public string GetFilePath() => _filePath;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                    _writer = null;
                }
                catch { }
            }
        }
    }

    public class TranslationRecord
    {
        [JsonPropertyName("translation_input")]
        public string? TranslationInput { get; set; }
        [JsonPropertyName("term_corrections")]
        public SourceTermEdit[]? TermCorrections { get; set; }
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("segment_id")]
        public long SegmentId { get; set; }
        [JsonPropertyName("revision")]
        public int Revision { get; set; }
        [JsonPropertyName("source_revision")]
        public int SourceRevision { get; set; }
        [JsonPropertyName("utterance_key")]
        public string UtteranceKey { get; set; } = "";
        [JsonPropertyName("is_final")]
        public bool IsFinal { get; set; }
        [JsonPropertyName("is_incomplete")]
        public bool IsIncomplete { get; set; }
        [JsonPropertyName("end_reason")]
        public string EndReason { get; set; } = "";
        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
        [JsonPropertyName("raw_source_text")]
        public string? RawSourceText { get; set; }
        [JsonPropertyName("is_pending")]
        public bool IsPending { get; set; }
        [JsonPropertyName("queue_ms")]
        public double QueueMs { get; set; }
        [JsonPropertyName("asr_ms")]
        public double AsrMs { get; set; }
        [JsonPropertyName("asr_queue_ms")]
        public double AsrQueueMs { get; set; }
        [JsonPropertyName("audio_start_sample")]
        public long? AudioStartSample { get; set; }
        [JsonPropertyName("audio_end_sample")]
        public long? AudioEndSample { get; set; }
        [JsonPropertyName("audio_stream_id")]
        public string AudioStreamId { get; set; } = "";
        [JsonPropertyName("continues_previous")]
        public bool ContinuesPrevious { get; set; }
        [JsonPropertyName("forced_end")]
        public bool ForcedEnd { get; set; }
        [JsonPropertyName("ready_ms")]
        public double ReadyMs { get; set; }
        [JsonPropertyName("source_ended_at")]
        public DateTimeOffset? SourceEndedAt { get; set; }
        [JsonPropertyName("source_to_ready_ms")]
        public double? SourceToReadyMs { get; set; }
        [JsonPropertyName("sequence")]
        public int Sequence { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("source_lang")]
        public string SourceLang { get; set; } = "en";

        [JsonPropertyName("target_lang")]
        public string TargetLang { get; set; } = "zh-CN";

        [JsonPropertyName("source_text")]
        public string SourceText { get; set; } = string.Empty;

        [JsonPropertyName("translated_text")]
        public string TranslatedText { get; set; } = string.Empty;

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("latency_ms")]
        public long LatencyMs { get; set; }

        [JsonPropertyName("is_error")]
        public bool IsError { get; set; }

        [JsonPropertyName("cache_hit")]
        public bool CacheHit { get; set; }
    }
}
