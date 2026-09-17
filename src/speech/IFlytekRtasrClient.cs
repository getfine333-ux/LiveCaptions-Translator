using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.speech
{
    // Client for iFlytek Real-time ASR (实时语音转写, RTASR).
    // Protocol: wss://rtasr.xfyun.cn/v1/ws
    //   signa = Base64(HMAC-SHA1(key = apiKey, data = MD5hex(appid + ts)))
    //   audio : 16kHz / 16bit / mono PCM, 1280 bytes per 40ms
    //   end   : binary message {"end": true}
    public class IFlytekRtasrClient : IAsrClient
    {
        private const string Endpoint = "wss://rtasr.xfyun.cn/v1/ws";

        private readonly string _appId;
        private readonly string _apiKey;
        private readonly string _lang;

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        // (text, isFinal)
        public event Action<string, bool>? OnResult;
        public event Action<string>? OnError;
        public event Action? OnClosed;

        public bool IsOpen => _ws?.State == WebSocketState.Open;

        public IFlytekRtasrClient(string appId, string apiKey, string lang = "cn")
        {
            _appId = appId;
            _apiKey = apiKey;
            _lang = string.IsNullOrWhiteSpace(lang) ? "cn" : lang;
        }

        public static string BuildSigna(string appId, string apiKey, string ts)
        {
            using var md5 = MD5.Create();
            byte[] md5Bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(appId + ts));
            string md5Hex = Convert.ToHexString(md5Bytes).ToLowerInvariant();

            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(apiKey));
            byte[] signaBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(md5Hex));
            return Convert.ToBase64String(signaBytes);
        }

        public async Task ConnectAsync(CancellationToken token = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(IFlytekRtasrClient));

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            string signa = BuildSigna(_appId, _apiKey, ts);

            string url = $"{Endpoint}?appid={Uri.EscapeDataString(_appId)}" +
                         $"&ts={Uri.EscapeDataString(ts)}" +
                         $"&signa={Uri.EscapeDataString(signa)}" +
                         $"&lang={Uri.EscapeDataString(_lang)}";

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
            byte[] end = Encoding.UTF8.GetBytes("{\"end\": true}");
            await _ws.SendAsync(new ArraySegment<byte>(end), WebSocketMessageType.Binary, true, token);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            var buffer = new byte[32 * 1024];
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

                string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

                if (action == "error")
                {
                    string desc = root.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
                    string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    OnError?.Invoke($"iFlytek error {code}: {desc}");
                    return;
                }

                if (action != "result")
                    return;

                string dataStr = root.TryGetProperty("data", out var dataProp) ? dataProp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(dataStr))
                    return;

                using var dataDoc = JsonDocument.Parse(dataStr);
                var data = dataDoc.RootElement;

                bool isFinal = false;
                var textBuilder = new StringBuilder();

                // data = { "<lang>": { "st": { "type": "0|1", "rt": [ { "ws": [ { "cw": [ { "w": "字" } ] } ] } ] } }, "seg_id": N }
                foreach (var langProp in data.EnumerateObject())
                {
                    if (langProp.NameEquals("seg_id") || langProp.Value.ValueKind != JsonValueKind.Object)
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

                    if (!st.TryGetProperty("rt", out var rt) || rt.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var seg in rt.EnumerateArray())
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
