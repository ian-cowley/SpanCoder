using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public class AiAgentCoordinator
    {
        private static readonly HttpClient SharedHttpClient = new();
        private readonly LlmClient _llmClient;
        private readonly Action<byte[]> _sendResponse;
        private CancellationTokenSource? _activeCts;
        private readonly object _lock = new();

        public AiAgentCoordinator(Action<byte[]> sendResponse)
        {
            _llmClient = new LlmClient(SharedHttpClient);
            _sendResponse = sendResponse;
        }

        public void StartRequest(string requestJson)
        {
            lock (_lock)
            {
                // Abort any active loop
                _activeCts?.Cancel();
                _activeCts = new CancellationTokenSource();

                Task.Run(() => RunAgentLoopAsync(requestJson, _activeCts.Token));
            }
        }

        public void StopActiveRequest()
        {
            lock (_lock)
            {
                _activeCts?.Cancel();
                _activeCts = null;
                Console.WriteLine("[AiAgentCoordinator] Stop requested. Agent loop terminated.");
            }
        }

        private async Task RunAgentLoopAsync(string requestJson, CancellationToken token)
        {
            try
            {
                var request = JsonSerializer.Deserialize<AiChatRequest>(requestJson, ContractsJsonContext.Default.AiChatRequest);
                if (request == null)
                {
                    SendError("Error: Failed to deserialize AiChatRequest.");
                    return;
                }

                string apiKey = GetApiKeyForProvider(request.Provider);
                if (string.IsNullOrEmpty(apiKey) && !request.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    SendError($"Error: API Key is not configured for provider '{request.Provider}'. Go to Tools -> Options -> AI Settings to configure.");
                    return;
                }

                // 1. Build initial LLM Messages list
                var messages = new List<LlmRequestMessage>();

                // A. Add System Prompt
                messages.Add(new LlmRequestMessage
                {
                    Role = "system",
                    Content = GetSystemPrompt(request.YoloMode)
                });

                // B. Add Chat History
                foreach (var msg in request.History)
                {
                    var llmMsg = new LlmRequestMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content,
                        ToolCallId = msg.ToolCallId,
                        Name = msg.ToolName
                    };
                    messages.Add(llmMsg);
                }

                // C. Add User Request
                messages.Add(new LlmRequestMessage
                {
                    Role = "user",
                    Content = request.Prompt
                });

                int loopCount = 0;
                const int maxLoops = 15; // Safeguard against infinite loops

                while (!token.IsCancellationRequested && loopCount < maxLoops)
                {
                    loopCount++;
                    SendToolProgress("agent_loop", $"iteration_{loopCount}", "running", $"Starting agent loop iteration {loopCount}...");

                    // 2. Call LLM
                    var tools = AiToolsRegistry.GetToolsDefinition();
                    var response = await _llmClient.SendChatRequestAsync(
                        request.Provider,
                        request.Model,
                        apiKey,
                        messages,
                        tools
                    );

                    if (response == null || response.Choices.Count == 0)
                    {
                        SendError("Error: LLM returned an empty or failed response.");
                        return;
                    }

                    var choice = response.Choices[0];
                    var assistantMsg = choice.Message;

                    var assistantHistoryMsg = new LlmRequestMessage
                    {
                        Role = "assistant",
                        Content = string.IsNullOrEmpty(assistantMsg.Content) ? null : assistantMsg.Content,
                        ToolCalls = assistantMsg.ToolCalls
                    };
                    messages.Add(assistantHistoryMsg);

                    // 3. Handle assistant text response
                    if (!string.IsNullOrEmpty(assistantMsg.Content))
                    {
                        SendTextToken(assistantMsg.Content);
                    }

                    // 4. Handle assistant tool calls
                    if (assistantMsg.ToolCalls != null && assistantMsg.ToolCalls.Count > 0)
                    {
                        foreach (var toolCall in assistantMsg.ToolCalls)
                        {
                            if (token.IsCancellationRequested) break;

                            string toolName = toolCall.Function.Name;
                            string args = toolCall.Function.Arguments;

                            Console.WriteLine($"[AiAgent] Executing tool '{toolName}' with arguments: {args}");
                            SendToolProgress(toolCall.Id, toolName, "running", $"Executing {toolName}...");

                            // Run tool
                            string toolOutput = await AiToolsRegistry.ExecuteToolAsync(toolName, args);

                            SendToolProgress(toolCall.Id, toolName, "completed", toolOutput);

                            // Append tool output message to context
                            messages.Add(new LlmRequestMessage
                            {
                                Role = "tool",
                                Content = toolOutput,
                                ToolCallId = toolCall.Id,
                                Name = toolName
                            });
                        }
                    }
                    else
                    {
                        // No tool calls returned, agent is done
                        SendDone();
                        return;
                    }
                }

                if (loopCount >= maxLoops)
                {
                    SendError("Error: Maximum autonomous loop iteration limit (15) reached to prevent runaway usage.");
                }
            }
            catch (Exception ex)
            {
                SendError($"Error in agent loop: {ex.Message}");
            }
        }

        private string GetApiKeyForProvider(string provider)
        {
            // Read configured API Key from settings.json
            string settingsFile = GetSettingsFilePath();
            if (!File.Exists(settingsFile)) return "";

            try
            {
                string json = File.ReadAllText(settingsFile);
                using var doc = JsonDocument.Parse(json);
                string key = $"ai.{provider.ToLower()}.apikey";
                string encryptedKey = "";
                if (doc.RootElement.TryGetProperty(key, out var keyProp))
                {
                    encryptedKey = keyProp.GetString() ?? "";
                }
                else if (doc.RootElement.TryGetProperty("values", out var vals))
                {
                    if (vals.TryGetProperty(key, out var keyPropNested))
                    {
                        encryptedKey = keyPropNested.GetString() ?? "";
                    }
                }

                return DpapiHelper.Decrypt(encryptedKey);
            }
            catch { }

            return "";
        }

        private string GetSettingsFilePath()
        {
            if (IsRunningInUnitTest())
            {
                return Path.Combine(Path.GetTempPath(), "spancoder_test_settings.json");
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "SpanCoder", "settings.json");
        }

        private bool IsRunningInUnitTest()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName ?? "";
                if (name.Contains("test", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("nunit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private string GetSystemPrompt(bool yoloMode)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are SpanCoder AI, an elite autonomous software engineering agent embedded inside the developer's IDE.");
            sb.AppendLine("Your goal is to solve the developer's request by calling your registered tools.");
            sb.AppendLine("Guidelines:");
            sb.AppendLine("1. EXPLORE: If you do not know the codebase structure, call list_workspace_files to see the available files, and search_grep to locate code definitions.");
            sb.AppendLine("2. PRECISE EDITS: Never overwrite entire files if you are only modifying a few lines. Use edit_file_replace to target specific lines with exact indentation matches.");
            sb.AppendLine("3. COMPILE & VERIFY: Always verify your changes compile and pass tests by calling run_build_and_test. Never assume your edits compile. If errors occur, read the logs, edit files to fix them, and re-test.");
            sb.AppendLine("4. TERMINAL CAPABILITY: You have direct access to execute_terminal_command. Use it for database migrations, tool installations, git staging, or running scripts.");
            if (yoloMode)
            {
                sb.AppendLine("5. YOLO DEV MODE: You are authorized to run terminal commands and modify files completely autonomously. Move fast and fix things yourself.");
            }
            else
            {
                sb.AppendLine("5. CONFIRMATION: The user expects updates on your steps, but you should run your plans autonomously. For high-risk actions, explain them briefly first.");
            }
            return sb.ToString();
        }

        #region Helpers to send Socket TCP Packets

        private void SendTextToken(string token)
        {
            var res = new AiChatResponse { TextToken = token };
            SendResponsePacket(MessageTypes.AiChatResponse, res);
        }

        private void SendError(string error)
        {
            var res = new AiChatResponse { Error = error, IsDone = true };
            SendResponsePacket(MessageTypes.AiChatResponse, res);
        }

        private void SendDone()
        {
            var res = new AiChatResponse { IsDone = true };
            SendResponsePacket(MessageTypes.AiChatResponse, res);
        }

        private void SendToolProgress(string callId, string name, string status, string output)
        {
            var ev = new AiToolExecutionEvent
            {
                ToolName = name,
                Arguments = callId, // Using args field to map tool call ID
                Status = status,
                Output = output
            };
            SendResponsePacket(MessageTypes.AiToolExecutionEvent, ev);
        }

        private void SendResponsePacket<T>(byte type, T payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload, typeof(T), ContractsJsonContext.Default);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + json.Length * sizeof(char)];
                int len = BinaryMessageSerializer.WriteStringPayload(buffer, type, json);
                
                byte[] finalBuffer = new byte[len];
                Array.Copy(buffer, 0, finalBuffer, 0, len);
                _sendResponse(finalBuffer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AiAgentCoordinator] Error sending response packet: {ex.Message}");
            }
        }

        #endregion
    }

    #region Context for Contracts JSON (Native AOT Safe)
    [JsonSerializable(typeof(AiChatRequest))]
    [JsonSerializable(typeof(AiChatResponse))]
    [JsonSerializable(typeof(AiToolExecutionEvent))]
    [JsonSerializable(typeof(AiMessage))]
    [JsonSerializable(typeof(List<AiMessage>))]
    public partial class ContractsJsonContext : JsonSerializerContext
    {
    }
    #endregion
}
