using System;
using System.Buffers;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public enum VimMode
    {
        Normal,
        Insert
    }

    public class TextEditorCanvas : Control
    {
        // Vim Emulation State
        private bool _vimEnabled = false;
        private VimMode _vimMode = VimMode.Insert;
        private string _pendingVimCommand = "";

        public bool VimEnabled => _vimEnabled;
        public VimMode VimMode => _vimMode;

        public event Action? VimModeChanged;
        public event Action? UndoRequested;
        public event Action? RedoRequested;
        public static readonly StyledProperty<IDocumentView?> DocumentProperty =
            AvaloniaProperty.Register<TextEditorCanvas, IDocumentView?>(nameof(Document));

        public IDocumentView? Document
        {
            get => GetValue(DocumentProperty);
            set => SetValue(DocumentProperty, value);
        }

        public double ScrollX { get; set; } = 0;
        public double ScrollY { get; set; } = 0;

        public double LineHeight { get; set; } = 20.0;
        private double _fontSize = 14.0;
        private Typeface _typeface = new Typeface("Consolas");
        public double CaretThickness { get; set; } = 2.0;
        private static readonly IBrush KeywordBrush = new SolidColorBrush(Color.Parse("#569CD6"));
        private static readonly IBrush CommentBrush = new SolidColorBrush(Color.Parse("#6A9955"));
        private static readonly IBrush StringBrush = new SolidColorBrush(Color.Parse("#D69D85"));
        private static readonly IBrush NumberBrush = new SolidColorBrush(Color.Parse("#B5CEA8"));
        private static readonly IBrush TypeBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
        private static readonly IBrush MethodBrush = new SolidColorBrush(Color.Parse("#DCDCAA"));
        private static readonly IBrush PreprocessorBrush = new SolidColorBrush(Color.Parse("#9B9B9B"));
        private static readonly IBrush AttributeBrush = new SolidColorBrush(Color.Parse("#4EC9B0"));
        private static readonly IBrush TagBrush = new SolidColorBrush(Color.Parse("#569CD6"));
        private static readonly IBrush SelectorBrush = new SolidColorBrush(Color.Parse("#D7BA7D"));
        private static readonly IBrush PropertyBrush = new SolidColorBrush(Color.Parse("#9CDCFE"));
        private static readonly IBrush HeadingBrush = new SolidColorBrush(Color.Parse("#569CD6"));
        private double _charWidth = 0.0;

        public double CharWidth
        {
            get
            {
                if (_charWidth <= 0.0)
                {
                    try
                    {
                        var measureText = new string('X', 100);
                        var formatted = new FormattedText(
                            measureText,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            _typeface,
                            _fontSize,
                            Brushes.LightGray
                        );
                        _charWidth = formatted.Width / 100.0;
                    }
                    catch
                    {
                        _charWidth = 8.2; // Monospace fallback
                    }
                }
                return _charWidth;
            }
        }

        public double GetGutterWidth()
        {
            if (Document == null) return 0;
            int lineCount = Document.GetLineCount();
            int digits = Math.Max(2, (int)Math.Log10(Math.Max(1, lineCount)) + 1);
            return digits * CharWidth + 25.0;
        }
        

        public class FoldingRange
        {
            public int StartLine { get; set; } // 0-indexed
            public int EndLine { get; set; }   // 0-indexed
            public bool IsFolded { get; set; }
        }

        private readonly System.Collections.Generic.List<FoldingRange> _foldingRanges = new();
        private readonly System.Collections.Generic.List<int> _visibleLines = new();
        private int[] _docToVisualLine = Array.Empty<int>();

        public System.Collections.Generic.IReadOnlyList<FoldingRange> FoldingRanges => _foldingRanges;
        public System.Collections.Generic.IReadOnlyList<int> VisibleLines => _visibleLines;
        public int[] DocToVisualLineMap => _docToVisualLine;

        public event Action? LayoutChanged;

        // Caret state
        public int CaretLine { get; private set; } = 0;
        public int CaretCol { get; private set; } = 0;
        public int CaretAbsoluteOffset { get; private set; } = 0;
        private bool _caretVisible = true;
        
        public struct ExtraCaret
        {
            public int Line { get; set; }
            public int Col { get; set; }
            public int AbsoluteOffset { get; set; }
        }

        private readonly System.Collections.Generic.List<ExtraCaret> _extraCarets = new();
        public System.Collections.Generic.IReadOnlyList<ExtraCaret> ExtraCarets => _extraCarets;

        private int _selectionStartOffset = -1;
        private int _selectionEndOffset = -1;
        private bool _isDragging;
        private bool _isBlockDragging;
        private int _blockDragStartLine;
        private int _blockDragStartCol;
        private readonly System.Collections.Generic.List<LineState> _lineStates = new();
        private DispatcherTimer? _caretTimer;

        private readonly System.Collections.Generic.List<DiagnosticItem> _diagnostics = new();
        private DispatcherTimer? _hoverTimer;
        private Point _lastMousePos;

        private readonly HashSet<int> _breakpoints = new();
        private int? _debugActiveLine;
        private Dictionary<int, GitLineChangeType> _gitLineChanges = new();

        private readonly System.Collections.Generic.List<InlayHintItem> _inlayHints = new();
        private readonly System.Collections.Generic.Dictionary<int, string> _codeLensItems = new();
        private readonly System.Collections.Generic.Dictionary<int, bool> _lineCoverage = new();

        public void SetInlayHints(System.Collections.Generic.IEnumerable<InlayHintItem> hints)
        {
            _inlayHints.Clear();
            _inlayHints.AddRange(hints);
            InvalidateVisual();
        }

        public void SetCodeLens(System.Collections.Generic.Dictionary<int, string> items)
        {
            _codeLensItems.Clear();
            foreach (var kvp in items)
            {
                _codeLensItems[kvp.Key] = kvp.Value;
            }
            InvalidateVisual();
        }

        public void SetLineCoverage(System.Collections.Generic.Dictionary<int, bool> coverage)
        {
            _lineCoverage.Clear();
            foreach (var kvp in coverage)
            {
                _lineCoverage[kvp.Key] = kvp.Value;
            }
            InvalidateVisual();
        }

        private (int Line, int Col) GetLineColFromOffset(int absoluteOffset)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            if (lineCount <= 0) return (0, 0);

            int low = 0;
            int high = lineCount - 1;
            int targetLine = 0;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                long start = Document.GetLineStart(mid);
                if (start <= absoluteOffset)
                {
                    targetLine = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            long lineStart = Document.GetLineStart(targetLine);
            int col = absoluteOffset - (int)lineStart;
            return (targetLine, col);
        }

        public int? DebugActiveLine
        {
            get => _debugActiveLine;
            set
            {
                _debugActiveLine = value;
                InvalidateVisual();
            }
        }

        public Dictionary<int, GitLineChangeType> GitLineChanges
        {
            get => _gitLineChanges;
            set
            {
                _gitLineChanges = value;
                InvalidateVisual();
            }
        }

        public event Action<System.Collections.Generic.List<int>>? BreakpointsChanged;

        public System.Collections.Generic.List<int> GetBreakpoints() => new System.Collections.Generic.List<int>(_breakpoints);

        public void ToggleBreakpoint(int line)
        {
            if (_breakpoints.Contains(line))
            {
                _breakpoints.Remove(line);
            }
            else
            {
                _breakpoints.Add(line);
            }
            BreakpointsChanged?.Invoke(GetBreakpoints());
            InvalidateVisual();
        }

        public bool IsAutocompleteVisible { get; set; }
        public string? GhostText { get; set; }
        public int GhostTextOffset { get; set; } = -1;

        public event Action<int, string>? TextInputReceived;
        public event Action<int, int>? TextDeleteReceived;
        public event Action<TextEdit[]>? BatchEditReceived;
        public event Action? CaretMoved;
        public event Action<double, double>? ScrollRequested;

        public event Action<int>? AutocompleteRequested;
        public event Action<int, double, double>? HoverRequested;
        public event Action<int>? GotoDefinitionRequested;
        public event Action<int>? FindReferencesRequested;
        public event Action<int>? RenameRequested;
        public event Action? MouseMovedOrLeft;
        public event Action? AutocompleteUpRequested;
        public event Action? AutocompleteDownRequested;
        public event Action? AutocompleteCommitRequested;
        public event Action? AutocompleteCancelRequested;

        public class ContextMenuItem
        {
            public string Header { get; set; } = "";
            public string CommandId { get; set; } = "";
            public string ExtensionId { get; set; } = "";
        }

        public System.Collections.Generic.List<ContextMenuItem> ExtensionContextMenuItems { get; } = new();
        public event Action<string>? ExtensionContextMenuItemClicked;

        public event Action? CutRequested;
        public event Action? CopyRequested;
        public event Action? PasteRequested;

        static TextEditorCanvas()
        {
            AffectsRender<TextEditorCanvas>(DocumentProperty);
        }

        public TextEditorCanvas()
        {
            Focusable = true;
            ClipToBounds = true;
            LoadSettings();
            SettingsManager.SettingChanged += OnSettingChanged;

            // Timer for caret blinking
            _caretTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _caretTimer.Tick += (s, e) =>
            {
                _caretVisible = !_caretVisible;
                InvalidateVisual();
            };
            _caretTimer.Start();

            _hoverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _hoverTimer.Tick += HoverTimer_Tick;
        }

        private void LoadSettings()
        {
            _fontSize = SettingsManager.Get<double>("editor.fontSize", 14.0);
            string fontFamily = SettingsManager.Get("editor.fontFamily");
            if (string.IsNullOrEmpty(fontFamily)) fontFamily = "Consolas";
            _typeface = new Typeface(fontFamily);
            LineHeight = _fontSize + 6.0;
            _charWidth = 0.0;

            bool wasVimEnabled = _vimEnabled;
            _vimEnabled = SettingsManager.Get<bool>("editor.vimEnabled", false);
            if (_vimEnabled != wasVimEnabled)
            {
                _vimMode = _vimEnabled ? VimMode.Normal : VimMode.Insert;
                VimModeChanged?.Invoke();
            }

            InvalidateVisual();
        }

        public void SetVimMode(VimMode mode)
        {
            if (!_vimEnabled) return;
            if (_vimMode != mode)
            {
                _vimMode = mode;
                InvalidateVisual();
                VimModeChanged?.Invoke();
            }
        }

        private void OnSettingChanged(string id)
        {
            if (id.StartsWith("editor.", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.UIThread.Post(() => LoadSettings());
            }
        }

        public void SetDiagnostics(System.Collections.Generic.IEnumerable<DiagnosticItem> items)
        {
            _diagnostics.Clear();
            _diagnostics.AddRange(items);
            InvalidateVisual();
        }

        private void HoverTimer_Tick(object? sender, EventArgs e)
        {
            _hoverTimer?.Stop();
            if (Document == null) return;

            double targetY = _lastMousePos.Y + ScrollY;
            double targetX = _lastMousePos.X + ScrollX;

            int visibleLineIndex = (int)(targetY / LineHeight);
            int lineIndex = 0;
            if (_visibleLines.Count > 0)
            {
                if (visibleLineIndex < 0 || visibleLineIndex >= _visibleLines.Count) return;
                lineIndex = _visibleLines[visibleLineIndex];
            }
            else
            {
                int lineCount = Document.GetLineCount();
                if (visibleLineIndex < 0 || visibleLineIndex >= lineCount) return;
                lineIndex = visibleLineIndex;
            }

            double gutterWidth = GetGutterWidth();
            int colIndex = (int)Math.Round((targetX - gutterWidth - 10) / CharWidth);
            colIndex = Math.Max(0, colIndex);

            long start = Document.GetLineStart(lineIndex);
            long end = (lineIndex + 1 < Document.GetLineCount()) ? Document.GetLineStart(lineIndex + 1) : Document.Length;
            int lineLen = (int)(end - start);

            var lineSpan = Document.GetLine(lineIndex, out _, out var rented);
            int printableLen = lineLen;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
            if (rented != null) ArrayPool<char>.Shared.Return(rented);

            if (colIndex >= 0 && colIndex <= printableLen)
            {
                int offset = (int)start + colIndex;
                HoverRequested?.Invoke(offset, _lastMousePos.X, _lastMousePos.Y);
            }
        }

        public int GetCaretAbsoluteOffset()
        {
            return CaretAbsoluteOffset;
        }

        public void MoveCaret(int line, int col)
        {
            if (Document == null) return;
            LogHelper.Log($"[TextEditorCanvas] MoveCaret: line={line}, col={col} (current CaretLine={CaretLine}, CaretCol={CaretCol})");
            int lineCount = Document.GetLineCount();
            CaretLine = Math.Max(0, Math.Min(line, lineCount - 1));

            // Find line length
            long start = Document.GetLineStart(CaretLine);
            long end = (CaretLine + 1 < lineCount) ? Document.GetLineStart(CaretLine + 1) : Document.Length;
            int lineLen = (int)(end - start);

            // Strip trailing newlines from column positioning
            if (lineLen > 0)
            {
                // We'll read the line to check if it ends with \n or \r\n
                var lineSpan = Document.GetLine(CaretLine, out var isCont, out var rented);
                int printableLen = lineLen;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                
                CaretCol = Math.Max(0, Math.Min(col, printableLen));
            }
            else
            {
                CaretCol = 0;
            }

            CaretAbsoluteOffset = (int)start + CaretCol;
            _caretVisible = true;
            CaretMoved?.Invoke();
            EnsureCaretVisible();
            InvalidateVisual();
            LogHelper.Log($"[TextEditorCanvas] MoveCaret finished: CaretLine={CaretLine}, CaretCol={CaretCol}, CaretAbsoluteOffset={CaretAbsoluteOffset}");
        }

        public void EnsureCaretVisible()
        {
            if (Document == null || Bounds.Height <= 0 || Bounds.Width <= 0) return;

            double dy = 0;
            int caretVisibleIndex = _docToVisualLine.Length > CaretLine ? _docToVisualLine[CaretLine] : CaretLine;
            double caretTopY = caretVisibleIndex * LineHeight;
            double caretBottomY = (caretVisibleIndex + 1) * LineHeight;

            if (caretTopY < ScrollY)
            {
                dy = caretTopY - ScrollY;
            }
            else if (caretBottomY > ScrollY + Bounds.Height)
            {
                dy = caretBottomY - Bounds.Height - ScrollY;
            }

            double dx = 0;
            double gutterWidth = GetGutterWidth();
            double caretLeftX = CaretCol * CharWidth;
            double caretRightX = (CaretCol + 1) * CharWidth;
            double textViewportWidth = Bounds.Width - gutterWidth - 20.0;

            if (textViewportWidth > 0)
            {
                if (caretLeftX < ScrollX)
                {
                    dx = caretLeftX - ScrollX;
                }
                else if (caretRightX > ScrollX + textViewportWidth)
                {
                    dx = caretRightX - textViewportWidth - ScrollX;
                }
            }

            if (dx != 0 || dy != 0)
            {
                ScrollRequested?.Invoke(dx, dy);
            }
        }

        public void MoveCaretToOffset(int absoluteOffset)
        {
            if (Document == null) return;
            LogHelper.Log($"[TextEditorCanvas] MoveCaretToOffset: absoluteOffset={absoluteOffset}");
            int lineCount = Document.GetLineCount();
            if (lineCount <= 0)
            {
                MoveCaret(0, 0);
                return;
            }

            int low = 0;
            int high = lineCount - 1;
            int targetLine = 0;

            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                long start = Document.GetLineStart(mid);
                if (start <= absoluteOffset)
                {
                    targetLine = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            long lineStart = Document.GetLineStart(targetLine);
            int col = absoluteOffset - (int)lineStart;
            MoveCaret(targetLine, col);
        }

        public void AdjustCaret(int editOffset, int addedLength, int deletedLength)
        {
            _selectionStartOffset = -1;
            _selectionEndOffset = -1;
            if (Document == null) return;
            UpdateLineStates(0);
            RecomputeFoldingRanges();
            UpdateFoldingLayout();

            int caretOffset = GetCaretAbsoluteOffset();
            if (addedLength > 0)
            {
                if (caretOffset >= editOffset)
                {
                    caretOffset += addedLength;
                }
            }
            else if (deletedLength > 0)
            {
                if (caretOffset > editOffset)
                {
                    int shift = Math.Min(caretOffset - editOffset, deletedLength);
                    caretOffset -= shift;
                }
            }
            MoveCaretToOffset(caretOffset);
            InvalidateVisual();
        }

        private (int Line, int Col) ClampToPrintable(int line, int col)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            int targetLine = Math.Max(0, Math.Min(line, lineCount - 1));
            long start = Document.GetLineStart(targetLine);
            long end = (targetLine + 1 < lineCount) ? Document.GetLineStart(targetLine + 1) : Document.Length;
            int lineLen = (int)(end - start);
            int clampedCol = 0;
            if (lineLen > 0)
            {
                var lineSpan = Document.GetLine(targetLine, out var isCont, out var rented);
                int printableLen = lineLen;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                clampedCol = Math.Max(0, Math.Min(col, printableLen));
            }
            return (targetLine, clampedCol);
        }

        private (int Line, int Col) GetNewCaretPosLeft(int line, int col, bool preventWrap = false)
        {
            if (Document == null) return (0, 0);
            if (col > 0)
            {
                return (line, col - 1);
            }
            
            if (preventWrap)
            {
                return (line, col);
            }

            if (_visibleLines.Count > 0)
            {
                int v = _docToVisualLine.Length > line ? _docToVisualLine[line] : 0;
                if (v > 0)
                {
                    int prevLine = _visibleLines[v - 1];
                    long start = Document.GetLineStart(prevLine);
                    long end = (prevLine + 1 < Document.GetLineCount()) ? Document.GetLineStart(prevLine + 1) : Document.Length;
                    int prevLen = (int)(end - start);
                    var lineSpan = Document.GetLine(prevLine, out _, out var rented);
                    int printableLen = prevLen;
                    if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                    if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                    if (rented != null) ArrayPool<char>.Shared.Return(rented);
                    return (prevLine, printableLen);
                }
            }
            else if (line > 0)
            {
                long start = Document.GetLineStart(line - 1);
                long end = Document.GetLineStart(line);
                int prevLen = (int)(end - start);
                var lineSpan = Document.GetLine(line - 1, out _, out var rented);
                int printableLen = prevLen;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                return (line - 1, printableLen);
            }
            return (line, col);
        }

        private (int Line, int Col) GetNewCaretPosRight(int line, int col, bool preventWrap = false)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            long start = Document.GetLineStart(line);
            long end = (line + 1 < lineCount) ? Document.GetLineStart(line + 1) : Document.Length;
            int lineLen = (int)(end - start);
            var lineSpan = Document.GetLine(line, out _, out var rented);
            int printableLen = lineLen;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
            if (rented != null) ArrayPool<char>.Shared.Return(rented);

            if (col < printableLen)
            {
                return (line, col + 1);
            }
            
            if (preventWrap)
            {
                return (line, col);
            }

            if (_visibleLines.Count > 0)
            {
                int v = _docToVisualLine.Length > line ? _docToVisualLine[line] : 0;
                if (v + 1 < _visibleLines.Count)
                {
                    return (_visibleLines[v + 1], 0);
                }
            }
            else if (line + 1 < lineCount)
            {
                return (line + 1, 0);
            }
            return (line, col);
        }

        private (int Line, int Col) GetNewCaretPosUp(int line, int col)
        {
            if (Document == null) return (0, 0);
            if (_visibleLines.Count > 0)
            {
                int v = _docToVisualLine.Length > line ? _docToVisualLine[line] : 0;
                if (v > 0)
                {
                    return ClampToPrintable(_visibleLines[v - 1], col);
                }
            }
            else if (line > 0)
            {
                return ClampToPrintable(line - 1, col);
            }
            return (line, col);
        }

        private (int Line, int Col) GetNewCaretPosDown(int line, int col)
        {
            if (Document == null) return (0, 0);
            if (_visibleLines.Count > 0)
            {
                int v = _docToVisualLine.Length > line ? _docToVisualLine[line] : 0;
                if (v + 1 < _visibleLines.Count)
                {
                    return ClampToPrintable(_visibleLines[v + 1], col);
                }
            }
            else
            {
                int lineCount = Document.GetLineCount();
                if (line + 1 < lineCount)
                {
                    return ClampToPrintable(line + 1, col);
                }
            }
            return (line, col);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DocumentProperty)
            {
                UpdateLineStates(0);
                RecomputeFoldingRanges();
                UpdateFoldingLayout();
            }
        }

        private void UpdateLineStates(int startLineIndex)
        {
            if (Document == null) return;
            int lineCount = Document.GetLineCount();
            
            while (_lineStates.Count < lineCount)
            {
                _lineStates.Add(LineState.Normal);
            }
            if (_lineStates.Count > lineCount)
            {
                _lineStates.RemoveRange(lineCount, _lineStates.Count - lineCount);
            }

            LineState currentState = startLineIndex > 0 ? _lineStates[startLineIndex - 1] : LineState.Normal;
            for (int i = startLineIndex; i < lineCount; i++)
            {
                var lineSpan = Document.GetLine(i, out var isCont, out var rented);
                int renderLen = lineSpan.Length;
                if (renderLen > 0 && lineSpan[renderLen - 1] == '\n') renderLen--;
                if (renderLen > 0 && lineSpan[renderLen - 1] == '\r') renderLen--;

                string extension = System.IO.Path.GetExtension(Document.FilePath ?? "");
                var nextState = DocumentLexer.ComputeEndState(lineSpan.Slice(0, renderLen), extension, currentState);
                if (rented != null) ArrayPool<char>.Shared.Return(rented);

                _lineStates[i] = nextState;
                currentState = nextState;
            }
        }

        public int GetVisibleLineCount()
        {
            if (Document == null) return 0;
            return _visibleLines.Count > 0 ? _visibleLines.Count : Document.GetLineCount();
        }

        public void SetFoldingRangesFromLsp(FoldingRangeItem[] items)
        {
            if (Document == null) return;
            var oldRanges = _foldingRanges.ToList();
            _foldingRanges.Clear();

            foreach (var item in items)
            {
                _foldingRanges.Add(new FoldingRange
                {
                    StartLine = item.StartLine,
                    EndLine = item.EndLine,
                    IsFolded = false
                });
            }

            RestoreFoldedStates(oldRanges);
            UpdateFoldingLayout();
            InvalidateVisual();
        }

        private void RestoreFoldedStates(System.Collections.Generic.List<FoldingRange> oldRanges)
        {
            foreach (var newRange in _foldingRanges)
            {
                var match = oldRanges.FirstOrDefault(r => r.StartLine == newRange.StartLine);
                if (match == null)
                {
                    string newStartLineText = GetTrimmedLineText(newRange.StartLine);
                    match = oldRanges.FirstOrDefault(r => Math.Abs(r.StartLine - newRange.StartLine) < 50
                        && GetTrimmedLineText(r.StartLine) == newStartLineText);
                }

                if (match != null)
                {
                    newRange.IsFolded = match.IsFolded;
                    oldRanges.Remove(match);
                }
            }
        }

        private void RecomputeFoldingRanges()
        {
            if (Document == null) return;
            int lineCount = Document.GetLineCount();

            var oldRanges = _foldingRanges.ToList();
            _foldingRanges.Clear();

            var braceStack = new System.Collections.Generic.Stack<int>();
            var regionStack = new System.Collections.Generic.Stack<int>();

            string extension = System.IO.Path.GetExtension(Document.FilePath ?? "");
            LineState lexerState = LineState.Normal;

            for (int i = 0; i < lineCount; i++)
            {
                var lineSpan = Document.GetLine(i, out _, out var rented);
                int len = lineSpan.Length;
                if (len > 0 && lineSpan[len - 1] == '\n') len--;
                if (len > 0 && lineSpan[len - 1] == '\r') len--;
                var cleanSpan = lineSpan.Slice(0, len);

                string lineStr = cleanSpan.ToString().Trim();
                if (lineStr.StartsWith("#region", StringComparison.OrdinalIgnoreCase))
                {
                    regionStack.Push(i);
                }
                else if (lineStr.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase))
                {
                    if (regionStack.Count > 0)
                    {
                        int start = regionStack.Pop();
                        if (i > start)
                        {
                            _foldingRanges.Add(new FoldingRange { StartLine = start, EndLine = i, IsFolded = false });
                        }
                    }
                }

                var lexer = new DocumentLexer(cleanSpan, extension, lexerState);
                while (lexer.NextToken(out var token, out var nextState))
                {
                    lexerState = nextState;
                    if (token.Type == TokenType.String || token.Type == TokenType.Comment)
                    {
                        continue;
                    }

                    var tokenText = cleanSpan.Slice(token.Start, token.Length);
                    for (int charIdx = 0; charIdx < tokenText.Length; charIdx++)
                    {
                        char c = tokenText[charIdx];
                        if (c == '{')
                        {
                            braceStack.Push(i);
                        }
                        else if (c == '}')
                        {
                            if (braceStack.Count > 0)
                            {
                                int start = braceStack.Pop();
                                if (i > start)
                                {
                                    _foldingRanges.Add(new FoldingRange { StartLine = start, EndLine = i, IsFolded = false });
                                }
                            }
                        }
                    }
                }

                if (rented != null) ArrayPool<char>.Shared.Return(rented);
            }

            RestoreFoldedStates(oldRanges);
        }

        public void UpdateFoldingLayout()
        {
            _visibleLines.Clear();
            if (Document == null)
            {
                _docToVisualLine = Array.Empty<int>();
                LayoutChanged?.Invoke();
                return;
            }

            int lineCount = Document.GetLineCount();
            if (_docToVisualLine.Length < lineCount)
            {
                _docToVisualLine = new int[lineCount * 2];
            }

            for (int i = 0; i < lineCount; i++)
            {
                if (IsLineVisible(i))
                {
                    _docToVisualLine[i] = _visibleLines.Count;
                    _visibleLines.Add(i);
                }
                else
                {
                    int headerLine = FindFoldingHeader(i);
                    _docToVisualLine[i] = headerLine >= 0 ? _docToVisualLine[headerLine] : 0;
                }
            }

            LayoutChanged?.Invoke();
            InvalidateVisual();
        }

        private bool IsLineVisible(int lineIndex)
        {
            foreach (var r in _foldingRanges)
            {
                if (r.IsFolded && lineIndex > r.StartLine && lineIndex <= r.EndLine)
                {
                    return false;
                }
            }
            return true;
        }

        private int FindFoldingHeader(int lineIndex)
        {
            int innermostStart = -1;
            foreach (var r in _foldingRanges)
            {
                if (r.IsFolded && lineIndex > r.StartLine && lineIndex <= r.EndLine)
                {
                    if (innermostStart == -1 || r.StartLine > innermostStart)
                    {
                        innermostStart = r.StartLine;
                    }
                }
            }
            return innermostStart;
        }

        private string GetTrimmedLineText(int lineIndex)
        {
            if (Document == null || lineIndex < 0 || lineIndex >= Document.GetLineCount()) return "";
            var span = Document.GetLine(lineIndex, out _, out var rented);
            string s = span.ToString().Trim();
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
            return s;
        }


        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var pos = e.GetPosition(this);
            double gutterWidth = GetGutterWidth();

            if (pos.X < gutterWidth)
            {
                int visibleLineIndex = (int)((pos.Y + ScrollY) / LineHeight);
                int clickedLine = 0;
                if (_visibleLines.Count > 0 && visibleLineIndex >= 0 && visibleLineIndex < _visibleLines.Count)
                {
                    clickedLine = _visibleLines[visibleLineIndex] + 1;
                }
                else
                {
                    clickedLine = visibleLineIndex + 1;
                }

                if (Document != null && clickedLine >= 1 && clickedLine <= Document.GetLineCount())
                {
                    if (pos.X >= gutterWidth - 15)
                    {
                        var range = _foldingRanges.FirstOrDefault(r => r.StartLine == clickedLine - 1);
                        if (range != null)
                        {
                            range.IsFolded = !range.IsFolded;
                            UpdateFoldingLayout();
                            InvalidateVisual();
                        }
                    }
                    else
                    {
                        ToggleBreakpoint(clickedLine);
                    }
                }
                e.Handled = true;
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                var (clickLine, clickCol) = GetLineColFromPointer(pos);
                MoveCaret(clickLine, clickCol);
                ShowContextMenu(e);
                e.Handled = true;
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                bool isAltPressed = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                LogHelper.Log($"[TextEditorCanvas] OnPointerPressed: IsLeftButtonPressed=true, isAltPressed={isAltPressed}");
                if (isAltPressed)
                {
                    _isBlockDragging = true;
                    var (clickLine, clickCol) = GetLineColFromPointer(pos);
                    _blockDragStartLine = clickLine;
                    _blockDragStartCol = clickCol;

                    _selectionStartOffset = -1;
                    _selectionEndOffset = -1;
                    _extraCarets.Clear();

                    MoveCaret(clickLine, clickCol);
                    e.Handled = true;
                }
                else
                {
                    _extraCarets.Clear();
                    int clickOffset = GetOffsetFromPointer(pos);

                    if (e.ClickCount == 3 && Document != null)
                    {
                        var (clickLine, _) = GetLineColFromPointer(pos);
                        SelectLineAt(clickLine);
                        _isDragging = true;
                    }
                    else if (e.ClickCount == 2 && Document != null)
                    {
                        var (clickLine, clickCol) = GetLineColFromPointer(pos);
                        SelectWordAt(clickLine, clickCol);
                        _isDragging = true;
                    }
                    else
                    {
                        _selectionStartOffset = clickOffset;
                        _selectionEndOffset = clickOffset;
                        _isDragging = true;
                        MoveCaretToOffset(clickOffset);
                    }
                    e.Handled = true;
                }
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            LogHelper.Log($"[TextEditorCanvas] OnKeyDown: key={e.Key}, modifiers={e.KeyModifiers}, caretLine={CaretLine}, caretCol={CaretCol}, extraCaretsCount={_extraCarets.Count}");

            if (_vimEnabled)
            {
                if (_vimMode == VimMode.Insert && e.Key == Key.Escape)
                {
                    if (CaretCol > 0)
                    {
                        MoveCaret(CaretLine, CaretCol - 1);
                    }
                    SetVimMode(VimMode.Normal);
                    e.Handled = true;
                    return;
                }

                if (_vimMode == VimMode.Normal)
                {
                    if (GhostText != null)
                    {
                        GhostText = null;
                        GhostTextOffset = -1;
                        InvalidateVisual();
                    }

                    bool handled = HandleVimNormalModeKey(e.Key, e.KeyModifiers);
                    if (handled)
                    {
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Back || e.Key == Key.Delete || e.Key == Key.Enter || e.Key == Key.Tab)
                    {
                        e.Handled = true;
                        return;
                    }
                }
            }

            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                LogHelper.Log($"[TextEditorCanvas] Handling Alt key in OnKeyDown to prevent menu focus steal");
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab && !string.IsNullOrEmpty(GhostText) && CaretAbsoluteOffset == GhostTextOffset)
            {
                LogHelper.Log($"[TextEditorCanvas] Intercepted Tab key to accept GhostText: '{GhostText}'");
                string acceptedText = GhostText;
                int offset = GhostTextOffset;
                GhostText = null;
                GhostTextOffset = -1;
                InvalidateVisual();
                TextInputReceived?.Invoke(offset, acceptedText);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && !string.IsNullOrEmpty(GhostText))
            {
                LogHelper.Log("[TextEditorCanvas] Intercepted Escape to clear GhostText");
                GhostText = null;
                GhostTextOffset = -1;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (e.Key != Key.LeftCtrl && e.Key != Key.RightCtrl &&
                e.Key != Key.LeftAlt && e.Key != Key.RightAlt &&
                e.Key != Key.LeftShift && e.Key != Key.RightShift)
            {
                if (GhostText != null)
                {
                    GhostText = null;
                    GhostTextOffset = -1;
                    InvalidateVisual();
                }
            }
            if (IsAutocompleteVisible)
            {
                if (e.Key == Key.Up)
                {
                    AutocompleteUpRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Down)
                {
                    AutocompleteDownRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    AutocompleteCommitRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
                else if (e.Key == Key.Escape)
                {
                    AutocompleteCancelRequested?.Invoke();
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
            {
                AutocompleteRequested?.Invoke(GetCaretAbsoluteOffset());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _extraCarets.Count > 0)
            {
                _extraCarets.Clear();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
            if (Document == null) return;

            int lineCount = Document.GetLineCount();
            bool shiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            int prevOffset = GetCaretAbsoluteOffset();
            bool isNavigationKey = e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right;

            if (isNavigationKey)
            {
                if (shiftPressed)
                {
                    if (_selectionStartOffset == -1)
                    {
                        _selectionStartOffset = prevOffset;
                    }
                }
                else
                {
                    _selectionStartOffset = -1;
                    _selectionEndOffset = -1;
                }
            }

            switch (e.Key)
            {
                case Key.F9:
                    ToggleBreakpoint(CaretLine + 1);
                    e.Handled = true;
                    break;
                case Key.F12:
                    if (e.KeyModifiers == KeyModifiers.Shift)
                    {
                        FindReferencesRequested?.Invoke(GetCaretAbsoluteOffset());
                    }
                    else
                    {
                        GotoDefinitionRequested?.Invoke(GetCaretAbsoluteOffset());
                    }
                    e.Handled = true;
                    break;
                case Key.F2:
                    RenameRequested?.Invoke(GetCaretAbsoluteOffset());
                    e.Handled = true;
                    break;
                case Key.Up:
                    {
                        if (_extraCarets.Count > 0)
                        {
                            for (int i = 0; i < _extraCarets.Count; i++)
                            {
                                var extra = _extraCarets[i];
                                var (nl, nc) = GetNewCaretPosUp(extra.Line, extra.Col);
                                long start = Document.GetLineStart(nl);
                                _extraCarets[i] = new ExtraCaret
                                {
                                    Line = nl,
                                    Col = nc,
                                    AbsoluteOffset = (int)start + nc
                                };
                            }
                        }
                        var (newLine, newCol) = GetNewCaretPosUp(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        DeduplicateExtraCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    {
                        if (_extraCarets.Count > 0)
                        {
                            for (int i = 0; i < _extraCarets.Count; i++)
                            {
                                var extra = _extraCarets[i];
                                var (nl, nc) = GetNewCaretPosDown(extra.Line, extra.Col);
                                long start = Document.GetLineStart(nl);
                                _extraCarets[i] = new ExtraCaret
                                {
                                    Line = nl,
                                    Col = nc,
                                    AbsoluteOffset = (int)start + nc
                                };
                            }
                        }
                        var (newLine, newCol) = GetNewCaretPosDown(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        DeduplicateExtraCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Left:
                    {
                        bool isMultiCaret = _extraCarets.Count > 0;
                        if (isMultiCaret)
                        {
                            for (int i = 0; i < _extraCarets.Count; i++)
                            {
                                var extra = _extraCarets[i];
                                var (nl, nc) = GetNewCaretPosLeft(extra.Line, extra.Col, preventWrap: true);
                                long start = Document.GetLineStart(nl);
                                _extraCarets[i] = new ExtraCaret
                                {
                                    Line = nl,
                                    Col = nc,
                                    AbsoluteOffset = (int)start + nc
                                };
                            }
                        }
                        var (newLine, newCol) = GetNewCaretPosLeft(CaretLine, CaretCol, preventWrap: isMultiCaret);
                        MoveCaret(newLine, newCol);
                        DeduplicateExtraCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Right:
                    {
                        bool isMultiCaret = _extraCarets.Count > 0;
                        if (isMultiCaret)
                        {
                            for (int i = 0; i < _extraCarets.Count; i++)
                            {
                                var extra = _extraCarets[i];
                                var (nl, nc) = GetNewCaretPosRight(extra.Line, extra.Col, preventWrap: true);
                                long start = Document.GetLineStart(nl);
                                _extraCarets[i] = new ExtraCaret
                                {
                                    Line = nl,
                                    Col = nc,
                                    AbsoluteOffset = (int)start + nc
                                };
                            }
                        }
                        var (newLine, newCol) = GetNewCaretPosRight(CaretLine, CaretCol, preventWrap: isMultiCaret);
                        MoveCaret(newLine, newCol);
                        DeduplicateExtraCarets();
                        e.Handled = true;
                    }
                    break;

                case Key.Back:
                    {
                        if (_extraCarets.Count > 0)
                        {
                            var editsList = new System.Collections.Generic.List<TextEdit>();
                            LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Back: generating batch edits for {_extraCarets.Count + 1} carets");
                            int mainOffset = GetCaretAbsoluteOffset();
                            if (mainOffset > 0)
                            {
                                editsList.Add(new TextEdit { Offset = mainOffset - 1, DeleteLength = 1, Text = "" });
                            }
                            foreach (var extra in _extraCarets)
                            {
                                if (extra.AbsoluteOffset > 0)
                                {
                                    editsList.Add(new TextEdit { Offset = extra.AbsoluteOffset - 1, DeleteLength = 1, Text = "" });
                                }
                            }
                            if (editsList.Count > 0)
                            {
                                LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Back: Invoking BatchEditReceived with {editsList.Count} edits");
                                BatchEditReceived?.Invoke(editsList.ToArray());
                            }
                        }
                        else
                        {
                            int offset = GetCaretAbsoluteOffset();
                            if (offset > 0)
                            {
                                TextDeleteReceived?.Invoke(offset - 1, 1);
                            }
                        }
                        e.Handled = true;
                    }
                    break;

                case Key.Delete:
                    {
                        if (_extraCarets.Count > 0)
                        {
                            var editsList = new System.Collections.Generic.List<TextEdit>();
                            LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Delete: generating batch edits for {_extraCarets.Count + 1} carets");
                            int mainOffset = GetCaretAbsoluteOffset();
                            if (mainOffset < Document.Length)
                            {
                                editsList.Add(new TextEdit { Offset = mainOffset, DeleteLength = 1, Text = "" });
                            }
                            foreach (var extra in _extraCarets)
                            {
                                if (extra.AbsoluteOffset < Document.Length)
                                {
                                    editsList.Add(new TextEdit { Offset = extra.AbsoluteOffset, DeleteLength = 1, Text = "" });
                                }
                            }
                            if (editsList.Count > 0)
                            {
                                LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Delete: Invoking BatchEditReceived with {editsList.Count} edits");
                                BatchEditReceived?.Invoke(editsList.ToArray());
                            }
                        }
                        else
                        {
                            int offset = GetCaretAbsoluteOffset();
                            if (offset < Document.Length)
                            {
                                TextDeleteReceived?.Invoke(offset, 1);
                            }
                        }
                        e.Handled = true;
                    }
                    break;

                case Key.Enter:
                    {
                        if (_extraCarets.Count > 0)
                        {
                            var editsList = new System.Collections.Generic.List<TextEdit>();
                            LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Enter: generating batch edits for {_extraCarets.Count + 1} carets");
                            int mainOffset = GetCaretAbsoluteOffset();
                            string mainWS = GetLeadingWhitespace(CaretLine, CaretCol);
                            editsList.Add(new TextEdit { Offset = mainOffset, DeleteLength = 0, Text = "\n" + mainWS });
                            foreach (var extra in _extraCarets)
                            {
                                string extraWS = GetLeadingWhitespace(extra.Line, extra.Col);
                                editsList.Add(new TextEdit { Offset = extra.AbsoluteOffset, DeleteLength = 0, Text = "\n" + extraWS });
                            }
                            LogHelper.Log($"[TextEditorCanvas] OnKeyDown Key.Enter: Invoking BatchEditReceived with {editsList.Count} edits");
                            BatchEditReceived?.Invoke(editsList.ToArray());
                        }
                        else
                        {
                            int offset = GetCaretAbsoluteOffset();
                            string mainWS = GetLeadingWhitespace(CaretLine, CaretCol);
                            TextInputReceived?.Invoke(offset, "\n" + mainWS);
                        }
                        e.Handled = true;
                    }
                    break;
            }

            if (isNavigationKey && shiftPressed)
            {
                _selectionEndOffset = GetCaretAbsoluteOffset();
                InvalidateVisual();
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            LogHelper.Log($"[TextEditorCanvas] OnKeyUp: key={e.Key}, modifiers={e.KeyModifiers}");
            if (e.Key == Key.LeftAlt || e.Key == Key.RightAlt)
            {
                LogHelper.Log($"[TextEditorCanvas] Handling Alt key in OnKeyUp to prevent menu focus steal");
                e.Handled = true;
                return;
            }
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            LogHelper.Log($"[TextEditorCanvas] LostFocus! FocusedElement is now: {focused?.GetType().Name} (HashCode: {focused?.GetHashCode()})");
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            if (_vimEnabled && _vimMode == VimMode.Normal)
            {
                e.Handled = true;
                return;
            }
            base.OnTextInput(e);
            LogHelper.Log($"[TextEditorCanvas] OnTextInput: text='{e.Text}', mainCaretOffset={GetCaretAbsoluteOffset()}, extraCaretsCount={_extraCarets.Count}");
            if (GhostText != null)
            {
                GhostText = null;
                GhostTextOffset = -1;
                InvalidateVisual();
            }
            if (Document == null || string.IsNullOrEmpty(e.Text) || e.Text == "\r" || e.Text == "\n")
                return;

            if (_extraCarets.Count > 0)
            {
                var editsList = new System.Collections.Generic.List<TextEdit>();
                int mainOffset = GetCaretAbsoluteOffset();
                editsList.Add(new TextEdit { Offset = mainOffset, DeleteLength = 0, Text = e.Text });
                foreach (var extra in _extraCarets)
                {
                    editsList.Add(new TextEdit { Offset = extra.AbsoluteOffset, DeleteLength = 0, Text = e.Text });
                }
                LogHelper.Log($"[TextEditorCanvas] OnTextInput: Invoking BatchEditReceived with {editsList.Count} edits");
                BatchEditReceived?.Invoke(editsList.ToArray());
            }
            else
            {
                int offset = GetCaretAbsoluteOffset();
                LogHelper.Log($"[TextEditorCanvas] OnTextInput: Invoking TextInputReceived with offset={offset}, text='{e.Text}'");
                TextInputReceived?.Invoke(offset, e.Text);
            }
            e.Handled = true;

            if (e.Text == ".")
            {
                Dispatcher.UIThread.Post(() => AutocompleteRequested?.Invoke(GetCaretAbsoluteOffset()));
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            double deltaY = -e.Delta.Y * LineHeight * 2;
            double deltaX = -e.Delta.X * CharWidth * 4;
            ScrollRequested?.Invoke(deltaX, deltaY);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            _lastMousePos = e.GetPosition(this);
            _hoverTimer?.Stop();
            _hoverTimer?.Start();
            MouseMovedOrLeft?.Invoke();

            if (Document == null) return;

            if (_isDragging)
            {
                int dragOffset = GetOffsetFromPointer(_lastMousePos);
                MoveCaretToOffset(dragOffset);
                _selectionEndOffset = dragOffset;
                InvalidateVisual();
            }
            else if (_isBlockDragging)
            {
                var (currentLine, currentCol) = GetLineColFromPointer(_lastMousePos);
                LogHelper.Log($"[TextEditorCanvas] OnPointerMoved block dragging: startLine={_blockDragStartLine}, currentLine={currentLine}, currentCol={currentCol}");
                _extraCarets.Clear();
                int start = Math.Min(_blockDragStartLine, currentLine);
                int end = Math.Max(_blockDragStartLine, currentLine);
                
                for (int line = start; line <= end; line++)
                {
                    if (line == currentLine)
                        continue;
                    
                    var (_, extraCol) = ClampToPrintable(line, currentCol);
                    long lineStart = Document.GetLineStart(line);
                    _extraCarets.Add(new ExtraCaret
                    {
                        Line = line,
                        Col = extraCol,
                        AbsoluteOffset = (int)lineStart + extraCol
                    });
                }
                
                MoveCaret(currentLine, currentCol);
                LogHelper.Log($"[TextEditorCanvas] OnPointerMoved block dragging: CaretLine={CaretLine}, CaretCol={CaretCol}, extraCaretsCount={_extraCarets.Count}");
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverTimer?.Stop();
            MouseMovedOrLeft?.Invoke();
        }

        private IBrush? GetTokenBrush(TokenType type)
        {
            return type switch
            {
                TokenType.Keyword => KeywordBrush,
                TokenType.Comment => CommentBrush,
                TokenType.String => StringBrush,
                TokenType.Number => NumberBrush,
                TokenType.Type => TypeBrush,
                TokenType.Method => MethodBrush,
                TokenType.Preprocessor => PreprocessorBrush,
                TokenType.Attribute => AttributeBrush,
                TokenType.Tag => TagBrush,
                TokenType.Selector => SelectorBrush,
                TokenType.Property => PropertyBrush,
                TokenType.Heading => HeadingBrush,
                _ => null
            };
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // Background
            context.FillRectangle(Brushes.Black, Bounds);

            var doc = Document;
            if (doc == null)
                return;

            double width = Bounds.Width;
            double height = Bounds.Height;

            int lineCount = doc!.GetLineCount();
            int visibleLineCount = _visibleLines.Count > 0 ? _visibleLines.Count : lineCount;
            double gutterWidth = GetGutterWidth();

            // Calculate visible line range
            int startVisibleIndex = (int)(ScrollY / LineHeight);
            int endVisibleIndex = (int)((ScrollY + height) / LineHeight) + 1;

            startVisibleIndex = Math.Max(0, Math.Min(startVisibleIndex, visibleLineCount - 1));
            endVisibleIndex = Math.Max(0, Math.Min(endVisibleIndex, visibleLineCount - 1));

            // Render text
            for (int v = startVisibleIndex; v <= endVisibleIndex; v++)
            {
                int i = _visibleLines.Count > 0 ? _visibleLines[v] : v;
                double yOffset = (v * LineHeight) - ScrollY;
                if (DebugActiveLine == i + 1)
                {
                    context.FillRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 0)), new Rect(gutterWidth, yOffset, Bounds.Width - gutterWidth, LineHeight));
                }

                // Draw CodeLens if exists
                if (_codeLensItems.TryGetValue(i + 1, out var lensText))
                {
                    var formattedLens = new FormattedText(
                        lensText,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        10.0,
                        new SolidColorBrush(Color.Parse("#858585"))
                    );
                    context.DrawText(formattedLens, new Point(gutterWidth + 10 - ScrollX, yOffset - 12));
                }

                // Draw Inlay Hints for this line
                var lineHints = new List<(int Col, string Label)>();
                foreach (var hint in _inlayHints)
                {
                    var (hLine, hCol) = GetLineColFromOffset(hint.Offset);
                    if (hLine == i)
                    {
                        lineHints.Add((hCol, hint.Label));
                    }
                }
                
                foreach (var hint in lineHints)
                {
                    double hintX = gutterWidth + 10 + hint.Col * CharWidth - ScrollX;
                    var formattedHint = new FormattedText(
                        hint.Label,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        9.0,
                        new SolidColorBrush(Color.Parse("#519ABA")) // Nice plugin blue
                    );
                    
                    double badgeW = formattedHint.Width + 4;
                    double badgeH = 12;
                    context.FillRectangle(
                        new SolidColorBrush(Color.FromArgb(40, 81, 154, 186)),
                        new Rect(hintX, yOffset - 8, badgeW, badgeH),
                        2.0f
                    );
                    context.DrawText(formattedHint, new Point(hintX + 2, yOffset - 8));
                }

                var lineSpan = doc!.GetLine(i, out bool isContiguous, out var rented);

                // Strip trailing CR/LF for rendering
                int renderLen = lineSpan.Length;
                if (renderLen > 0 && lineSpan[renderLen - 1] == '\n') renderLen--;
                if (renderLen > 0 && lineSpan[renderLen - 1] == '\r') renderLen--;

                // --- Draw Selection Highlight ---
                int minOffset = Math.Min(_selectionStartOffset, _selectionEndOffset);
                int maxOffset = Math.Max(_selectionStartOffset, _selectionEndOffset);
                if (minOffset != -1 && maxOffset != -1 && minOffset != maxOffset)
                {
                    long lineStartOffset = doc.GetLineStart(i);
                    long lineEndOffset = (i + 1 < lineCount) ? doc.GetLineStart(i + 1) : doc.Length;
                    if (lineStartOffset < maxOffset && lineEndOffset > minOffset)
                    {
                        int selStartInLine = Math.Max((int)lineStartOffset, minOffset) - (int)lineStartOffset;
                        int selEndInLine = Math.Min((int)lineEndOffset, maxOffset) - (int)lineStartOffset;

                        int startCol = Math.Min(renderLen, selStartInLine);
                        int endCol = Math.Min(renderLen, selEndInLine);

                        double startX = gutterWidth + 10 + startCol * CharWidth - ScrollX;
                        double endX = gutterWidth + 10 + endCol * CharWidth - ScrollX;

                        if (selEndInLine > renderLen && i + 1 < lineCount)
                        {
                            endX += CharWidth; // extend to show selected newline
                        }

                        if (endX > startX)
                        {
                            double y = (v * LineHeight) - ScrollY;
                            context.FillRectangle(new SolidColorBrush(Color.Parse("#264F78")), new Rect(startX, y, endX - startX, LineHeight));
                        }
                    }
                }

                bool hasGhost = i == CaretLine && !string.IsNullOrEmpty(GhostText) && CaretAbsoluteOffset == GhostTextOffset;
                if (renderLen > 0 || hasGhost)
                {
                    string textStr = renderLen > 0 ? lineSpan.Slice(0, renderLen).ToString() : "";
                    string renderText = textStr;
                    int ghostIndex = 0;
                    if (hasGhost)
                    {
                        int caretColClamped = Math.Max(0, Math.Min(CaretCol, textStr.Length));
                        renderText = textStr.Substring(0, caretColClamped) + GhostText + textStr.Substring(caretColClamped);
                        ghostIndex = caretColClamped;
                    }

                    var formatted = new FormattedText(
                        renderText,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        Brushes.LightGray
                    );

                    if (renderLen > 0)
                    {
                        LineState startState = i > 0 && (i - 1) < _lineStates.Count ? _lineStates[i - 1] : LineState.Normal;
                        string extension = System.IO.Path.GetExtension(doc.FilePath ?? "");
                        var lexer = new DocumentLexer(lineSpan.Slice(0, renderLen), extension, startState);
                        while (lexer.NextToken(out var token, out var nextState))
                        {
                            var brush = GetTokenBrush(token.Type);
                            if (brush != null)
                            {
                                if (hasGhost && token.Start >= ghostIndex)
                                {
                                    formatted.SetForegroundBrush(brush, token.Start + GhostText!.Length, token.Length);
                                }
                                else
                                {
                                    formatted.SetForegroundBrush(brush, token.Start, token.Length);
                                }
                            }
                        }
                    }

                    if (hasGhost)
                    {
                        formatted.SetForegroundBrush(new SolidColorBrush(Color.Parse("#707070")), ghostIndex, GhostText!.Length);
                    }

                    // Offset text by gutter width
                    context.DrawText(formatted, new Point(gutterWidth + 10 - ScrollX, yOffset));
                }

                // Draw squiggles for this line
                if (doc != null)
                {
                    long lineStart = doc!.GetLineStart(i);
                    long lineEnd = (i + 1 < lineCount) ? doc!.GetLineStart(i + 1) : doc!.Length;
                    foreach (var diag in _diagnostics)
                    {
                        if (diag.StartOffset < lineEnd && diag.EndOffset >= lineStart)
                        {
                            int startCol = Math.Max(0, diag.StartOffset - (int)lineStart);
                            int endCol = Math.Min(renderLen, diag.EndOffset - (int)lineStart);
                            if (startCol < endCol)
                            {
                                double startX = gutterWidth + 10 + startCol * CharWidth - ScrollX;
                                double endX = gutterWidth + 10 + endCol * CharWidth - ScrollX;
                                double y = (v * LineHeight) - ScrollY + LineHeight - 2;
                                IBrush brush = diag.Severity == 1 ? Brushes.Red : new SolidColorBrush(Color.Parse("#FFC800"));
                                DrawSquiggle(context, startX, endX, y, brush);
                            }
                        }
                    }
                }

                if (rented != null)
                {
                    ArrayPool<char>.Shared.Return(rented);
                }
            }

            // Render Caret
            if (_caretVisible)
            {
                var pen = new Pen(Brushes.LightGray, CaretThickness);
                var blockBrush = new SolidColorBrush(Color.FromArgb(120, 211, 211, 211));
                int caretVisibleIndex = _docToVisualLine.Length > CaretLine ? _docToVisualLine[CaretLine] : CaretLine;
                if (caretVisibleIndex >= startVisibleIndex && caretVisibleIndex <= endVisibleIndex)
                {
                    double caretX = gutterWidth + 10 + (CaretCol * CharWidth) - ScrollX;
                    double caretY = (caretVisibleIndex * LineHeight) - ScrollY;
                    if (_vimEnabled && _vimMode == VimMode.Normal)
                    {
                        context.FillRectangle(blockBrush, new Rect(caretX, caretY + 2, CharWidth, LineHeight - 4));
                    }
                    else
                    {
                        context.DrawLine(pen, new Point(caretX, caretY + 2), new Point(caretX, caretY + LineHeight - 2));
                    }
                }
                foreach (var extra in _extraCarets)
                {
                    int extraVisibleIndex = _docToVisualLine.Length > extra.Line ? _docToVisualLine[extra.Line] : extra.Line;
                    if (extraVisibleIndex >= startVisibleIndex && extraVisibleIndex <= endVisibleIndex)
                    {
                        double caretX = gutterWidth + 10 + (extra.Col * CharWidth) - ScrollX;
                        double caretY = (extraVisibleIndex * LineHeight) - ScrollY;
                        if (_vimEnabled && _vimMode == VimMode.Normal)
                        {
                            context.FillRectangle(blockBrush, new Rect(caretX, caretY + 2, CharWidth, LineHeight - 4));
                        }
                        else
                        {
                            context.DrawLine(pen, new Point(caretX, caretY + 2), new Point(caretX, caretY + LineHeight - 2));
                        }
                    }
                }
            }

            // Render Gutter (drawn after text so it acts as an overlay mask when scrolling horizontally)
            if (gutterWidth > 0)
            {
                // Gutter background
                context.FillRectangle(new SolidColorBrush(Color.Parse("#1A1A1A")), new Rect(0, 0, gutterWidth, Bounds.Height));

                // Gutter separator line
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#2D2D2D")), 1.0), new Point(gutterWidth, 0), new Point(gutterWidth, Bounds.Height));

                // Render line numbers inside gutter
                for (int v = startVisibleIndex; v <= endVisibleIndex; v++)
                {
                    int i = _visibleLines.Count > 0 ? _visibleLines[v] : v;
                    double yOffset = (v * LineHeight) - ScrollY;

                    // Draw Git diff line indicator on the leftmost edge of the gutter
                    if (_gitLineChanges.TryGetValue(i + 1, out var gitStatus))
                    {
                        IBrush gitBrush = gitStatus switch
                        {
                            GitLineChangeType.Added => new SolidColorBrush(Color.Parse("#23D160")),
                            GitLineChangeType.Modified => new SolidColorBrush(Color.Parse("#209CEE")),
                            GitLineChangeType.Deleted => new SolidColorBrush(Color.Parse("#FF3860")),
                            _ => Brushes.Transparent
                        };

                        if (gitStatus == GitLineChangeType.Deleted)
                        {
                            context.FillRectangle(gitBrush, new Rect(0, yOffset, 3, 3));
                        }
                        else
                        {
                            context.FillRectangle(gitBrush, new Rect(0, yOffset, 3, LineHeight));
                        }
                    }

                    // Draw Test Coverage indicator on the right edge of the gutter
                    if (_lineCoverage.TryGetValue(i + 1, out var isCovered))
                    {
                        IBrush covBrush = isCovered ? new SolidColorBrush(Color.Parse("#23D160")) : new SolidColorBrush(Color.Parse("#FF3860"));
                        context.FillRectangle(covBrush, new Rect(gutterWidth - 2, yOffset, 2, LineHeight));
                    }

                    // Draw Breakpoint dot
                    if (_breakpoints.Contains(i + 1))
                    {
                        context.DrawGeometry(Brushes.Red, null, new EllipseGeometry(new Rect(5, yOffset + (LineHeight - 10) / 2.0, 10, 10)));
                    }

                    // Draw Debug active line arrow
                    if (DebugActiveLine == i + 1)
                    {
                        var pg = new PathGeometry();
                        var fig = new PathFigure { StartPoint = new Point(4, yOffset + 4), IsClosed = true };
                        fig.Segments ??= new PathSegments();
                        fig.Segments.Add(new LineSegment { Point = new Point(14, yOffset + LineHeight / 2.0) });
                        fig.Segments.Add(new LineSegment { Point = new Point(4, yOffset + LineHeight - 4) });
                        pg.Figures ??= new PathFigures();
                        pg.Figures.Add(fig);
                        context.DrawGeometry(Brushes.Yellow, null, pg);
                    }

                    // Draw Folding Toggle
                    var range = _foldingRanges.FirstOrDefault(r => r.StartLine == i);
                    if (range != null)
                    {
                        string toggleStr = range.IsFolded ? "▸" : "▾";
                        var formattedToggle = new FormattedText(
                            toggleStr,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            _typeface,
                            _fontSize - 1.0,
                            new SolidColorBrush(Color.Parse("#858585"))
                        );
                        context.DrawText(formattedToggle, new Point(gutterWidth - 12.0, yOffset + 1));
                    }

                    string lineNumStr = (i + 1).ToString();
                    var formattedNum = new FormattedText(
                        lineNumStr,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize - 1.0,
                        new SolidColorBrush(Color.Parse("#858585"))
                    );
                    double xOffset = gutterWidth - 18.0 - formattedNum.Width;
                    context.DrawText(formattedNum, new Point(xOffset, yOffset + 1));
                }
            }
        }

        private void DrawSquiggle(DrawingContext context, double startX, double endX, double y, IBrush brush)
        {
            var pen = new Pen(brush, 1.0);
            double x = startX;
            bool up = true;
            while (x < endX)
            {
                double nextX = Math.Min(x + 2.0, endX);
                double nextY = y + (up ? -1.5 : 1.5);
                context.DrawLine(pen, new Point(x, y + (up ? 1.5 : -1.5)), new Point(nextX, nextY));
                x = nextX;
                up = !up;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_isDragging)
            {
                _isDragging = false;
                if (_selectionStartOffset == _selectionEndOffset)
                {
                    _selectionStartOffset = -1;
                    _selectionEndOffset = -1;
                }
                InvalidateVisual();
            }
            if (_isBlockDragging)
            {
                LogHelper.Log($"[TextEditorCanvas] OnPointerReleased: _isBlockDragging was true. Final extraCaretsCount={_extraCarets.Count}");
                _isBlockDragging = false;
                InvalidateVisual();
            }
        }

        private (int Line, int Col) GetLineColFromPointer(Point pos)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            
            double targetY = pos.Y + ScrollY;
            double targetX = pos.X + ScrollX;

            int visibleLineIndex = (int)(targetY / LineHeight);
            int lineIndex = 0;
            if (_visibleLines.Count > 0)
            {
                visibleLineIndex = Math.Max(0, Math.Min(visibleLineIndex, _visibleLines.Count - 1));
                lineIndex = _visibleLines[visibleLineIndex];
            }
            else
            {
                lineIndex = Math.Max(0, Math.Min(visibleLineIndex, lineCount - 1));
            }

            double gutterWidth = GetGutterWidth();
            int colIndex = (int)Math.Round((targetX - gutterWidth - 10) / CharWidth);
            colIndex = Math.Max(0, colIndex);

            return ClampToPrintable(lineIndex, colIndex);
        }

        private int GetOffsetFromPointer(Point pos)
        {
            if (Document == null) return 0;
            int lineCount = Document.GetLineCount();
            
            double targetY = pos.Y + ScrollY;
            double targetX = pos.X + ScrollX;

            int visibleLineIndex = (int)(targetY / LineHeight);
            int lineIndex = 0;
            if (_visibleLines.Count > 0)
            {
                visibleLineIndex = Math.Max(0, Math.Min(visibleLineIndex, _visibleLines.Count - 1));
                lineIndex = _visibleLines[visibleLineIndex];
            }
            else
            {
                lineIndex = Math.Max(0, Math.Min(visibleLineIndex, lineCount - 1));
            }

            double gutterWidth = GetGutterWidth();
            int colIndex = (int)Math.Round((targetX - gutterWidth - 10) / CharWidth);
            colIndex = Math.Max(0, colIndex);

            long start = Document.GetLineStart(lineIndex);
            long end = (lineIndex + 1 < lineCount) ? Document.GetLineStart(lineIndex + 1) : Document.Length;
            int lineLen = (int)(end - start);
            var lineSpan = Document.GetLine(lineIndex, out _, out var rented);
            int printableLen = lineLen;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
            if (rented != null) ArrayPool<char>.Shared.Return(rented);

            colIndex = Math.Min(colIndex, printableLen);
            return (int)start + colIndex;
        }

        public bool HasSelection(out int start, out int len)
        {
            start = 0;
            len = 0;
            int min = Math.Min(_selectionStartOffset, _selectionEndOffset);
            int max = Math.Max(_selectionStartOffset, _selectionEndOffset);
            if (min != -1 && max != -1 && min != max)
            {
                start = min;
                len = max - min;
                return true;
            }
            return false;
        }

        public string GetSelectedText(out int startOffset, out int length)
        {
            startOffset = 0;
            length = 0;
            if (Document == null) return "";

            int min = Math.Min(_selectionStartOffset, _selectionEndOffset);
            int max = Math.Max(_selectionStartOffset, _selectionEndOffset);

            if (min != -1 && max != -1 && min != max)
            {
                startOffset = min;
                length = max - min;
                return GetTextFromDocument(min, length);
            }

            // Fallback: Current line
            int line = CaretLine;
            int lineCount = Document.GetLineCount();
            long start = Document.GetLineStart(line);
            long end = (line + 1 < lineCount) ? Document.GetLineStart(line + 1) : Document.Length;
            
            startOffset = (int)start;
            length = (int)(end - start);
            
            if (length <= 0) return "";
            
            return GetTextFromDocument((int)start, length);
        }

        private string GetTextFromDocument(int offset, int len)
        {
            if (Document == null || len <= 0) return "";
            
            var sb = new System.Text.StringBuilder(len);
            int lineCount = Document.GetLineCount();
            int remaining = len;
            int currentOffset = offset;

            for (int i = 0; i < lineCount && remaining > 0; i++)
            {
                long lineStart = Document.GetLineStart(i);
                long lineEnd = (i + 1 < lineCount) ? Document.GetLineStart(i + 1) : Document.Length;

                if (lineStart < currentOffset + remaining && lineEnd > currentOffset)
                {
                    int selStartInLine = Math.Max((int)lineStart, currentOffset) - (int)lineStart;
                    int selEndInLine = Math.Min((int)lineEnd, currentOffset + remaining) - (int)lineStart;

                    int chunkLen = selEndInLine - selStartInLine;
                    if (chunkLen > 0)
                    {
                        var lineSpan = Document.GetLine(i, out _, out var rented);
                        sb.Append(lineSpan.Slice(selStartInLine, chunkLen).ToString());
                        if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);

                        remaining -= chunkLen;
                        currentOffset += chunkLen;
                    }
                }
            }

            return sb.ToString();
        }

        public void AdjustCaretsForBatch(TextEdit[] edits)
        {
            _selectionStartOffset = -1;
            _selectionEndOffset = -1;
            if (Document == null) return;
            LogHelper.Log($"[TextEditorCanvas] AdjustCaretsForBatch: editsCount={edits.Length}");
            foreach (var edit in edits)
            {
                LogHelper.Log($"[TextEditorCanvas] AdjustCaretsForBatch edit: offset={edit.Offset}, deleteLen={edit.DeleteLength}, text='{edit.Text}'");
            }
            UpdateLineStates(0);
            RecomputeFoldingRanges();
            UpdateFoldingLayout();

            int mainOffset = GetCaretAbsoluteOffset();
            LogHelper.Log($"[TextEditorCanvas] AdjustCaretsForBatch: beforeMainOffset={mainOffset}, extraCaretsCount={_extraCarets.Count}");
            mainOffset = AdjustOffset(mainOffset, edits);
            MoveCaretToOffset(mainOffset);

            for (int i = 0; i < _extraCarets.Count; i++)
            {
                var extra = _extraCarets[i];
                int newOffset = AdjustOffset(extra.AbsoluteOffset, edits);
                var (l, c) = GetLineColFromOffset(newOffset);
                _extraCarets[i] = new ExtraCaret
                {
                    Line = l,
                    Col = c,
                    AbsoluteOffset = newOffset
                };
            }
            LogHelper.Log($"[TextEditorCanvas] AdjustCaretsForBatch: afterMainOffset={GetCaretAbsoluteOffset()}, extraCaretsCount={_extraCarets.Count}");

            InvalidateVisual();
        }

        private int AdjustOffset(int originalOffset, TextEdit[] edits)
        {
            int offset = originalOffset;
            foreach (var edit in edits)
            {
                if (!string.IsNullOrEmpty(edit.Text))
                {
                    if (edit.Offset <= originalOffset)
                    {
                        offset += edit.Text.Length;
                    }
                }
                if (edit.DeleteLength > 0)
                {
                    if (edit.Offset < originalOffset)
                    {
                        int shift = Math.Min(originalOffset - edit.Offset, edit.DeleteLength);
                        offset -= shift;
                    }
                }
            }
            return offset;
        }

        private void DeduplicateExtraCarets()
        {
            if (_extraCarets.Count == 0) return;

            var seenLines = new System.Collections.Generic.HashSet<int> { CaretLine };
            var uniqueCarets = new System.Collections.Generic.List<ExtraCaret>();

            foreach (var extra in _extraCarets)
            {
                if (!seenLines.Contains(extra.Line))
                {
                    seenLines.Add(extra.Line);
                    uniqueCarets.Add(extra);
                }
            }

            _extraCarets.Clear();
            _extraCarets.AddRange(uniqueCarets);
        }

        private string GetLeadingWhitespace(int lineIndex, int caretCol)
        {
            if (Document == null) return "";
            var lineSpan = Document.GetLine(lineIndex, out _, out var rented);
            int len = 0;
            while (len < lineSpan.Length && len < caretCol && (lineSpan[len] == ' ' || lineSpan[len] == '\t'))
            {
                len++;
            }
            string ws = new string(lineSpan.Slice(0, len));
            if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
            return ws;
        }

        private int GetLineLength(int lineIndex)
        {
            if (Document == null) return 0;
            long start = Document.GetLineStart(lineIndex);
            long end = (lineIndex + 1 < Document.GetLineCount()) ? Document.GetLineStart(lineIndex + 1) : Document.Length;
            int len = (int)(end - start);
            var lineSpan = Document.GetLine(lineIndex, out _, out var rented);
            int printableLen = len;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
            return printableLen;
        }

        private void VimMoveWord(bool forward)
        {
            if (Document == null) return;
            int offset = GetCaretAbsoluteOffset();
            if (forward)
            {
                if (offset >= Document.Length) return;
                int lookahead = Math.Min(200, Document.Length - offset);
                if (lookahead <= 0) return;
                string text = Document.GetTextRange(offset, lookahead);
                
                int i = 0;
                if (i < text.Length)
                {
                    char startChar = text[i];
                    bool startWordClass = char.IsLetterOrDigit(startChar);
                    while (i < text.Length && char.IsLetterOrDigit(text[i]) == startWordClass && !char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }
                }
                if (i > 0)
                {
                    MoveCaretToOffset(offset + i);
                }
            }
            else
            {
                if (offset <= 0) return;
                int lookbehind = Math.Min(200, offset);
                string text = Document.GetTextRange(offset - lookbehind, lookbehind);
                
                int i = text.Length - 1;
                while (i >= 0 && char.IsWhiteSpace(text[i]))
                {
                    i--;
                }
                if (i >= 0)
                {
                    char targetChar = text[i];
                    bool targetWordClass = char.IsLetterOrDigit(targetChar);
                    while (i >= 0 && char.IsLetterOrDigit(text[i]) == targetWordClass && !char.IsWhiteSpace(text[i]))
                    {
                        i--;
                    }
                    i++;
                }
                else
                {
                    i = 0;
                }
                int newOffset = offset - lookbehind + i;
                if (newOffset != offset)
                {
                    MoveCaretToOffset(newOffset);
                }
            }
        }

        private void VimDeleteCurrentLine()
        {
            if (Document == null) return;
            int lineCount = Document.GetLineCount();
            if (lineCount <= 0) return;
            
            long start = Document.GetLineStart(CaretLine);
            long end;
            if (CaretLine + 1 < lineCount)
            {
                end = Document.GetLineStart(CaretLine + 1);
            }
            else
            {
                end = Document.Length;
                if (start > 0)
                {
                    start--;
                }
            }
            
            int len = (int)(end - start);
            if (len > 0)
            {
                TextDeleteReceived?.Invoke((int)start, len);
                int newLine = Math.Max(0, Math.Min(CaretLine, Document.GetLineCount() - 1));
                MoveCaret(newLine, 0);
            }
        }

        private bool HandleVimNormalModeKey(Key key, KeyModifiers modifiers)
        {
            bool shiftPressed = (modifiers & KeyModifiers.Shift) != 0;
            bool ctrlPressed = (modifiers & KeyModifiers.Control) != 0;

            if (_pendingVimCommand == "d")
            {
                if (key == Key.D)
                {
                    VimDeleteCurrentLine();
                    _pendingVimCommand = "";
                    return true;
                }
                _pendingVimCommand = "";
                return true;
            }

            if (_pendingVimCommand == "g")
            {
                if (key == Key.G && !shiftPressed)
                {
                    MoveCaret(0, 0);
                    _pendingVimCommand = "";
                    return true;
                }
                _pendingVimCommand = "";
                return true;
            }

            switch (key)
            {
                case Key.Enter:
                    {
                        var (nl, nc) = GetNewCaretPosDown(CaretLine, CaretCol);
                        MoveCaret(nl, nc);
                        return true;
                    }
                case Key.H:
                case Key.Left:
                    {
                        var (nl, nc) = GetNewCaretPosLeft(CaretLine, CaretCol, preventWrap: true);
                        MoveCaret(nl, nc);
                        return true;
                    }
                case Key.L:
                case Key.Right:
                    {
                        var (nl, nc) = GetNewCaretPosRight(CaretLine, CaretCol, preventWrap: true);
                        MoveCaret(nl, nc);
                        return true;
                    }
                case Key.J:
                case Key.Down:
                    {
                        var (nl, nc) = GetNewCaretPosDown(CaretLine, CaretCol);
                        MoveCaret(nl, nc);
                        return true;
                    }
                case Key.K:
                case Key.Up:
                    {
                        var (nl, nc) = GetNewCaretPosUp(CaretLine, CaretCol);
                        MoveCaret(nl, nc);
                        return true;
                    }

                case Key.W:
                    VimMoveWord(forward: true);
                    return true;
                case Key.B:
                    VimMoveWord(forward: false);
                    return true;

                case Key.D0:
                    if (!shiftPressed)
                    {
                        MoveCaret(CaretLine, 0);
                        return true;
                    }
                    break;
                case Key.D4:
                    if (shiftPressed) // $
                    {
                        int len = GetLineLength(CaretLine);
                        MoveCaret(CaretLine, len);
                        return true;
                    }
                    break;

                case Key.I:
                    if (shiftPressed)
                    {
                        if (Document != null)
                        {
                            var lineSpan = Document.GetLine(CaretLine, out _, out var rented);
                            int idx = 0;
                            while (idx < lineSpan.Length && (lineSpan[idx] == ' ' || lineSpan[idx] == '\t'))
                            {
                                idx++;
                            }
                            if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                            MoveCaret(CaretLine, idx);
                        }
                        SetVimMode(VimMode.Insert);
                    }
                    else
                    {
                        SetVimMode(VimMode.Insert);
                    }
                    return true;

                case Key.A:
                    if (shiftPressed)
                    {
                        int len = GetLineLength(CaretLine);
                        MoveCaret(CaretLine, len);
                        SetVimMode(VimMode.Insert);
                    }
                    else
                    {
                        var (nl, nc) = GetNewCaretPosRight(CaretLine, CaretCol, preventWrap: true);
                        MoveCaret(nl, nc);
                        SetVimMode(VimMode.Insert);
                    }
                    return true;

                case Key.O:
                    if (shiftPressed)
                    {
                        MoveCaret(CaretLine, 0);
                        int offset = GetCaretAbsoluteOffset();
                        string ws = GetLeadingWhitespace(CaretLine, CaretCol);
                        TextInputReceived?.Invoke(offset, ws + "\n");
                        MoveCaret(CaretLine, ws.Length);
                        SetVimMode(VimMode.Insert);
                    }
                    else
                    {
                        int len = GetLineLength(CaretLine);
                        MoveCaret(CaretLine, len);
                        int offset = GetCaretAbsoluteOffset();
                        string ws = GetLeadingWhitespace(CaretLine, CaretCol);
                        TextInputReceived?.Invoke(offset, "\n" + ws);
                        SetVimMode(VimMode.Insert);
                    }
                    return true;

                case Key.X:
                    {
                        int offset = GetCaretAbsoluteOffset();
                        if (Document != null && offset < Document.Length)
                        {
                            TextDeleteReceived?.Invoke(offset, 1);
                        }
                        return true;
                    }

                case Key.D:
                    _pendingVimCommand = "d";
                    return true;

                case Key.G:
                    if (shiftPressed)
                    {
                        if (Document != null)
                        {
                            MoveCaret(Document.GetLineCount() - 1, 0);
                        }
                        return true;
                    }
                    else
                    {
                        _pendingVimCommand = "g";
                        return true;
                    }

                case Key.U:
                    if (ctrlPressed)
                    {
                        RedoRequested?.Invoke();
                    }
                    else
                    {
                        UndoRequested?.Invoke();
                    }
                    return true;

                case Key.R:
                    if (ctrlPressed)
                    {
                        RedoRequested?.Invoke();
                        return true;
                    }
                    break;
            }

            return false;
        }

        private bool IsCaretOnWord()
        {
            if (Document == null) return false;
            try
            {
                var lineSpan = Document.GetLine(CaretLine, out _, out var rented);
                if (CaretCol < 0 || CaretCol >= lineSpan.Length)
                {
                    if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                    return false;
                }
                char c = lineSpan[CaretCol];
                bool isWord = char.IsLetterOrDigit(c) || c == '_';
                if (rented != null) System.Buffers.ArrayPool<char>.Shared.Return(rented);
                return isWord;
            }
            catch
            {
                return false;
            }
        }

        public void SelectWordAt(int line, int col)
        {
            if (Document == null) return;
            if (line < 0 || line >= Document.GetLineCount()) return;

            var lineSpan = Document.GetLine(line, out _, out var rentedBuffer);
            if (col < 0 || col > lineSpan.Length)
            {
                if (rentedBuffer != null) System.Buffers.ArrayPool<char>.Shared.Return(rentedBuffer);
                return;
            }

            int targetCol = Math.Min(col, lineSpan.Length - 1);
            if (targetCol >= 0)
            {
                char c = lineSpan[targetCol];
                bool isWordChar = char.IsLetterOrDigit(c) || c == '_';

                int startCol = targetCol;
                int endCol = targetCol;

                if (isWordChar)
                {
                    while (startCol > 0 && (char.IsLetterOrDigit(lineSpan[startCol - 1]) || lineSpan[startCol - 1] == '_'))
                    {
                        startCol--;
                    }
                    while (endCol < lineSpan.Length - 1 && (char.IsLetterOrDigit(lineSpan[endCol + 1]) || lineSpan[endCol + 1] == '_'))
                    {
                        endCol++;
                    }
                }
                else if (char.IsWhiteSpace(c))
                {
                    while (startCol > 0 && char.IsWhiteSpace(lineSpan[startCol - 1]))
                    {
                        startCol--;
                    }
                    while (endCol < lineSpan.Length - 1 && char.IsWhiteSpace(lineSpan[endCol + 1]))
                    {
                        endCol++;
                    }
                }

                long lineStart = Document.GetLineStart(line);
                _selectionStartOffset = (int)lineStart + startCol;
                _selectionEndOffset = (int)lineStart + endCol + 1;
                MoveCaretToOffset(_selectionEndOffset);
            }
            if (rentedBuffer != null) System.Buffers.ArrayPool<char>.Shared.Return(rentedBuffer);
        }

        public void SelectLineAt(int line)
        {
            if (Document == null) return;
            if (line < 0 || line >= Document.GetLineCount()) return;

            long start = Document.GetLineStart(line);
            long end = (line + 1 < Document.GetLineCount()) ? Document.GetLineStart(line + 1) : Document.Length;

            _selectionStartOffset = (int)start;
            _selectionEndOffset = (int)end;
            MoveCaretToOffset(_selectionEndOffset);
        }

        private void ShowContextMenu(PointerPressedEventArgs e)
        {
            var contextMenu = new ContextMenu();
            bool onWord = IsCaretOnWord();

            // 1. Cut / Copy / Paste
            var cutItem = new MenuItem { Header = "Cut", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
            cutItem.Click += (s, ev) => CutRequested?.Invoke();
            
            var copyItem = new MenuItem { Header = "Copy", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
            copyItem.Click += (s, ev) => CopyRequested?.Invoke();

            var pasteItem = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
            pasteItem.Click += (s, ev) => PasteRequested?.Invoke();

            contextMenu.Items.Add(cutItem);
            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(pasteItem);

            contextMenu.Items.Add(new Separator());

            // 2. Go to Definition / Find References / Rename
            var gotoDefItem = new MenuItem { Header = "Go to Definition", InputGesture = new KeyGesture(Key.F12), IsEnabled = onWord };
            gotoDefItem.Click += (s, ev) => GotoDefinitionRequested?.Invoke(GetCaretAbsoluteOffset());
            contextMenu.Items.Add(gotoDefItem);

            var findRefsItem = new MenuItem { Header = "Find All References", InputGesture = new KeyGesture(Key.F12, KeyModifiers.Shift), IsEnabled = onWord };
            findRefsItem.Click += (s, ev) => FindReferencesRequested?.Invoke(GetCaretAbsoluteOffset());
            contextMenu.Items.Add(findRefsItem);

            var renameItem = new MenuItem { Header = "Rename Symbol", InputGesture = new KeyGesture(Key.F2), IsEnabled = onWord };
            renameItem.Click += (s, ev) => RenameRequested?.Invoke(GetCaretAbsoluteOffset());
            contextMenu.Items.Add(renameItem);

            contextMenu.Items.Add(new Separator());

            // 3. Toggle Breakpoint
            var toggleBpItem = new MenuItem { Header = "Toggle Breakpoint", InputGesture = new KeyGesture(Key.F9) };
            toggleBpItem.Click += (s, ev) => ToggleBreakpoint(CaretLine + 1);
            contextMenu.Items.Add(toggleBpItem);

            // 4. Custom Extension Items
            if (ExtensionContextMenuItems.Count > 0)
            {
                contextMenu.Items.Add(new Separator());
                foreach (var item in ExtensionContextMenuItems)
                {
                    var extItem = new MenuItem { Header = item.Header };
                    var localCommandId = item.CommandId;
                    extItem.Click += (s, ev) => ExtensionContextMenuItemClicked?.Invoke(localCommandId);
                    contextMenu.Items.Add(extItem);
                }
            }

            contextMenu.Open(this);
        }
    }
}
