using System;
using System.Runtime.InteropServices;

namespace SpanCoder.Contracts
{
    public static class MessageTypes
    {
        public const byte InsertText = 1;
        public const byte DeleteText = 2;
        public const byte LoadFile = 3;
        public const byte DocumentChanged = 4;
        public const byte CommandRequest = 5;
        public const byte DiagnosticsReport = 6;
        public const byte AutocompleteRequest = 7;
        public const byte AutocompleteResponse = 8;
        public const byte HoverRequest = 9;
        public const byte HoverResponse = 10;
        public const byte RegisterExtension = 11;
        public const byte ExecuteExtensionCommand = 12;
        public const byte UpdateExtensionPanel = 13;
        public const byte GotoDefinitionRequest = 14;
        public const byte GotoDefinitionResponse = 15;
        public const byte FindReferencesRequest = 16;
        public const byte FindReferencesResponse = 17;
        public const byte RenameRequest = 18;
        public const byte RenameResponse = 19;
        public const byte DocumentSymbolsRequest = 20;
        public const byte DocumentSymbolsResponse = 21;
        public const byte DebugStartRequest = 22;
        public const byte DebugStopRequest = 23;
        public const byte DebugStepOverRequest = 24;
        public const byte DebugStepIntoRequest = 25;
        public const byte DebugStepOutRequest = 26;
        public const byte DebugContinueRequest = 27;
        public const byte DebugSetBreakpointsRequest = 28;
        public const byte DebugStoppedEvent = 29;
        public const byte DebugStateReport = 30;
        public const byte AiChatRequest = 31;
        public const byte AiChatResponse = 32;
        public const byte AiToolExecutionEvent = 33;
        public const byte AiStopCommand = 34;
        public const byte FoldingRangeRequest = 35;
        public const byte FoldingRangeResponse = 36;
        public const byte BatchEditRequest = 37;
        public const byte BatchEditResponse = 38;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MessageHeader
    {
        public byte Type;
        public int Length; // Total length of message including header
        public int DocumentId;
        public int Offset;
    }

    public struct DiagnosticItem
    {
        public int StartOffset;
        public int EndOffset;
        public byte Severity; // 1 = Error, 2 = Warning, 3 = Info, 4 = Hint
        public string Message;
    }

    public struct AutocompleteItem
    {
        public string Label;
        public string Detail;
    }

    public struct ReferenceItem
    {
        public string FilePath;
        public int Line;
        public int Character;
    }

    public struct DocumentSymbolItem
    {
        public string Name;
        public string Detail;
        public int Line;
        public int Character;
    }

    public struct InlayHintItem
    {
        public int Offset;
        public string Label;
        public string Tooltip;

        public InlayHintItem(int offset, string label, string tooltip = "")
        {
            Offset = offset;
            Label = label;
            Tooltip = tooltip;
        }
    }

    public struct FoldingRangeItem
    {
        public int StartLine;
        public int EndLine;
    }

    public struct TextEdit
    {
        public int Offset;
        public int DeleteLength;
        public string Text;
    }

    public static class BinaryMessageSerializer
    {
        public static readonly int HeaderSize = Marshal.SizeOf<MessageHeader>();

        public static int WriteHeader(Span<byte> buffer, byte type, int totalLength, int documentId, int offset)
        {
            if (buffer.Length < HeaderSize)
                throw new ArgumentException("Buffer too small for header", nameof(buffer));

            var header = new MessageHeader
            {
                Type = type,
                Length = totalLength,
                DocumentId = documentId,
                Offset = offset
            };

            MemoryMarshal.Write(buffer, in header);
            return HeaderSize;
        }

        public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out MessageHeader header)
        {
            if (buffer.Length < HeaderSize)
            {
                header = default;
                return false;
            }

            header = MemoryMarshal.Read<MessageHeader>(buffer.Slice(0, HeaderSize));
            return true;
        }

        public static int WriteInsertText(Span<byte> buffer, int documentId, int offset, ReadOnlySpan<char> text)
        {
            int textBytesCount = text.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + textBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.InsertText, totalLength, documentId, offset);
            
            int textLenBytes = text.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in textLenBytes);

