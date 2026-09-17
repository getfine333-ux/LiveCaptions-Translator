// Modified derivative; see CHANGES.md. Original upstream attribution is retained in NOTICE.
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `SettingPage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static string Prompt => Translator.Setting.Prompt;

        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        private static int openai_fallback_index = 0;

        public static async Task<string> OpenAI(string text, CancellationToken token = default) => (await OpenAIRequest(text, null, token)).Content;
        public static async Task<string> OpenAI(TranslationSegment segment, CancellationToken token) => (await OpenAIReply(segment, token)).Content;
        public static Task<ProviderReply> OpenAIReply(TranslationSegment segment, CancellationToken token) => OpenAIRequest(segment.TranslationSource ?? segment.Text, segment, token);
        private static async Task<ProviderReply> OpenAIRequest(string text, TranslationSegment? segment, CancellationToken token)
        {
            var config = Translator.Setting["OpenAI"] as OpenAIConfig;
            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                segment?.TargetLang ?? Translator.Setting.TargetLanguage, out var langValue) ? langValue : (segment?.TargetLang ?? Translator.Setting.TargetLanguage);

            bool correct = Translator.Setting.CorrectAsrErrors &&
                (segment?.SourceLang ?? Translator.Setting.SourceLang).StartsWith("zh") &&
                (segment?.TargetLang ?? Translator.Setting.TargetLanguage).StartsWith("en");
            string systemPrompt = correct
                ? (Translator.Setting.CorrectionPrompt ?? "")
                : string.Format(Prompt, language);
            if (correct) systemPrompt += TranslationResponse.ScopeInstruction + TranslationResponse.CorrectionInstruction;
            if (correct && !string.IsNullOrWhiteSpace(segment?.TerminologyHints))
                systemPrompt += "\n\n本段有领域依据的术语（中文与英文应保持一致）：\n" + segment.TerminologyHints;

            // Inject the user's glossary so terminology is translated consistently.
            string glossary = segment?.RequestGlossary ?? Translator.GetGlossary();
            if (!string.IsNullOrWhiteSpace(glossary))
                systemPrompt += "\n\n术语表（必须优先采用以下译法，英文术语保持原样）：\n" + glossary;

            string userContent;
            if (correct)
            {
                string context = segment?.Context ?? Translator.Caption?.AwareContextsCaption ?? "";
                userContent = TranslationResponse.CorrectionInput(text, context);
            }
            else
            {
                userContent = $"🔤 {text} 🔤";
            }

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = systemPrompt },
                new BaseLLMConfig.Message { role = "user", content = userContent }
            };

            if (segment == null && Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            HttpResponseMessage response;
            int fallbackIndex = Volatile.Read(ref openai_fallback_index);
            try
            {
                while (true)
                {
                    var requestData = LLMRequestDataFactory.Create(fallbackIndex,
                        config.ModelName, messages, config.Temperature);
                    string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                    using var request = new HttpRequestMessage(HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl));
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TextUtil.ResolveSecret(config.ApiKey));
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    response = await client.SendAsync(request, token);
                    if (response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        break;
                    await Task.Delay(15, token);

                    fallbackIndex++;
                    if (fallbackIndex >= LLMRequestDataFactory.FallbackCount)
                    {
                        Volatile.Write(ref openai_fallback_index, 0);
                        break;
                    }
                    response.Dispose();
                }
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 10 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            using var ownedResponse = response;
            Volatile.Write(ref openai_fallback_index, fallbackIndex < LLMRequestDataFactory.FallbackCount ? fallbackIndex : 0);
            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync(token);
                var reply = ProviderReply.FromChatCompletion(responseString);
                return reply with { Content = RegexPatterns.ModelThinking().Replace(reply.Content, "") };
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Ollama"] as OllamaConfig;
            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }
            
            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                var output = responseObj.message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> LMStudio(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LMStudio"] as LMStudioConfig;
            string language = LMStudioConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl) + "/chat";

            string systemPrompt = string.Format(Prompt, language);

            // Build input with optional context
            string input = $"🔤 {text} 🔤";
            if (Translator.Setting.ContextAware)
            {
                var contextLines = new List<string>();
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");
                    contextLines.Add($"🔤 {entry.SourceText} 🔤 → {translatedText}");
                }
                if (contextLines.Count > 0)
                    input = string.Join("\n", contextLines) + "\n" + input;
            }

            var requestData = new
            {
                model = config.ModelName,
                system_prompt = systemPrompt,
                input = input,
                temperature = config.Temperature
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // LMStudio native /api/v1/chat response:
                // { "output": [ { "type": "message", "content": "..." }, ... ] }
                if (root.TryGetProperty("output", out var outputArray) &&
                    outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentProp))
                        {
                            return RegexPatterns.ModelThinking().Replace(contentProp.GetString() ?? "", "");
                        }
                    }
                }

                return "[ERROR] Translation Failed: Unexpected response format";
            }
            else
            {
                string body = await response.Content.ReadAsStringAsync();
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}: {body}";
            }
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenRouter"] as OpenRouterConfig;
            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (Translator.Setting.ContextAware)
            {
                foreach (var entry in Translator.Caption.AwareContexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (translatedText.Contains("[ERROR]") || translatedText.Contains("[WARNING]"))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(1, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                }
            }

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = Translator.Setting?.TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(url, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);

                string translatedText = responseObj[0][0];
                return translatedText;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        // Modified fork: preserve saved Google2 selections without redistributing
        // the upstream browser-extension credential or impersonating its client.
        public static Task<string> Google2(string text, CancellationToken token = default) => Google(text, token);
        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["DeepL"] as DeepLConfig;
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString();
                }
                return "[ERROR] Translation Failed: No valid feedback";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Youdao"] as YoudaoConfig;
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppKey}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);

                if (responseObj.errorCode != "0")
                    return $"[ERROR] Translation Failed: Youdao Error - {responseObj.errorCode}";

                return responseObj.translation?.FirstOrDefault() ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["MTranServer"] as MTranServerConfig;
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return responseObj.result;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Baidu"] as BaiduConfig;
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppId}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(config.ApiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    return $"[ERROR] Translation Failed: Baidu Error - {responseObj.error_code}";

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LibreTranslate"] as LibreTranslateConfig;
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                Translator.Setting.TargetLanguage, out var langValue) ? langValue : Translator.Setting.TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config?.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            client.DefaultRequestHeaders.Clear();

            HttpResponseMessage response;
            try
            {
                response = await client.PostAsync(apiUrl, content, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return responseObj.translatedText;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString();
                reader.Read();

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, configType, options);
                        else
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, typeof(TranslateAPIConfig), options);

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            JsonSerializer.Serialize(writer, config, configType, options);
                        else
                            JsonSerializer.Serialize(writer, config, typeof(TranslateAPIConfig), options);
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }
    }
}
