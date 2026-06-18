using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SpanCoder.Contracts
{
    [JsonSerializable(typeof(CollabPayload))]
    [JsonSerializable(typeof(CrdtNodeState))]
    [JsonSerializable(typeof(CollabSyncState))]
    [JsonSerializable(typeof(CollabInsertMessage))]
    [JsonSerializable(typeof(CollabDeleteMessage))]
    [JsonSerializable(typeof(CollabCursorMessage))]
    [JsonSerializable(typeof(List<CrdtNodeState>))]
    public partial class CollabJsonContext : JsonSerializerContext
    {
    }
    public static class CollabMessageTypes
    {
        public const string Sync = "sync";
        public const string Insert = "insert";
        public const string Delete = "delete";
        public const string Cursor = "cursor";
    }

    public class CollabPayload
    {
        public string Type { get; set; } = "";
        public string Data { get; set; } = ""; // JSON-serialized payload corresponding to the Type
    }

    public class CrdtNodeState
    {
        public int[] Position { get; set; } = Array.Empty<int>();
        public char Value { get; set; }
        public string ClientId { get; set; } = "";
        public int Clock { get; set; }
        public bool IsDeleted { get; set; }
    }

    public class CollabSyncState
    {
        public List<CrdtNodeState> Nodes { get; set; } = new List<CrdtNodeState>();
    }

    public class CollabInsertMessage
    {
        public int[] Position { get; set; } = Array.Empty<int>();
        public char Value { get; set; }
        public string ClientId { get; set; } = "";
        public int Clock { get; set; }
    }

    public class CollabDeleteMessage
    {
        public int[] Position { get; set; } = Array.Empty<int>();
        public string ClientId { get; set; } = "";
        public int Clock { get; set; }
    }

    public class CollabCursorMessage
    {
        public string ClientId { get; set; } = "";
        public string Username { get; set; } = "";
        public int Line { get; set; }
        public int Character { get; set; }
        public int SelectionStartOffset { get; set; }
        public int SelectionEndOffset { get; set; }
        public string ColorHex { get; set; } = "#FF0000";
    }
}
