using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.speech
{
    // Client for iFlytek "中英识别大模型" / "中文识别大模型" (spark_zh_iat).
    // Endpoint: wss://iat.xf-yun.com/v1
    // Auth    : authorization = base64('api_key="..",algorithm="hmac-sha256",
    //             headers="host date request-line",signature="<base64(hmacsha256(signature_origin, apiSecret))>"')
    // Frames  : JSON text frames carrying base64 PCM audio (16kHz/16bit/mono).
    // Limit   : a single session is capped at 60s, so we rotate before that.
    public class IFlytekIatLlmClient : IAsrClient
    {
        private const string Host = "iat.xf-yun.com";
        private const string Path = "/v1";
        private const int MaxSessionMs = 55000;   // rotate before the 60s cap
        private static readonly char[] SentencePunc = { '。', '？', '！', '?', '!', ';', '；' };

        private readonly string _appId;
        private readonly string _apiKey;
        private readonly string _apiSecret;

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        private int _seq;
        private bool _firstFrame = true;
        private int _audioMs;
        private bool _ending;

        // Accumulated recognition text for the current session.
        private readonly StringBuilder _sessionText = new();
        private int _emittedIndex;

        public event Action<string, bool>? OnResult;
        public event Action<string>? OnError;
        public event Action? OnClosed;

        public bool IsOpen => _ws?.State == WebSocketState.Open;

        public IFlytekIatLlmClient(string appId, string apiKey, string apiSecret)
        {
            _appId = appId;
            _apiKey = apiKey;
            _apiSecret = apiSecret;
        }

        public static string BuildAuthorization(string apiKey, string apiSecret, string date)
        {
            string signatureOrigin = $"host: {Host}\ndate: {date}\nGET {Path} HTTP/1.1";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
            byte[] sha = hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureOrigin));
            string signature = Convert.ToBase64String(sha);

            string authOrigin = $"api_key=\"{apiKey}\",algorithm=\"hmac-sha256\"," +
                                $"headers=\"host date request-line\",signature=\"{signature}\"";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(authOrigin));
        }

        public static string BuildDate(DateTime utcNow) =>
            utcNow.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture);

        public async Task ConnectAsync(CancellationToken token = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IFlytekIatLlmClient));

            _seq = 0;
            _firstFrame = true;
            _audioMs = 0;
            _ending = false;
            _sessionText.Clear();
            _emittedIndex = 0;

            string date = BuildDate(DateTime.UtcNow);
            string authorization = BuildAuthorization(_apiKey, _apiSecret, date);
            string url = $"wss://{Host}{Path}?authorization={Uri.EscapeDataString(authorization)}" +
                         $"&date={Uri.EscapeDataString(date)}&host={Uri.EscapeDataString(Host)}";

            _ws = new ClientWebSocket();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public async Task SendAudioAsync(byte[] data, CancellationToken token = default)
        {
            if (_ws?.State != WebSocketState.Open || _ending)
                return;

            _seq++;
            string audioB64 = Convert.ToBase64String(data);
            int status = _firstFrame ? 0 : 1;

            object frame;
            if (_firstFrame)
            {
                frame = new
                {
                    header = new { app_id = _appId, status = 0 },
                    parameter = new
                    {
                        iat = new
                        {
                            domain = "slm",
                            language = "zh_cn",
                            accent = "mandarin",
                            eos = 6000,
                            result = new { encoding = "utf8", compress = "raw", format = "json" }
                        }
                    },
                    payload = new
                    {
                        audio = new
                        {
                            encoding = "raw",
                            sample_rate = 16000,
                            channels = 1,
                            bit_depth = 16,
                            seq = _seq,
                            status = 0,
                            audio = audioB64
                        }
                    }
                };
                _firstFrame = false;
            }
            else
            {
                frame = new
                {
                    header = new { app_id = _appId, status = 1 },
                    payload = new
                    {
                        audio = new
                        {
                            encoding = "raw",
                            sample_rate = 16000,
                            channels = 1,
                            bit_depth = 16,
                            seq = _seq,
                            status = 1,
                            audio = audioB64
                        }
                    }
                };
            }

            string json = JsonSerializer.Serialize(frame);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);

            _audioMs += 40;
            if (_audioMs >= MaxSessionMs)
                await SendEndAsync(token);
        }

        public async Task SendEndAsync(CancellationToken token = default)
        {
            if (_ws?.State != WebSocketState.Open || _ending)
                return;
            _ending = true;

            _seq++;
            var frame = new
            {
                header = new { app_id = _appId, status = 2 },
                payload = new
                {
                    audio = new
                    {
                        encoding = "raw",
                        sample_rate = 16000,
                        channels = 1,
                        bit_depth = 16,
                        seq = _seq,
                        status = 2,
                        audio = ""
                    }
                }
            };
            string json = JsonSerializer.Serialize(frame);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            catch { }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_ws != null && _ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            FlushRemaining();
                            OnClosed?.Invoke();
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    HandleMessage(sb.ToString());
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"websocket receive error: {ex.Message}");
            }
            finally
            {
                FlushRemaining();
                OnClosed?.Invoke();
            }
        }

        private void HandleMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("header", out var header))
                {
                    if (header.TryGetProperty("code", out var code) &&
                        code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
                    {
                        string msg = header.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        OnError?.Invoke($"iFlytek IAT error {code.GetInt32()}: {msg}");
                        return;
                    }
                }

                if (!root.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("result", out var result) ||
                    !result.TryGetProperty("text", out var textProp))
                    return;

                string b64 = textProp.GetString() ?? "";
                if (string.IsNullOrEmpty(b64))
                    return;

                byte[] decoded = Convert.FromBase64String(b64);
                string textJson = Encoding.UTF8.GetString(decoded);

                using var textDoc = JsonDocument.Parse(textJson);
                var textRoot = textDoc.RootElement;

                if (!textRoot.TryGetProperty("ws", out var wsArr) || wsArr.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var ws in wsArr.EnumerateArray())
                {
                    if (!ws.TryGetProperty("cw", out var cwArr) || cwArr.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var cw in cwArr.EnumerateArray())
                    {
                        if (cw.TryGetProperty("w", out var w))
                            _sessionText.Append(w.GetString());
                    }
                }

                EmitSentences();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"parse error: {ex.Message}");
            }
        }

        // Emit complete sentences as final, keep the trailing partial as intermediate.
        private void EmitSentences()
        {
            string full = _sessionText.ToString();
            int lastPunc = full.LastIndexOfAny(SentencePunc);
            if (lastPunc >= _emittedIndex)
            {
                string sentences = full.Substring(_emittedIndex, lastPunc - _emittedIndex + 1).Trim();
                _emittedIndex = lastPunc + 1;
                if (!string.IsNullOrEmpty(sentences))
                    OnResult?.Invoke(sentences, true);
            }

            if (_emittedIndex < full.Length)
            {
                string partial = full.Substring(_emittedIndex).Trim();
                if (!string.IsNullOrEmpty(partial))
                    OnResult?.Invoke(partial, false);
            }
        }

        // On session end, flush whatever is left as a final result.
        private void FlushRemaining()
        {
            try
            {
                string full = _sessionText.ToString();
                if (_emittedIndex < full.Length)
                {
                    string rest = full.Substring(_emittedIndex).Trim();
                    _emittedIndex = full.Length;
                    if (!string.IsNullOrEmpty(rest))
                        OnResult?.Invoke(rest, true);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_ws?.State == WebSocketState.Open)
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                       .Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            try { _ws?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _ws = null;
            _cts = null;
        }
    }
}
