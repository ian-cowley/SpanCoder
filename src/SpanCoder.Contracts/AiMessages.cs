using System.Collections.Generic;

namespace SpanCoder.Contracts
{
    public class AiChatRequest
    {
        public string Prompt { get; set; } = "";
        public string Provider { get; set; } = ""; // "OpenAI", "Gemini", "Ollama"
        public string Model { get; set; } = "";
        public bool YoloMode { get; set; } // Auto-approve terminal execution without confirmation
        public List<AiMessage> History { get; set; } = new();
    }

    public class AiMessage
    {
        public string Role { get; set; } = ""; // "user", "assistant", "system", "tool"
        public string Content { get; set; } = "";
        public string? ToolCallId { get; set; }
        public string? ToolName { get; set; }
    }

    public class AiChatResponse
    {
        public string? TextToken { get; set; }
        public bool IsDone { get; set; }
        public string? Error { get; set; }
    }

    public class AiToolExecutionEvent
    {
        public string ToolName { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Status { get; set; } = ""; // "running", "completed", "failed"
        public string Output { get; set; } = "";
    }
}