            ReadOnlySpan<byte> textSpanBytes = MemoryMarshal.AsBytes(text);
            textSpanBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));

            return totalLength;
        }

        public static ReadOnlySpan<char> ParseInsertText(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int textLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> textBytes = messageBuffer.Slice(HeaderSize + sizeof(int), textLen * sizeof(char));
            return MemoryMarshal.Cast<byte, char>(textBytes);
        }

        public static int WriteDeleteText(Span<byte> buffer, int documentId, int offset, int length)
        {
            int totalLength = HeaderSize + sizeof(int);

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DeleteText, totalLength, documentId, offset);
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in length);

            return totalLength;
        }

        public static int ParseDeleteText(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            return MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
        }

        public static int WriteLoadFile(Span<byte> buffer, ReadOnlySpan<char> filePath)
        {
            int pathBytesCount = filePath.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + pathBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.LoadFile, totalLength, 0, 0);
            
            int pathLen = filePath.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in pathLen);

            ReadOnlySpan<byte> pathSpanBytes = MemoryMarshal.AsBytes(filePath);
            pathSpanBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));

            return totalLength;
        }

        public static ReadOnlySpan<char> ParseLoadFile(ReadOnlySpan<byte> messageBuffer)
        {
            int pathLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> pathBytes = messageBuffer.Slice(HeaderSize + sizeof(int), pathLen * sizeof(char));
            return MemoryMarshal.Cast<byte, char>(pathBytes);
        }

        public static int WriteDocumentChanged(Span<byte> buffer, int documentId, int offset, int addedLength, int deletedLength, ReadOnlySpan<char> insertedText)
        {
            int textBytesCount = insertedText.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + sizeof(int) + textBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DocumentChanged, totalLength, documentId, offset);
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in addedLength);
            MemoryMarshal.Write(buffer.Slice(HeaderSize + sizeof(int), sizeof(int)), in deletedLength);

            if (textBytesCount > 0)
            {
                ReadOnlySpan<byte> textSpanBytes = MemoryMarshal.AsBytes(insertedText);
                textSpanBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int) + sizeof(int)));
            }

            return totalLength;
        }

        public static ReadOnlySpan<char> ParseDocumentChanged(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset, out int addedLength, out int deletedLength)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            addedLength = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            deletedLength = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize + sizeof(int), sizeof(int)));

            if (addedLength > 0)
            {
                ReadOnlySpan<byte> textBytes = messageBuffer.Slice(HeaderSize + sizeof(int) + sizeof(int), addedLength * sizeof(char));
                return MemoryMarshal.Cast<byte, char>(textBytes);
            }

            return ReadOnlySpan<char>.Empty;
        }

        public static int WriteCommandRequest(Span<byte> buffer, ReadOnlySpan<char> commandId)
        {
            int cmdBytesCount = commandId.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + cmdBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.CommandRequest, totalLength, 0, 0);

            int cmdLen = commandId.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in cmdLen);

            ReadOnlySpan<byte> cmdSpanBytes = MemoryMarshal.AsBytes(commandId);
            cmdSpanBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));

            return totalLength;
        }

        public static ReadOnlySpan<char> ParseCommandRequest(ReadOnlySpan<byte> messageBuffer)
        {
            int cmdLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> cmdBytes = messageBuffer.Slice(HeaderSize + sizeof(int), cmdLen * sizeof(char));
            return MemoryMarshal.Cast<byte, char>(cmdBytes);
        }

        public static int WriteDiagnosticsReport(Span<byte> buffer, int documentId, ReadOnlySpan<DiagnosticItem> items)
        {
            int bodyLength = sizeof(int);
            for (int i = 0; i < items.Length; i++)
            {
                bodyLength += sizeof(int) * 2 + sizeof(byte) + sizeof(int) + items[i].Message.Length * sizeof(char);
            }
            int totalLength = HeaderSize + bodyLength;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DiagnosticsReport, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int count = items.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                int start = items[i].StartOffset;
                int end = items[i].EndOffset;
                byte severity = items[i].Severity;
                int msgLen = items[i].Message.Length;

                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in start);
                writeOffset += sizeof(int);

                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in end);
                writeOffset += sizeof(int);

                buffer[writeOffset] = severity;
                writeOffset += sizeof(byte);

                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in msgLen);
                writeOffset += sizeof(int);

                if (msgLen > 0)
                {
                    ReadOnlySpan<byte> msgBytes = MemoryMarshal.AsBytes(items[i].Message.AsSpan());
                    msgBytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += msgLen * sizeof(char);
                }
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<DiagnosticItem> ParseDiagnosticsReport(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var items = new System.Collections.Generic.List<DiagnosticItem>(count);
            for (int i = 0; i < count; i++)
            {
                int start = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                int end = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                byte severity = messageBuffer[readOffset];
                readOffset += sizeof(byte);

                int msgLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string message = "";
                if (msgLen > 0)
                {
                    ReadOnlySpan<byte> msgBytes = messageBuffer.Slice(readOffset, msgLen * sizeof(char));
                    message = new string(MemoryMarshal.Cast<byte, char>(msgBytes));
                    readOffset += msgLen * sizeof(char);
                }

                items.Add(new DiagnosticItem
                {
                    StartOffset = start,
                    EndOffset = end,
                    Severity = severity,
                    Message = message
                });
            }

            return items;
        }

        public static int WriteAutocompleteRequest(Span<byte> buffer, int documentId, int offset)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.AutocompleteRequest, totalLength, documentId, offset);
            return totalLength;
        }

        public static int WriteAutocompleteResponse(Span<byte> buffer, int documentId, int offset, ReadOnlySpan<AutocompleteItem> items)
        {
            int bodyLength = sizeof(int);
            for (int i = 0; i < items.Length; i++)
            {
                bodyLength += sizeof(int) * 2 + (items[i].Label.Length + items[i].Detail.Length) * sizeof(char);
            }

            int totalLength = HeaderSize + bodyLength;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.AutocompleteResponse, totalLength, documentId, offset);
            int writeOffset = HeaderSize;

            int count = items.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                int labelLen = items[i].Label.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in labelLen);
                writeOffset += sizeof(int);

                if (labelLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(items[i].Label.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += labelLen * sizeof(char);
                }

                int detailLen = items[i].Detail.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in detailLen);
                writeOffset += sizeof(int);

                if (detailLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(items[i].Detail.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += detailLen * sizeof(char);
                }
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<AutocompleteItem> ParseAutocompleteResponse(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var items = new System.Collections.Generic.List<AutocompleteItem>(count);
            for (int i = 0; i < count; i++)
            {
                int labelLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string label = "";
                if (labelLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, labelLen * sizeof(char));
                    label = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += labelLen * sizeof(char);
                }

                int detailLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string detail = "";
                if (detailLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, detailLen * sizeof(char));
                    detail = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += detailLen * sizeof(char);
                }

                items.Add(new AutocompleteItem { Label = label, Detail = detail });
            }

            return items;
        }

        public static int WriteHoverRequest(Span<byte> buffer, int documentId, int offset)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.HoverRequest, totalLength, documentId, offset);
            return totalLength;
        }

        public static int WriteHoverResponse(Span<byte> buffer, int documentId, int offset, int startOffset, int endOffset, ReadOnlySpan<char> text)
        {
            int textBytesCount = text.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) * 3 + textBytesCount;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.HoverResponse, totalLength, documentId, offset);
            int writeOffset = HeaderSize;

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in startOffset);
            writeOffset += sizeof(int);

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in endOffset);
            writeOffset += sizeof(int);

            int textLen = text.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in textLen);
            writeOffset += sizeof(int);

            if (textBytesCount > 0)
            {
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(text);
                bytes.CopyTo(buffer.Slice(writeOffset));
            }

            return totalLength;
        }

        public static string ParseHoverResponse(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset, out int startOffset, out int endOffset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int readOffset = HeaderSize;
            startOffset = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            endOffset = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            int textLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            if (textLen > 0)
            {
                ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, textLen * sizeof(char));
                return new string(MemoryMarshal.Cast<byte, char>(bytes));
            }

            return "";
        }

        public static int WriteRegisterExtension(Span<byte> buffer, ReadOnlySpan<byte> manifestJsonBytes)
        {
            int totalLength = HeaderSize + sizeof(int) + manifestJsonBytes.Length;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.RegisterExtension, totalLength, 0, 0);
            int jsonLen = manifestJsonBytes.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in jsonLen);
            manifestJsonBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));
            return totalLength;
        }

        public static ReadOnlySpan<byte> ParseRegisterExtension(ReadOnlySpan<byte> messageBuffer)
        {
            int jsonLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            return messageBuffer.Slice(HeaderSize + sizeof(int), jsonLen);
        }

        public static int WriteExecuteExtensionCommand(Span<byte> buffer, ReadOnlySpan<char> commandId)
        {
            int bytesCount = commandId.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + bytesCount;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.ExecuteExtensionCommand, totalLength, 0, 0);
            int len = commandId.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in len);
            ReadOnlySpan<byte> cmdBytes = MemoryMarshal.AsBytes(commandId);
            cmdBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));
            return totalLength;
        }

        public static string ParseExecuteExtensionCommand(ReadOnlySpan<byte> messageBuffer)
        {
            int len = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> bytes = messageBuffer.Slice(HeaderSize + sizeof(int), len * sizeof(char));
            return new string(MemoryMarshal.Cast<byte, char>(bytes));
        }

        public static int WriteUpdateExtensionPanel(Span<byte> buffer, ReadOnlySpan<char> panelId, ReadOnlySpan<char> content)
        {
            int panelIdBytesCount = panelId.Length * sizeof(char);
            int contentBytesCount = content.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + panelIdBytesCount + sizeof(int) + contentBytesCount;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.UpdateExtensionPanel, totalLength, 0, 0);
            int writeOffset = HeaderSize;

            int panelIdLen = panelId.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in panelIdLen);
            writeOffset += sizeof(int);

            ReadOnlySpan<byte> panelIdBytes = MemoryMarshal.AsBytes(panelId);
            panelIdBytes.CopyTo(buffer.Slice(writeOffset));
            writeOffset += panelIdBytesCount;

            int contentLen = content.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in contentLen);
            writeOffset += sizeof(int);

            ReadOnlySpan<byte> contentBytes = MemoryMarshal.AsBytes(content);
            contentBytes.CopyTo(buffer.Slice(writeOffset));

            return totalLength;
        }

        public static void ParseUpdateExtensionPanel(ReadOnlySpan<byte> messageBuffer, out string panelId, out string content)
        {
            int readOffset = HeaderSize;

            int panelIdLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            ReadOnlySpan<byte> panelIdBytes = messageBuffer.Slice(readOffset, panelIdLen * sizeof(char));
            panelId = new string(MemoryMarshal.Cast<byte, char>(panelIdBytes));
            readOffset += panelIdLen * sizeof(char);

            int contentLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            ReadOnlySpan<byte> contentBytes = messageBuffer.Slice(readOffset, contentLen * sizeof(char));
            content = new string(MemoryMarshal.Cast<byte, char>(contentBytes));
        }

        public static int WriteGotoDefinitionRequest(Span<byte> buffer, int documentId, int offset)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.GotoDefinitionRequest, totalLength, documentId, offset);
            return totalLength;
        }

        public static int WriteGotoDefinitionResponse(Span<byte> buffer, int documentId, int offset, ReadOnlySpan<char> filePath, int line, int character)
        {
            int pathBytesCount = filePath.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + pathBytesCount + sizeof(int) + sizeof(int);

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.GotoDefinitionResponse, totalLength, documentId, offset);
            int writeOffset = HeaderSize;

            int pathLen = filePath.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in pathLen);
            writeOffset += sizeof(int);

            if (pathBytesCount > 0)
            {
                ReadOnlySpan<byte> pathBytes = MemoryMarshal.AsBytes(filePath);
                pathBytes.CopyTo(buffer.Slice(writeOffset));
                writeOffset += pathBytesCount;
            }

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in line);
            writeOffset += sizeof(int);

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in character);

            return totalLength;
        }

        public static string ParseGotoDefinitionResponse(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset, out int line, out int character)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int readOffset = HeaderSize;
            int pathLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            string filePath = "";
            if (pathLen > 0)
            {
                ReadOnlySpan<byte> pathBytes = messageBuffer.Slice(readOffset, pathLen * sizeof(char));
                filePath = new string(MemoryMarshal.Cast<byte, char>(pathBytes));
                readOffset += pathLen * sizeof(char);
            }

            line = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            character = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));

            return filePath;
        }

        public static int WriteFindReferencesRequest(Span<byte> buffer, int documentId, int offset)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.FindReferencesRequest, totalLength, documentId, offset);
            return totalLength;
        }

        public static int WriteFindReferencesResponse(Span<byte> buffer, int documentId, int offset, ReadOnlySpan<ReferenceItem> items)
        {
            int bodyLength = sizeof(int);
            for (int i = 0; i < items.Length; i++)
            {
                bodyLength += sizeof(int) + items[i].FilePath.Length * sizeof(char) + sizeof(int) + sizeof(int);
            }

            int totalLength = HeaderSize + bodyLength;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.FindReferencesResponse, totalLength, documentId, offset);
            int writeOffset = HeaderSize;

            int count = items.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                string path = items[i].FilePath;
                int pathLen = path.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in pathLen);
                writeOffset += sizeof(int);

                if (pathLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(path.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += pathLen * sizeof(char);
                }

                int line = items[i].Line;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in line);
                writeOffset += sizeof(int);

                int character = items[i].Character;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in character);
                writeOffset += sizeof(int);
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<ReferenceItem> ParseFindReferencesResponse(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var items = new System.Collections.Generic.List<ReferenceItem>(count);
            for (int i = 0; i < count; i++)
            {
                int pathLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string path = "";
                if (pathLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, pathLen * sizeof(char));
                    path = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += pathLen * sizeof(char);
                }

                int line = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                int character = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                items.Add(new ReferenceItem { FilePath = path, Line = line, Character = character });
            }

            return items;
        }

        public static int WriteRenameRequest(Span<byte> buffer, int documentId, int offset, ReadOnlySpan<char> newName)
        {
            int nameBytesCount = newName.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + nameBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.RenameRequest, totalLength, documentId, offset);
            int writeOffset = HeaderSize;

            int nameLen = newName.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in nameLen);
            writeOffset += sizeof(int);

            if (nameBytesCount > 0)
            {
                ReadOnlySpan<byte> nameBytes = MemoryMarshal.AsBytes(newName);
                nameBytes.CopyTo(buffer.Slice(writeOffset));
            }

            return totalLength;
        }

        public static string ParseRenameRequest(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;

            int nameLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> nameBytes = messageBuffer.Slice(HeaderSize + sizeof(int), nameLen * sizeof(char));
            return new string(MemoryMarshal.Cast<byte, char>(nameBytes));
        }

        public static int WriteRenameResponse(Span<byte> buffer, int documentId, int offset, bool success)
        {
            int totalLength = HeaderSize + sizeof(byte);
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.RenameResponse, totalLength, documentId, offset);
            buffer[HeaderSize] = success ? (byte)1 : (byte)0;
            return totalLength;
        }

        public static bool ParseRenameResponse(ReadOnlySpan<byte> messageBuffer, out int documentId, out int offset)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;
            offset = header.Offset;
            return messageBuffer[HeaderSize] == 1;
        }

        public static int WriteDocumentSymbolsRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DocumentSymbolsRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteDocumentSymbolsResponse(Span<byte> buffer, int documentId, ReadOnlySpan<DocumentSymbolItem> items)
        {
            int bodyLength = sizeof(int);
            for (int i = 0; i < items.Length; i++)
            {
                bodyLength += sizeof(int) + items[i].Name.Length * sizeof(char) +
                             sizeof(int) + items[i].Detail.Length * sizeof(char) +
                             sizeof(int) + sizeof(int);
            }

            int totalLength = HeaderSize + bodyLength;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DocumentSymbolsResponse, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int count = items.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                string name = items[i].Name;
                int nameLen = name.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in nameLen);
                writeOffset += sizeof(int);

                if (nameLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(name.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += nameLen * sizeof(char);
                }

                string detail = items[i].Detail;
                int detailLen = detail.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in detailLen);
                writeOffset += sizeof(int);

                if (detailLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(detail.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += detailLen * sizeof(char);
                }

                int line = items[i].Line;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in line);
                writeOffset += sizeof(int);

                int character = items[i].Character;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in character);
                writeOffset += sizeof(int);
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<DocumentSymbolItem> ParseDocumentSymbolsResponse(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var items = new System.Collections.Generic.List<DocumentSymbolItem>(count);
            for (int i = 0; i < count; i++)
            {
                int nameLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string name = "";
                if (nameLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, nameLen * sizeof(char));
                    name = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += nameLen * sizeof(char);
                }

                int detailLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string detail = "";
                if (detailLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, detailLen * sizeof(char));
                    detail = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += detailLen * sizeof(char);
                }

                int line = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                int character = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                items.Add(new DocumentSymbolItem { Name = name, Detail = detail, Line = line, Character = character });
            }

            return items;
        }

        public static int WriteDebugStartRequest(Span<byte> buffer, int documentId, ReadOnlySpan<char> path)
        {
            int pathBytesCount = path.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + pathBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStartRequest, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int pathLen = path.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in pathLen);
            writeOffset += sizeof(int);

            if (pathBytesCount > 0)
            {
                ReadOnlySpan<byte> pathBytes = MemoryMarshal.AsBytes(path);
                pathBytes.CopyTo(buffer.Slice(writeOffset));
            }

            return totalLength;
        }

        public static string ParseDebugStartRequest(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            int pathLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            if (pathLen > 0)
            {
                ReadOnlySpan<byte> pathBytes = messageBuffer.Slice(readOffset, pathLen * sizeof(char));
                return new string(MemoryMarshal.Cast<byte, char>(pathBytes));
            }
            return "";
        }

        public static int WriteDebugSetBreakpointsRequest(Span<byte> buffer, int documentId, ReadOnlySpan<int> lines)
        {
            int bodyLen = sizeof(int) + lines.Length * sizeof(int);
            int totalLength = HeaderSize + bodyLen;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugSetBreakpointsRequest, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int count = lines.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                int line = lines[i];
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in line);
                writeOffset += sizeof(int);
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<int> ParseDebugSetBreakpointsRequest(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var list = new System.Collections.Generic.List<int>(count);
            for (int i = 0; i < count; i++)
            {
                int line = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);
                list.Add(line);
            }
            return list;
        }

        public static int WriteDebugStoppedEvent(Span<byte> buffer, int documentId, int line, int character, ReadOnlySpan<char> reason)
        {
            int reasonBytesCount = reason.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + sizeof(int) + sizeof(int) + reasonBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStoppedEvent, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in line);
            writeOffset += sizeof(int);

            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in character);
            writeOffset += sizeof(int);

            int reasonLen = reason.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in reasonLen);
            writeOffset += sizeof(int);

            if (reasonBytesCount > 0)
            {
                ReadOnlySpan<byte> reasonBytes = MemoryMarshal.AsBytes(reason);
                reasonBytes.CopyTo(buffer.Slice(writeOffset));
            }

            return totalLength;
        }

        public static string ParseDebugStoppedEvent(ReadOnlySpan<byte> messageBuffer, out int documentId, out int line, out int character)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            line = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            character = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            int reasonLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            if (reasonLen > 0)
            {
                ReadOnlySpan<byte> reasonBytes = messageBuffer.Slice(readOffset, reasonLen * sizeof(char));
                return new string(MemoryMarshal.Cast<byte, char>(reasonBytes));
            }
            return "";
        }

        public static int WriteDebugStateReport(Span<byte> buffer, int documentId, System.Collections.Generic.List<string> stackFrames, System.Collections.Generic.List<string> variables)
        {
            int bodyLen = sizeof(int); // stackFrames count
            for (int i = 0; i < stackFrames.Count; i++)
            {
                bodyLen += sizeof(int) + stackFrames[i].Length * sizeof(char);
            }

            bodyLen += sizeof(int); // variables count
            for (int i = 0; i < variables.Count; i++)
            {
                bodyLen += sizeof(int) + variables[i].Length * sizeof(char);
            }

            int totalLength = HeaderSize + bodyLen;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStateReport, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int sfCount = stackFrames.Count;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in sfCount);
            writeOffset += sizeof(int);

            for (int i = 0; i < sfCount; i++)
            {
                string sf = stackFrames[i];
                int sfLen = sf.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in sfLen);
                writeOffset += sizeof(int);

                if (sfLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(sf.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += sfLen * sizeof(char);
                }
            }

            int varCount = variables.Count;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in varCount);
            writeOffset += sizeof(int);

            for (int i = 0; i < varCount; i++)
            {
                string v = variables[i];
                int vLen = v.Length;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in vLen);
                writeOffset += sizeof(int);

                if (vLen > 0)
                {
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(v.AsSpan());
                    bytes.CopyTo(buffer.Slice(writeOffset));
                    writeOffset += vLen * sizeof(char);
                }
            }

            return totalLength;
        }

        public static void ParseDebugStateReport(ReadOnlySpan<byte> messageBuffer, out int documentId, out System.Collections.Generic.List<string> stackFrames, out System.Collections.Generic.List<string> variables)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;

            int sfCount = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            stackFrames = new System.Collections.Generic.List<string>(sfCount);
            for (int i = 0; i < sfCount; i++)
            {
                int sfLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string sf = "";
                if (sfLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, sfLen * sizeof(char));
                    sf = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += sfLen * sizeof(char);
                }
                stackFrames.Add(sf);
            }

            int varCount = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            variables = new System.Collections.Generic.List<string>(varCount);
            for (int i = 0; i < varCount; i++)
            {
                int vLen = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                string v = "";
                if (vLen > 0)
                {
                    ReadOnlySpan<byte> bytes = messageBuffer.Slice(readOffset, vLen * sizeof(char));
                    v = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    readOffset += vLen * sizeof(char);
                }
                variables.Add(v);
            }
        }

        public static int WriteDebugStopRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStopRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteDebugStepOverRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStepOverRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteDebugStepIntoRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStepIntoRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteDebugStepOutRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugStepOutRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteDebugContinueRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.DebugContinueRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteStringPayload(Span<byte> buffer, byte messageType, string payload)
        {
            int payloadBytesCount = payload.Length * sizeof(char);
            int totalLength = HeaderSize + sizeof(int) + payloadBytesCount;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, messageType, totalLength, 0, 0);
            
            int payloadLen = payload.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in payloadLen);

            ReadOnlySpan<byte> payloadSpanBytes = MemoryMarshal.AsBytes(payload.AsSpan());
            payloadSpanBytes.CopyTo(buffer.Slice(HeaderSize + sizeof(int)));

            return totalLength;
        }

        public static string ParseStringPayload(ReadOnlySpan<byte> messageBuffer)
        {
            int payloadLen = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            ReadOnlySpan<byte> payloadBytes = messageBuffer.Slice(HeaderSize + sizeof(int), payloadLen * sizeof(char));
            return new string(MemoryMarshal.Cast<byte, char>(payloadBytes));
        }

        public static int WriteFoldingRangeRequest(Span<byte> buffer, int documentId)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.FoldingRangeRequest, totalLength, documentId, 0);
            return totalLength;
        }

        public static int WriteFoldingRangeResponse(Span<byte> buffer, int documentId, ReadOnlySpan<FoldingRangeItem> items)
        {
            int bodyLength = sizeof(int) + items.Length * sizeof(int) * 2;
            int totalLength = HeaderSize + bodyLength;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.FoldingRangeResponse, totalLength, documentId, 0);
            int writeOffset = HeaderSize;

            int count = items.Length;
            MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in count);
            writeOffset += sizeof(int);

            for (int i = 0; i < count; i++)
            {
                int start = items[i].StartLine;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in start);
                writeOffset += sizeof(int);

                int end = items[i].EndLine;
                MemoryMarshal.Write(buffer.Slice(writeOffset, sizeof(int)), in end);
                writeOffset += sizeof(int);
            }

            return totalLength;
        }

        public static System.Collections.Generic.List<FoldingRangeItem> ParseFoldingRangeResponse(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int readOffset = HeaderSize;
            int count = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
            readOffset += sizeof(int);

            var items = new System.Collections.Generic.List<FoldingRangeItem>(count);
            for (int i = 0; i < count; i++)
            {
                int start = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                int end = MemoryMarshal.Read<int>(messageBuffer.Slice(readOffset, sizeof(int)));
                readOffset += sizeof(int);

                items.Add(new FoldingRangeItem { StartLine = start, EndLine = end });
            }

            return items;
        }

        public static int WriteAiStopCommand(Span<byte> buffer)
        {
            int totalLength = HeaderSize;
            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.AiStopCommand, totalLength, 0, 0);
            return totalLength;
        }

        public static int WriteBatchEditRequest(Span<byte> buffer, int documentId, TextEdit[] edits)
        {
            int textEditsBytes = 0;
            foreach (var edit in edits)
            {
                textEditsBytes += sizeof(int) * 3;
                if (edit.Text != null)
                {
                    textEditsBytes += edit.Text.Length * sizeof(char);
                }
            }
            int totalLength = HeaderSize + sizeof(int) + textEditsBytes;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.BatchEditRequest, totalLength, documentId, 0);

            int editsCount = edits.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in editsCount);

            int currentOffset = HeaderSize + sizeof(int);
            foreach (var edit in edits)
            {
                int offset = edit.Offset;
                int delLen = edit.DeleteLength;
                int textLen = edit.Text?.Length ?? 0;

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in offset);
                currentOffset += sizeof(int);

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in delLen);
                currentOffset += sizeof(int);

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in textLen);
                currentOffset += sizeof(int);

                if (textLen > 0)
                {
                    ReadOnlySpan<byte> textSpanBytes = MemoryMarshal.AsBytes(edit.Text.AsSpan());
                    textSpanBytes.CopyTo(buffer.Slice(currentOffset, textLen * sizeof(char)));
                    currentOffset += textLen * sizeof(char);
                }
            }

            return totalLength;
        }

        public static TextEdit[] ParseBatchEditRequest(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int editsCount = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            var edits = new TextEdit[editsCount];

            int currentOffset = HeaderSize + sizeof(int);
            for (int i = 0; i < editsCount; i++)
            {
                int offset = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                int delLen = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                int textLen = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                string text = "";
                if (textLen > 0)
                {
                    ReadOnlySpan<byte> textBytes = messageBuffer.Slice(currentOffset, textLen * sizeof(char));
                    text = new string(MemoryMarshal.Cast<byte, char>(textBytes));
                    currentOffset += textLen * sizeof(char);
                }

                edits[i] = new TextEdit
                {
                    Offset = offset,
                    DeleteLength = delLen,
                    Text = text
                };
            }

            return edits;
        }

        public static int WriteBatchEditResponse(Span<byte> buffer, int documentId, TextEdit[] edits)
        {
            int textEditsBytes = 0;
            foreach (var edit in edits)
            {
                textEditsBytes += sizeof(int) * 3;
                if (edit.Text != null)
                {
                    textEditsBytes += edit.Text.Length * sizeof(char);
                }
            }
            int totalLength = HeaderSize + sizeof(int) + textEditsBytes;

            if (buffer.Length < totalLength)
                throw new ArgumentException("Buffer too small", nameof(buffer));

            WriteHeader(buffer, MessageTypes.BatchEditResponse, totalLength, documentId, 0);

            int editsCount = edits.Length;
            MemoryMarshal.Write(buffer.Slice(HeaderSize, sizeof(int)), in editsCount);

            int currentOffset = HeaderSize + sizeof(int);
            foreach (var edit in edits)
            {
                int offset = edit.Offset;
                int delLen = edit.DeleteLength;
                int textLen = edit.Text?.Length ?? 0;

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in offset);
                currentOffset += sizeof(int);

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in delLen);
                currentOffset += sizeof(int);

                MemoryMarshal.Write(buffer.Slice(currentOffset, sizeof(int)), in textLen);
                currentOffset += sizeof(int);

                if (textLen > 0)
                {
                    ReadOnlySpan<byte> textSpanBytes = MemoryMarshal.AsBytes(edit.Text.AsSpan());
                    textSpanBytes.CopyTo(buffer.Slice(currentOffset, textLen * sizeof(char)));
                    currentOffset += textLen * sizeof(char);
                }
            }

            return totalLength;
        }

        public static TextEdit[] ParseBatchEditResponse(ReadOnlySpan<byte> messageBuffer, out int documentId)
        {
            var header = MemoryMarshal.Read<MessageHeader>(messageBuffer.Slice(0, HeaderSize));
            documentId = header.DocumentId;

            int editsCount = MemoryMarshal.Read<int>(messageBuffer.Slice(HeaderSize, sizeof(int)));
            var edits = new TextEdit[editsCount];

            int currentOffset = HeaderSize + sizeof(int);
            for (int i = 0; i < editsCount; i++)
            {
                int offset = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                int delLen = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                int textLen = MemoryMarshal.Read<int>(messageBuffer.Slice(currentOffset, sizeof(int)));
                currentOffset += sizeof(int);

                string text = "";
                if (textLen > 0)
                {
                    ReadOnlySpan<byte> textBytes = messageBuffer.Slice(currentOffset, textLen * sizeof(char));
                    text = new string(MemoryMarshal.Cast<byte, char>(textBytes));
                    currentOffset += textLen * sizeof(char);
                }

                edits[i] = new TextEdit
                {
                    Offset = offset,
                    DeleteLength = delLen,
                    Text = text
                };
            }

            return edits;
        }
    }
}
