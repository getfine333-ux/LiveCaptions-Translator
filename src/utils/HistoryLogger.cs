// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.IO;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using CsvHelper;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = "Data Source=translation_history.db;";

        private static SqliteConnection _sharedConnection;
        private static readonly object _connectionLock = new object();
        private static Timer? _healthCheckTimer;
        private static DateTime _lastHealthCheck = DateTime.MinValue;

        static SQLiteHistoryLogger()
        {
            InitializeDatabase();

            // Setup periodic health check (every 5 minutes)
            _healthCheckTimer = new Timer(HealthCheckCallback, null,
                TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private static void HealthCheckCallback(object? state)
        {
            try
            {
                EnsureConnection();
                _lastHealthCheck = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HistoryLogger] Health check failed: {ex.Message}");
            }
        }

        private static void EnsureConnection()
        {
            lock (_connectionLock)
            {
                try
                {
                    if (_sharedConnection == null)
                    {
                        _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                        _sharedConnection.Open();
                    }
                    else if (_sharedConnection.State != System.Data.ConnectionState.Open)
                    {
                        _sharedConnection.Dispose();
                        _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                        _sharedConnection.Open();
                    }
                    else
                    {
                        // Test connection with a simple query
                        using var cmd = new SqliteCommand("SELECT 1", _sharedConnection);
                        cmd.ExecuteScalar();
                    }
                }
                catch
                {
                    try { _sharedConnection?.Dispose(); } catch { }
                    _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                    _sharedConnection.Open();
                }
            }
        }

        private static void InitializeDatabase()
        {
            GetConnection();

            using (var command = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );", GetConnection()))
            {
                command.ExecuteNonQuery();
            }
        }

        public static async Task UpsertSegment(string sessionId, TranslationResult result)
        {
            // A separate connection prevents UI history reads from sharing an active writer.
            using var connection = new SqliteConnection(CONNECTION_STRING);
            await connection.OpenAsync().ConfigureAwait(false);
            using var transaction = connection.BeginTransaction();
            using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = @"CREATE TABLE IF NOT EXISTS SegmentHistory (
                SessionId TEXT NOT NULL, SegmentId INTEGER NOT NULL,
                HistoryId INTEGER NOT NULL, Revision INTEGER NOT NULL, SourceRevision INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (SessionId, SegmentId));";
            await map.ExecuteNonQueryAsync().ConfigureAwait(false);
            map.CommandText = "PRAGMA table_info(SegmentHistory)";
            bool hasSourceRevision = false;
            using (var columns = await map.ExecuteReaderAsync().ConfigureAwait(false))
                while (await columns.ReadAsync().ConfigureAwait(false))
                    hasSourceRevision |= columns.GetString(1) == "SourceRevision";
            if (!hasSourceRevision)
            {
                map.CommandText = "ALTER TABLE SegmentHistory ADD COLUMN SourceRevision INTEGER NOT NULL DEFAULT 0";
                await map.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            map.CommandText = "SELECT HistoryId, Revision, SourceRevision FROM SegmentHistory WHERE SessionId=@Session AND SegmentId=@Segment";
            map.Parameters.AddWithValue("@Session", sessionId);
            map.Parameters.AddWithValue("@Segment", result.Segment.Id);
            long? historyId = null;
            int revision = -1;
            int sourceRevision = -1;
            using (var reader = await map.ExecuteReaderAsync().ConfigureAwait(false))
                if (await reader.ReadAsync().ConfigureAwait(false))
                { historyId = reader.GetInt64(0); revision = reader.GetInt32(1); sourceRevision = reader.GetInt32(2); }
            if (sourceRevision > result.Segment.SourceRevision ||
                (sourceRevision == result.Segment.SourceRevision && revision >= result.Revision))
            { transaction.Commit(); return; }
            using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = historyId.HasValue
                ? "UPDATE TranslationHistory SET SourceText=@Source, TranslatedText=@Text WHERE Id=@Id"
                : @"INSERT INTO TranslationHistory(Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed)
                    VALUES(@Time,@Source,@Text,@Target,@Api); SELECT last_insert_rowid();";
            write.Parameters.AddWithValue("@Source", result.CorrectedSource ?? result.Segment.Text);
            write.Parameters.AddWithValue("@Text", result.Text);
            if (historyId.HasValue) write.Parameters.AddWithValue("@Id", historyId.Value);
            else
            {
                write.Parameters.AddWithValue("@Time", result.Segment.CreatedAt.ToUnixTimeSeconds());
                write.Parameters.AddWithValue("@Target", result.Segment.TargetLang);
                write.Parameters.AddWithValue("@Api", result.Segment.Provider);
            }
            if (historyId.HasValue) await write.ExecuteNonQueryAsync().ConfigureAwait(false);
            else historyId = Convert.ToInt64(await write.ExecuteScalarAsync().ConfigureAwait(false));
            map.CommandText = @"INSERT INTO SegmentHistory(SessionId,SegmentId,HistoryId,Revision,SourceRevision)
                VALUES(@Session,@Segment,@History,@Revision,@SourceRevision)
                ON CONFLICT(SessionId,SegmentId) DO UPDATE SET Revision=excluded.Revision,SourceRevision=excluded.SourceRevision";
            map.Parameters.AddWithValue("@History", historyId.Value);
            map.Parameters.AddWithValue("@Revision", result.Revision);
            map.Parameters.AddWithValue("@SourceRevision", result.Segment.SourceRevision);
            await map.ExecuteNonQueryAsync().ConfigureAwait(false);
            transaction.Commit();
        }
        private static SqliteConnection GetConnection()
        {
            EnsureConnection();
            return _sharedConnection;
        }

        public static async Task LogTranslation(string sourceText, string translatedText,
            string targetLanguage, string apiUsed, CancellationToken token = default)
        {
            string insertQuery = @"
                INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)";

            using (var command = new SqliteCommand(insertQuery, GetConnection()))
            {
                command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("@SourceText", sourceText);
                command.Parameters.AddWithValue("@TranslatedText", translatedText);
                command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
                command.Parameters.AddWithValue("@ApiUsed", apiUsed);
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page, int maxRow, string searchText, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            int totalCount = 0;
            using (var command = new SqliteCommand(@"
                SELECT COUNT(*) 
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search", GetConnection()))

            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                totalCount = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            }

            // 计算最大页数，至少为 1
            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            int offset = Math.Max(0, (page - 1) * maxRow);

            using (var command = new SqliteCommand(@"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search
                ORDER BY Timestamp DESC
                LIMIT @maxRow OFFSET @offset", GetConnection()))

            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                command.Parameters.AddWithValue("@maxRow", maxRow);
                command.Parameters.AddWithValue("@offset", offset);

                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                        DateTime localTime;
                        try
                        {
                            localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                        }
                        catch (FormatException)
                        {
                            // DEPRECATED
                            await MigrateOldTimestampFormat();
                            return await LoadHistoryAsync(page, maxRow, string.Empty);
                        }
                        history.Add(new TranslationHistoryEntry
                        {
                            Timestamp = localTime.ToString("yyyy-MM-dd HH:mm"),
                            TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                            SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                            TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                            TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                            ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                        });
                    }
                }
            }
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            string selectQuery = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'";
            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            {
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task<string> LoadLastSourceText(CancellationToken token = default)
        {
            string selectQuery = @"
                SELECT SourceText
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                    return reader.GetString(reader.GetOrdinal("SourceText"));
                else
                    return string.Empty;
            }
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            string selectQuery = @"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            using (var command = new SqliteCommand(selectQuery, GetConnection()))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (await reader.ReadAsync(token))
                {
                    string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                    return new TranslationHistoryEntry
                    {
                        Timestamp = localTime.ToString("yyyy-MM-dd HH:mm"),
                        TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                        TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                        TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                        ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                    };
                }
                return null;
            }
        }

        public static async Task DeleteLastTranslation(CancellationToken token = default)
        {
            using (var command = new SqliteCommand(@"
                DELETE FROM TranslationHistory
                WHERE Id IN (SELECT Id FROM TranslationHistory ORDER BY Id DESC LIMIT 1)",
                GetConnection()))
            {
                await command.ExecuteNonQueryAsync(token);
            }
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var history = await LoadAllAsync(token);

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        private static async Task<List<TranslationHistoryEntry>> LoadAllAsync(CancellationToken token)
        {
            var history = new List<TranslationHistoryEntry>();
            string selectQuery = @"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Timestamp ASC";

            using var command = new SqliteCommand(selectQuery, GetConnection());
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                history.Add(new TranslationHistoryEntry
                {
                    Timestamp = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                    TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                    TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                    ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                });
            }
            return history;
        }

        public static async Task ExportToTxt(string filePath, CancellationToken token = default)
        {
            var history = await LoadAllAsync(token);
            var sb = new StringBuilder();
            sb.AppendLine("# Meeting Transcript");
            sb.AppendLine();

            foreach (var h in history)
            {
                sb.AppendLine($"## {h.Timestamp}");
                sb.AppendLine();
                sb.AppendLine("EN:");
                sb.AppendLine(h.SourceText);
                sb.AppendLine();
                sb.AppendLine("ZH:");
                sb.AppendLine(h.TranslatedText);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(true), token);
        }

        public static async Task ExportToMarkdown(string filePath, CancellationToken token = default)
        {
            var history = await LoadAllAsync(token);
            var sb = new StringBuilder();
            sb.AppendLine("# 会议记录 / Meeting Transcript");
            sb.AppendLine();

            foreach (var h in history)
            {
                sb.AppendLine($"### ⏱ {h.Timestamp}");
                sb.AppendLine();
                sb.AppendLine("**原文:**");
                sb.AppendLine($"> {h.SourceText}");
                sb.AppendLine();
                sb.AppendLine("**译文:**");
                sb.AppendLine($"> {h.TranslatedText}");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(true), token);
        }

        public static async Task ExportReviewTemplate(string filePath, CancellationToken token = default)
        {
            var history = await LoadAllAsync(token);
            var sb = new StringBuilder();
            sb.AppendLine("请将以下会议 transcript 整理为中文学习复盘。");
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

            foreach (var h in history)
            {
                sb.AppendLine($"[{h.Timestamp}]");
                sb.AppendLine($"原文: {h.SourceText}");
                sb.AppendLine($"译文: {h.TranslatedText}");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(true), token);
        }

        // DEPRECATED
        private static async Task MigrateOldTimestampFormat()
        {
            var records = new List<(long id, string timestamp)>();
            using (var command = new SqliteCommand("SELECT Id, Timestamp FROM TranslationHistory", GetConnection()))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(reader.GetOrdinal("Id"));
                    string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                    records.Add((id, timestamp));
                }
            }

            foreach (var (id, timestamp) in records)
            {
                if (DateTime.TryParse(timestamp, out DateTime dt))
                {
                    long unixTime = ((DateTimeOffset)dt).ToUnixTimeSeconds();
                    using var updateCommand = new SqliteCommand(
                        "UPDATE TranslationHistory SET Timestamp = @Timestamp WHERE Id = @Id",
                        GetConnection());
                    updateCommand.Parameters.AddWithValue("@Id", id);
                    updateCommand.Parameters.AddWithValue("@Timestamp", unixTime.ToString());
                    await updateCommand.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
