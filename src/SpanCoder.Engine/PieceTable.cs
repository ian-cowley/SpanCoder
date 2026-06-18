using System;
using System.Buffers;
using System.Collections.Generic;

namespace SpanCoder.Engine
{
    public enum PieceSource : byte
    {
        Original = 0,
        Added = 1
    }

    public readonly struct Piece
    {
        public readonly PieceSource Source;
        public readonly int Start;
        public readonly int Length;

        public Piece(PieceSource source, int start, int length)
        {
            Source = source;
            Start = start;
            Length = length;
        }

        public override string ToString() => $"{Source}: {Start}..{Start + Length}";
    }

    public class PieceTable : IDisposable
    {
        private ReadOnlyMemory<char> _originalText;
        private char[] _addedText;
        private int _addedLength;
        private readonly List<Piece> _pieces;
        private int _totalLength;

        public int Length => _totalLength;
        public List<Piece> Pieces => _pieces;

        public PieceTable(ReadOnlyMemory<char> originalText)
        {
            _originalText = originalText;
            _addedText = ArrayPool<char>.Shared.Rent(4096);
            _addedLength = 0;
            _pieces = new List<Piece>();

            if (_originalText.Length > 0)
            {
                _pieces.Add(new Piece(PieceSource.Original, 0, _originalText.Length));
                _totalLength = _originalText.Length;
            }
            else
            {
                _totalLength = 0;
            }
        }

        public void Insert(int position, ReadOnlySpan<char> text)
        {
            if (position < 0 || position > _totalLength)
                throw new ArgumentOutOfRangeException(nameof(position));

            if (text.IsEmpty)
                return;

            // 1. Write the new text into the added buffer
            EnsureAddedCapacity(text.Length);
            int addedStart = _addedLength;
            text.CopyTo(_addedText.AsSpan(_addedLength, text.Length));
            _addedLength += text.Length;

            var newPiece = new Piece(PieceSource.Added, addedStart, text.Length);

            // 2. Locate and split the existing piece at the insertion point
            if (_pieces.Count == 0)
            {
                _pieces.Add(newPiece);
            }
            else if (position == 0)
            {
                _pieces.Insert(0, newPiece);
            }
            else if (position == _totalLength)
            {
                _pieces.Add(newPiece);
            }
            else
            {
                int currentOffset = 0;
                for (int i = 0; i < _pieces.Count; i++)
                {
                    var piece = _pieces[i];
                    if (position >= currentOffset && position < currentOffset + piece.Length)
                    {
                        int relativeOffset = position - currentOffset;
                        if (relativeOffset == 0)
                        {
                            _pieces.Insert(i, newPiece);
                        }
                        else
                        {
                            // Split the piece
                            var left = new Piece(piece.Source, piece.Start, relativeOffset);
                            var right = new Piece(piece.Source, piece.Start + relativeOffset, piece.Length - relativeOffset);

                            _pieces[i] = left;
                            _pieces.Insert(i + 1, newPiece);
                            _pieces.Insert(i + 2, right);
                        }
                        break;
                    }
                    currentOffset += piece.Length;
                }
            }

            _totalLength += text.Length;
        }

