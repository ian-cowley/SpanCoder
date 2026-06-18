using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SpanCoder.Engine
{
    #region Data Contracts
    
    public class LlmRequestMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";
        
        [JsonPropertyName("content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; }
        
        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
        
        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolCallId { get; set; }
        
        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LlmToolCall>? ToolCalls { get; set; }
    }

    public class LlmToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
        
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";
        
        [JsonPropertyName("function")]
        public LlmFunctionCall Function { get; set; } = new();

        [JsonPropertyName("extra_content")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LlmToolCallExtraContent? ExtraContent { get; set; }
    }

    public class LlmToolCallExtraContent
    {
        [JsonPropertyName("google")]
        public LlmGoogleExtraContent Google { get; set; } = new();
    }

    public class LlmGoogleExtraContent
    {
        [JsonPropertyName("thought_signature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ThoughtSignature { get; set; }
    }

    public class LlmFunctionCall
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = ""; // JSON string
    }

    public class LlmTool
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";
        
        [JsonPropertyName("function")]
        public LlmFunctionDefinition Function { get; set; } = new();
    }

    public class LlmFunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; set; } // Raw JSON schema parameters
    }

    public class LlmRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";
        
        [JsonPropertyName("messages")]
        public List<LlmRequestMessage> Messages { get; set; } = new();
        
        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<LlmTool>? Tools { get; set; }
        
        [JsonPropertyName("tool_choice")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ToolChoice { get; set; }
    }

    public class LlmResponse
    {
        [JsonPropertyName("choices")]
        public List<LlmChoice> Choices { get; set; } = new();
    }

    public class LlmChoice
    {
        [JsonPropertyName("message")]
        public LlmResponseMessage Message { get; set; } = new();
        
        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; } = "";
    }

    public class LlmResponseMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";
        
        [JsonPropertyName("content")]
        public string? Content { get; set; }
        
        [JsonPropertyName("tool_calls")]
        public List<LlmToolCall>? ToolCalls { get; set; }
    }

    #endregion

    #region JSON Context (Native AOT Safe)

    [JsonSerializable(typeof(LlmRequest))]
    [JsonSerializable(typeof(LlmResponse))]
    [JsonSerializable(typeof(LlmTool))]
    [JsonSerializable(typeof(LlmRequestMessage))]
    [JsonSerializable(typeof(LlmToolCall))]
    [JsonSerializable(typeof(LlmToolCallExtraContent))]
    [JsonSerializable(typeof(LlmGoogleExtraContent))]
    internal partial class LlmJsonContext : JsonSerializerContext
    {
    }

    #endregion

    public class LlmClient
    {
        private readonly HttpClient _httpClient;

        public LlmClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<LlmResponse?> SendChatRequestAsync(
            string provider, 
            string model, 
            string apiKey, 
            List<LlmRequestMessage> messages, 
            List<LlmTool>? tools = null)
        {
            string url;
            string? bearerToken = null;

            if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://api.openai.com/v1/chat/completions";
                bearerToken = apiKey;
            }
            else if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                // Gemini OpenAI compatibility endpoint
                url = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
                bearerToken = apiKey;
            }
            else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                // Ollama local endpoint
                url = "http://localhost:11434/v1/chat/completions";
            }
            else
            {
                throw new ArgumentException($"Unsupported LLM provider: {provider}");
            }

            var requestPayload = new LlmRequest
            {
                Model = model,
                Messages = messages,
                Tools = tools,
                ToolChoice = tools != null && tools.Count > 0 ? "auto" : null
            };

            string requestJson = JsonSerializer.Serialize(requestPayload, LlmJsonContext.Default.LlmRequest);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            if (!string.IsNullOrEmpty(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            var response = await _httpClient.SendAsync(request);
            string responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"API Error ({response.StatusCode}): {responseJson}");
            }

            var deserialized = JsonSerializer.Deserialize(responseJson, LlmJsonContext.Default.LlmResponse);
            if (deserialized == null)
            {
                throw new Exception("Failed to deserialize LLM response JSON.");
            }
            return deserialized;
        }
    }
}
