using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpanCoder.Engine
{
    public class LineIndex
    {
        private long[] _offsets;
        private int _count;

        public int Count => _count;

        public LineIndex()
        {
            _offsets = new long[1024];
            _offsets[0] = 0;
            _count = 1;
        }

        public ReadOnlySpan<long> Offsets => _offsets.AsSpan(0, _count);

        public void Initialize(ReadOnlySpan<char> text)
        {
            // Initial scan of line starts
            var list = new List<long> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    list.Add(i + 1);
                }
            }

            if (list.Count > _offsets.Length)
            {
                _offsets = new long[list.Count * 2];
            }

            list.CopyTo(_offsets);
            _count = list.Count;
        }

        public int GetLineIndexFromOffset(long offset)
        {
            int low = 0;
            int high = _count - 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                long val = _offsets[mid];
                if (val == offset)
                    return mid;
                if (val < offset)
                    low = mid + 1;
                else
                    high = mid - 1;
            }
            return high;
        }

        public long GetLineStart(int lineIndex)
        {
            if (lineIndex < 0 || lineIndex >= _count)
                throw new ArgumentOutOfRangeException(nameof(lineIndex));
            return _offsets[lineIndex];
        }

        public void Insert(long position, long length, ReadOnlySpan<char> insertedText)
        {
            // 1. Find the index where the offsets should shift and new lines should be inserted
            // Any line start offset > position needs to be shifted by length.
            int shiftStartIndex = 0;
            while (shiftStartIndex < _count && _offsets[shiftStartIndex] <= position)
            {
                shiftStartIndex++;
            }

            // 2. Shift existing offsets downstream using SIMD
            if (shiftStartIndex < _count)
            {
                ShiftOffsets(shiftStartIndex, length);
            }

            // 3. Count new lines in the inserted text
            int newlines = 0;
            for (int i = 0; i < insertedText.Length; i++)
            {
                if (insertedText[i] == '\n')
                {
                    newlines++;
                }
            }

            if (newlines > 0)
            {
                EnsureCapacity(_count + newlines);

                // Make room for the new line starts
                Array.Copy(_offsets, shiftStartIndex, _offsets, shiftStartIndex + newlines, _count - shiftStartIndex);

                // Populate new line starts
                int currentInsertIndex = shiftStartIndex;
                for (int i = 0; i < insertedText.Length; i++)
                {
                    if (insertedText[i] == '\n')
                    {
                        _offsets[currentInsertIndex++] = position + i + 1;
                    }
                }

                _count += newlines;
            }
        }

        public void Delete(long position, long length)
        {
            // 1. Identify which offsets are deleted (range (position, position + length])
            int deleteStartIdx = -1;
            int deleteEndIdx = -1;

            for (int i = 0; i < _count; i++)
            {
                long offset = _offsets[i];
                if (offset > position && offset <= position + length)
                {
                    if (deleteStartIdx == -1)
                        deleteStartIdx = i;
                    deleteEndIdx = i;
                }
            }

            int deletedLinesCount = 0;
            int shiftStartIndex = _count;

            if (deleteStartIdx != -1 && deleteEndIdx != -1)
            {
                deletedLinesCount = deleteEndIdx - deleteStartIdx + 1;
                shiftStartIndex = deleteEndIdx + 1;

                // Shift the remaining offsets left to overwrite deleted entries
                Array.Copy(_offsets, shiftStartIndex, _offsets, deleteStartIdx, _count - shiftStartIndex);
                _count -= deletedLinesCount;
                shiftStartIndex = deleteStartIdx;
            }
            else
            {
                // Find where the shift starts if no offsets were deleted
                shiftStartIndex = 0;
                while (shiftStartIndex < _count && _offsets[shiftStartIndex] <= position)
                {
                    shiftStartIndex++;
                }
            }

            // 2. Shift all subsequent offsets left (upstream) by -length using SIMD
            if (shiftStartIndex < _count)
            {
                ShiftOffsets(shiftStartIndex, -length);
            }
        }

        private void ShiftOffsets(int startIndex, long delta)
        {
            int i = startIndex;
            int vectorSize = Vector<long>.Count;

            if (Vector.IsHardwareAccelerated && (_count - startIndex) >= vectorSize)
            {
                var deltaVec = new Vector<long>(delta);
                // Align loop to avoid out of bounds writing
                int limit = _count - vectorSize;
                for (; i <= limit; i += vectorSize)
                {
                    var vec = new Vector<long>(_offsets, i);
                    vec += deltaVec;
                    vec.CopyTo(_offsets, i);
                }
            }

            // Fallback for remaining elements
            for (; i < _count; i++)
            {
                _offsets[i] += delta;
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required > _offsets.Length)
            {
                int newSize = Math.Max(_offsets.Length * 2, required);
                var newArray = new long[newSize];
                Array.Copy(_offsets, newArray, _count);
                _offsets = newArray;
            }
        }
    }
}
