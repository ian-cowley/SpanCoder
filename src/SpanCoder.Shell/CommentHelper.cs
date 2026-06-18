using System;
using System.Buffers;
using System.Text;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public static class CommentHelper
    {
        public static void ToggleLineComment(TextEditorCanvas canvas, IEngineConnection engine, string? filePath)
        {
            var doc = canvas.Document;
            if (doc == null) return;

            string extension = System.IO.Path.GetExtension(filePath ?? "");
            var config = LanguageConfigurationRegistry.Get(extension);
            if (string.IsNullOrEmpty(config.LineComment))
            {
                if (!string.IsNullOrEmpty(config.BlockCommentStart) && !string.IsNullOrEmpty(config.BlockCommentEnd))
                {
                    ToggleBlockComment(canvas, engine, filePath);
                }
                return;
            }

            string lineComment = config.LineComment;
            
            int startLine, endLine;
            if (canvas.HasSelection(out int selStart, out int selLen))
            {
                startLine = GetLineFromOffset(doc, selStart);
                endLine = GetLineFromOffset(doc, selStart + selLen > selStart ? selStart + selLen - 1 : selStart);
            }
            else
            {
                startLine = endLine = canvas.CaretLine;
            }

            bool allCommented = true;
            bool hasNonWhitespace = false;
            for (int i = startLine; i <= endLine; i++)
            {
                var lineSpan = doc.GetLine(i, out _, out var rented);
                
                int leadingWs = 0;
                while (leadingWs < lineSpan.Length && char.IsWhiteSpace(lineSpan[leadingWs]))
                {
                    leadingWs++;
                }

                if (leadingWs < lineSpan.Length)
                {
                    hasNonWhitespace = true;
                    var remaining = lineSpan.Slice(leadingWs);
                    if (!remaining.StartsWith(lineComment))
                    {
                        allCommented = false;
                    }
                }

                if (rented != null) ArrayPool<char>.Shared.Return(rented);
            }

            if (!hasNonWhitespace)
            {
                int lineStart = (int)doc.GetLineStart(startLine);
                byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + lineComment.Length * 2];
                BinaryMessageSerializer.WriteInsertText(insBuf, doc.Id, lineStart, lineComment);
                engine.Send(insBuf);
                return;
            }

            for (int i = endLine; i >= startLine; i--)
            {
                var lineSpan = doc.GetLine(i, out _, out var rented);
                int leadingWs = 0;
                while (leadingWs < lineSpan.Length && char.IsWhiteSpace(lineSpan[leadingWs]))
                {
                    leadingWs++;
                }

                if (leadingWs < lineSpan.Length)
                {
                    int insertOffset = (int)doc.GetLineStart(i) + leadingWs;
                    if (allCommented)
                    {
                        byte[] delBuf = new byte[BinaryMessageSerializer.HeaderSize + 4];
                        BinaryMessageSerializer.WriteDeleteText(delBuf, doc.Id, insertOffset, lineComment.Length);
                        engine.Send(delBuf);
                    }
                    else
                    {
                        byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + lineComment.Length * 2];
                        BinaryMessageSerializer.WriteInsertText(insBuf, doc.Id, insertOffset, lineComment);
                        engine.Send(insBuf);
                    }
                }

                if (rented != null) ArrayPool<char>.Shared.Return(rented);
            }
        }

        public static void ToggleBlockComment(TextEditorCanvas canvas, IEngineConnection engine, string? filePath)
        {
            var doc = canvas.Document;
            if (doc == null) return;

            string extension = System.IO.Path.GetExtension(filePath ?? "");
            var config = LanguageConfigurationRegistry.Get(extension);
            if (string.IsNullOrEmpty(config.BlockCommentStart) || string.IsNullOrEmpty(config.BlockCommentEnd))
            {
                return;
            }

            string blockStart = config.BlockCommentStart;
            string blockEnd = config.BlockCommentEnd;

            bool hasSel = canvas.HasSelection(out int selStart, out int selLen);
            int startOffset, length;
            if (hasSel)
            {
                startOffset = selStart;
                length = selLen;
            }
            else
            {
                int line = canvas.CaretLine;
                long lineStart = doc.GetLineStart(line);
                long lineEnd = (line + 1 < doc.GetLineCount()) ? doc.GetLineStart(line + 1) : doc.Length;
                
                int len = (int)(lineEnd - lineStart);
                var lineSpan = doc.GetLine(line, out _, out var rented);
                if (len > 0 && lineSpan[len - 1] == '\n') len--;
                if (len > 0 && lineSpan[len - 1] == '\r') len--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);

                startOffset = (int)lineStart;
                length = len;
            }

            if (length <= 0)
            {
                byte[] insBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + (blockStart.Length + blockEnd.Length) * 2];
                BinaryMessageSerializer.WriteInsertText(insBuf, doc.Id, startOffset, blockStart + blockEnd);
                engine.Send(insBuf);
                return;
            }

            string selectedText = GetTextFromDocument(doc, startOffset, length);

            if (selectedText.StartsWith(blockStart) && selectedText.EndsWith(blockEnd))
            {
                byte[] delEndBuf = new byte[BinaryMessageSerializer.HeaderSize + 4];
                BinaryMessageSerializer.WriteDeleteText(delEndBuf, doc.Id, startOffset + length - blockEnd.Length, blockEnd.Length);
                engine.Send(delEndBuf);

                byte[] delStartBuf = new byte[BinaryMessageSerializer.HeaderSize + 4];
                BinaryMessageSerializer.WriteDeleteText(delStartBuf, doc.Id, startOffset, blockStart.Length);
                engine.Send(delStartBuf);
            }
            else
            {
                byte[] insEndBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + blockEnd.Length * 2];
                BinaryMessageSerializer.WriteInsertText(insEndBuf, doc.Id, startOffset + length, blockEnd);
                engine.Send(insEndBuf);

                byte[] insStartBuf = new byte[BinaryMessageSerializer.HeaderSize + 4 + blockStart.Length * 2];
                BinaryMessageSerializer.WriteInsertText(insStartBuf, doc.Id, startOffset, blockStart);
                engine.Send(insStartBuf);
            }
        }

        private static int GetLineFromOffset(IDocumentView doc, int offset)
        {
            int lineCount = doc.GetLineCount();
            for (int i = 0; i < lineCount; i++)
            {
                long start = doc.GetLineStart(i);
                long end = (i + 1 < lineCount) ? doc.GetLineStart(i + 1) : doc.Length;
                
                if (offset == start)
                {
                    return i;
                }
                if (offset > start && offset < end)
                {
                    return i;
                }
                if (offset == end && i == lineCount - 1)
                {
                    return i;
                }
            }
            return 0;
        }

        private static string GetTextFromDocument(IDocumentView doc, int offset, int len)
        {
            if (doc == null || len <= 0) return "";
            
            var sb = new StringBuilder(len);
            int lineCount = doc.GetLineCount();
            int remaining = len;
            int currentOffset = offset;

            for (int i = 0; i < lineCount && remaining > 0; i++)
            {
                long lineStart = doc.GetLineStart(i);
                long lineEnd = (i + 1 < lineCount) ? doc.GetLineStart(i + 1) : doc.Length;

                if (lineStart < currentOffset + remaining && lineEnd > currentOffset)
                {
                    int selStartInLine = Math.Max((int)lineStart, currentOffset) - (int)lineStart;
                    int selEndInLine = Math.Min((int)lineEnd, currentOffset + remaining) - (int)lineStart;

                    int chunkLen = selEndInLine - selStartInLine;
                    if (chunkLen > 0)
                    {
                        var lineSpan = doc.GetLine(i, out _, out var rented);
                        sb.Append(lineSpan.Slice(selStartInLine, chunkLen).ToString());
                        if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);

                        remaining -= chunkLen;
                        currentOffset += chunkLen;
                    }
                }
            }

            return sb.ToString();
        }
    }
}
