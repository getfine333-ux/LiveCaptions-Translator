using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.speech
{
    // Client for iFlytek "实时语音转写大模型" (ASR LLM).
    // Endpoint: wss://office-api-ast-dx.iflyaisol.com/ast/communicate/v1
    // Auth: signature = Base64(HMAC-SHA1(key = accessKeySecret, data = sortedBaseString))
    //   where sortedBaseString = URL-encoded "k=v" pairs (excluding signature), sorted by key.
    // Audio: 16kHz / 16bit / mono PCM, 1280 bytes per 40ms.
    // End:   {"end": true, "sessionId": "<uuid>"}
    public class IFlytekAsrLlmClient : IAsrClient
    {
        private const string Endpoint = "wss://office-api-ast-dx.iflyaisol.com/ast/communicate/v1";

        private readonly string _appId;
        private readonly string _accessKeyId;     // console "APIKey"
        private readonly string _accessKeySecret; // console "APISecret"
        private readonly string _lang;            // autodialect | autominor

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private string _sessionId = Guid.NewGuid().ToString();

        public event Action<string, bool>? OnResult;
        public event Action<string>? OnError;
        public event Action? OnClosed;

        public bool IsOpen => _ws?.State == WebSocketState.Open;

        public IFlytekAsrLlmClient(string appId, string accessKeyId, string accessKeySecret, string lang = "autodialect")
        {
            _appId = appId;
            _accessKeyId = accessKeyId;
            _accessKeySecret = accessKeySecret;
            _lang = string.IsNullOrWhiteSpace(lang) ? "autodialect" : lang;
        }

        public static string BuildUtc(DateTimeOffset now)
        {
            string offset = (now.Offset < TimeSpan.Zero ? "-" : "+") +
                            now.Offset.Duration().ToString(@"hhmm");
            return now.ToString("yyyy-MM-ddTHH:mm:ss") + offset;
        }

        public static string BuildSignature(string accessKeySecret, string baseString)
        {
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret));
            byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString));
            return Convert.ToBase64String(sig);
        }

        private static string BuildBaseString(SortedDictionary<string, string> parameters)
        {
            var sb = new StringBuilder();
            foreach (var kv in parameters)
            {
                if (sb.Length > 0)
                    sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value));
            }
            return sb.ToString();
        }

        public async Task ConnectAsync(CancellationToken token = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IFlytekAsrLlmClient));

            _sessionId = Guid.NewGuid().ToString();

            var parameters = new SortedDictionary<string, string>
            {
                ["accessKeyId"] = _accessKeyId,
                ["appId"] = _appId,
                ["audio_encode"] = "pcm_s16le",
                ["lang"] = _lang,
                ["samplerate"] = "16000",
                ["utc"] = BuildUtc(DateTimeOffset.Now),
                ["uuid"] = _sessionId,
            };

            string baseString = BuildBaseString(parameters);
            string signature = BuildSignature(_accessKeySecret, baseString);
            string url = $"{Endpoint}?{baseString}&signature={Uri.EscapeDataString(signature)}";

            _ws = new ClientWebSocket();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            await _ws.ConnectAsync(new Uri(url), _cts.Token);

            _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }

        public async Task SendAudioAsync(byte[] data, CancellationToken token = default)
        {
            if (_ws?.State != WebSocketState.Open)
                return;
            await _ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, token);
        }

        public async Task SendEndAsync(CancellationToken token = default)
        {
            if (_ws?.State != WebSocketState.Open)
                return;
            byte[] end = Encoding.UTF8.GetBytes($"{{\"end\": true, \"sessionId\": \"{_sessionId}\"}}");
            await _ws.SendAsync(new ArraySegment<byte>(end), WebSocketMessageType.Binary, true, token);
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
                OnClosed?.Invoke();
            }
        }

        private void HandleMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                string resType = root.TryGetProperty("res_type", out var rt) ? rt.GetString() ?? "" : "";

                // Abnormal frame
                if (resType == "frc")
                {
                    string desc = "";
                    if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                        d.TryGetProperty("desc", out var descProp))
                        desc = descProp.GetString() ?? "";
                    OnError?.Invoke($"iFlytek ASR LLM error: {desc}");
                    return;
                }

                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return;

                bool isFinal = false;
                bool isLast = false;
                var textBuilder = new StringBuilder();

                if (data.TryGetProperty("ls", out var ls) &&
                    (ls.ValueKind == JsonValueKind.True || ls.ValueKind == JsonValueKind.False))
                    isLast = ls.GetBoolean();

                // data.<lang>.st.rt[].ws[].cw[].w
                foreach (var langProp in data.EnumerateObject())
                {
                    if (langProp.NameEquals("seg_id") || langProp.NameEquals("ls") ||
                        langProp.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!langProp.Value.TryGetProperty("st", out var st))
                        continue;

                    if (st.TryGetProperty("type", out var typeProp))
                    {
                        if (typeProp.ValueKind == JsonValueKind.Number)
                            isFinal = typeProp.GetInt32() == 0;
                        else if (typeProp.ValueKind == JsonValueKind.String)
                            isFinal = typeProp.GetString() == "0";
                    }

                    if (!st.TryGetProperty("rt", out var rtArr) || rtArr.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var seg in rtArr.EnumerateArray())
                    {
                        if (!seg.TryGetProperty("ws", out var ws) || ws.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var word in ws.EnumerateArray())
                        {
                            if (!word.TryGetProperty("cw", out var cw) || cw.ValueKind != JsonValueKind.Array)
                                continue;
                            foreach (var ch in cw.EnumerateArray())
                            {
                                if (ch.TryGetProperty("w", out var w))
                                    textBuilder.Append(w.GetString());
                            }
                        }
                    }
                }

                string text = textBuilder.ToString().Trim();
                if (!string.IsNullOrEmpty(text))
                    OnResult?.Invoke(text, isFinal);

                if (isLast)
                    OnClosed?.Invoke();
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"parse error: {ex.Message}");
            }
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