        public void Delete(int position, int length)
        {
            if (position < 0 || length < 0 || position + length > _totalLength)
                throw new ArgumentOutOfRangeException(nameof(position));

            if (length == 0)
                return;

            int currentOffset = 0;
            int remainingToDelete = length;

            for (int i = 0; i < _pieces.Count && remainingToDelete > 0; )
            {
                var piece = _pieces[i];
                int pieceEnd = currentOffset + piece.Length;

                if (position < pieceEnd && position + remainingToDelete > currentOffset)
                {
                    // Overlaps with deleted range
                    int deleteStartInPiece = Math.Max(0, position - currentOffset);
                    int deleteLenInPiece = Math.Min(piece.Length - deleteStartInPiece, remainingToDelete);

                    if (deleteStartInPiece == 0 && deleteLenInPiece == piece.Length)
                    {
                        // Delete the entire piece
                        _pieces.RemoveAt(i);
                        remainingToDelete -= deleteLenInPiece;
                        // Don't increment i, next piece shifts left
                        continue;
                    }
                    else if (deleteStartInPiece == 0)
                    {
                        // Trim the start of the piece
                        _pieces[i] = new Piece(piece.Source, piece.Start + deleteLenInPiece, piece.Length - deleteLenInPiece);
                        remainingToDelete -= deleteLenInPiece;
                    }
                    else if (deleteStartInPiece + deleteLenInPiece == piece.Length)
                    {
                        // Trim the end of the piece
                        _pieces[i] = new Piece(piece.Source, piece.Start, deleteStartInPiece);
                        remainingToDelete -= deleteLenInPiece;
                    }
                    else
                    {
                        // Split the piece around the deleted segment
                        var left = new Piece(piece.Source, piece.Start, deleteStartInPiece);
                        var right = new Piece(piece.Source, piece.Start + deleteStartInPiece + deleteLenInPiece, piece.Length - (deleteStartInPiece + deleteLenInPiece));

                        _pieces[i] = left;
                        _pieces.Insert(i + 1, right);
                        remainingToDelete -= deleteLenInPiece;
                    }
                }
                
                currentOffset += _pieces[i].Length;
                i++;
            }

            _totalLength -= length;
        }

        public ReadOnlySpan<char> GetContiguousSpan(int position, int length, out bool isContiguous)
        {
            if (position < 0 || length < 0 || position + length > _totalLength)
                throw new ArgumentOutOfRangeException(nameof(position));

            if (length == 0)
            {
                isContiguous = true;
                return ReadOnlySpan<char>.Empty;
            }

            int currentOffset = 0;
            foreach (var piece in _pieces)
            {
                int pieceEnd = currentOffset + piece.Length;
                if (position >= currentOffset && position < pieceEnd)
                {
                    // Found the starting piece. Check if the entire request fits in it.
                    if (position + length <= pieceEnd)
                    {
                        int relOffset = position - currentOffset;
                        isContiguous = true;
                        return piece.Source == PieceSource.Original
                            ? _originalText.Span.Slice(piece.Start + relOffset, length)
                            : _addedText.AsSpan(piece.Start + relOffset, length);
                    }
                    break;
                }
                currentOffset = pieceEnd;
            }

            isContiguous = false;
            return ReadOnlySpan<char>.Empty;
        }

        public void GetText(int position, int length, Span<char> destination)
        {
            if (destination.Length < length)
                throw new ArgumentException("Destination too small", nameof(destination));

            int destIndex = 0;
            int currentPos = 0;

            foreach (var piece in _pieces)
            {
                int pieceEnd = currentPos + piece.Length;
                if (position < pieceEnd && position + length > currentPos)
                {
                    int startInPiece = Math.Max(0, position - currentPos);
                    int lengthInPiece = Math.Min(piece.Length - startInPiece, (position + length) - (currentPos + startInPiece));

                    ReadOnlySpan<char> sourceSpan = piece.Source == PieceSource.Original
                        ? _originalText.Span.Slice(piece.Start + startInPiece, lengthInPiece)
                        : _addedText.AsSpan(piece.Start + startInPiece, lengthInPiece);

                    sourceSpan.CopyTo(destination.Slice(destIndex, lengthInPiece));
                    destIndex += lengthInPiece;
                }
                currentPos = pieceEnd;
                if (destIndex >= length)
                    break;
            }
        }

        private void EnsureAddedCapacity(int needed)
        {
            if (_addedLength + needed > _addedText.Length)
            {
                int newSize = Math.Max(_addedText.Length * 2, _addedLength + needed);
                var newArray = ArrayPool<char>.Shared.Rent(newSize);
                Array.Copy(_addedText, newArray, _addedLength);
                ArrayPool<char>.Shared.Return(_addedText);
                _addedText = newArray;
            }
        }

        public void Dispose()
        {
            if (_addedText != null)
            {
                ArrayPool<char>.Shared.Return(_addedText);
                _addedText = null!;
            }
        }
    }
}
