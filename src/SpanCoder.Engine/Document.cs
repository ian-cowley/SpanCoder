using System;
using System.Buffers;
using SpanCoder.Contracts;

namespace SpanCoder.Engine
{
    public class Document : IDocumentView, IDisposable
    {
        public int Id { get; }
        public string FilePath { get; set; }
        public PieceTable PieceTable { get; private set; }
        public LineIndex LineIndex { get; private set; }

        public int Length => PieceTable?.Length ?? 0;

        public Document(int id, ReadOnlyMemory<char> initialText, string filePath = "")
        {
            Id = id;
            FilePath = filePath;
            PieceTable = new PieceTable(initialText);
            LineIndex = new LineIndex();
            LineIndex.Initialize(initialText.Span);
        }

        public void Insert(int offset, ReadOnlySpan<char> text)
        {
            if (offset < 0 || offset > PieceTable.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Must update line index BEFORE piece table is modified so the offset calculations are relative to current offsets
            LineIndex.Insert(offset, text.Length, text);
            PieceTable.Insert(offset, text);
        }

        public void Delete(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > PieceTable.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            // Must update line index BEFORE piece table is modified so the offsets are mapped correctly
            LineIndex.Delete(offset, length);
            PieceTable.Delete(offset, length);
        }

        public int GetLineCount() => LineIndex.Count;

        public long GetLineStart(int lineIndex) => LineIndex.GetLineStart(lineIndex);

        public ReadOnlySpan<char> GetLine(int lineIndex, out bool isContiguous, out char[]? rentedBuffer)
        {
            if (lineIndex < 0 || lineIndex >= LineIndex.Count)
                throw new ArgumentOutOfRangeException(nameof(lineIndex));

            long start = LineIndex.GetLineStart(lineIndex);
            long end = (lineIndex + 1 < LineIndex.Count) ? LineIndex.GetLineStart(lineIndex + 1) : PieceTable.Length;
            int length = (int)(end - start);

            if (length <= 0)
            {
                isContiguous = true;
                rentedBuffer = null;
                return ReadOnlySpan<char>.Empty;
            }

            var span = PieceTable.GetContiguousSpan((int)start, length, out isContiguous);
            if (isContiguous)
            {
                rentedBuffer = null;
                return span;
            }

            // Fallback: Copy to rented buffer
            rentedBuffer = ArrayPool<char>.Shared.Rent(length);
            PieceTable.GetText((int)start, length, rentedBuffer.AsSpan(0, length));
            return rentedBuffer.AsSpan(0, length);
        }

        public void Dispose()
        {
            PieceTable?.Dispose();
            PieceTable = null!;
            LineIndex = null!;
        }
    }
}
