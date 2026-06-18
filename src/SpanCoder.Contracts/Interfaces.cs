using System;

namespace SpanCoder.Contracts
{
    public interface IDocumentView
    {
        int Id { get; }
        string FilePath { get; }
        int GetLineCount();
        ReadOnlySpan<char> GetLine(int lineIndex, out bool isContiguous, out char[]? rentedBuffer);
        long GetLineStart(int lineIndex);
        int Length { get; }
        string GetTextRange(int offset, int length);
    }

    public interface IEngineConnection
    {
        void Send(byte[] message);
        event Action<byte[]>? MessageReceived;
        IDocumentView? GetDocument(int documentId);
    }
}
