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
    public class TextEditorCanvas : Control
    {
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
            return digits * CharWidth + 15.0;
        }
        
        public struct ExtraCaret
        {
            public int Line { get; set; }
            public int Col { get; set; }
            public int AbsoluteOffset { get; set; }
        }

        // Caret state
        public int CaretLine { get; private set; } = 0;
        public int CaretCol { get; private set; } = 0;
        public int CaretAbsoluteOffset { get; private set; } = 0;
        private bool _caretVisible = true;
        private readonly System.Collections.Generic.List<ExtraCaret> _extraCarets = new();
        public System.Collections.Generic.IReadOnlyList<ExtraCaret> ExtraCarets => _extraCarets;
        private int _selectionStartOffset = -1;
        private int _selectionEndOffset = -1;
        private bool _isDragging;
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

        public event Action<int, string>? TextInputReceived;
        public event Action<int, int>? TextDeleteReceived;
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
            InvalidateVisual();
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

            int lineIndex = (int)(targetY / LineHeight);
            double gutterWidth = GetGutterWidth();
            int colIndex = (int)Math.Round((targetX - gutterWidth - 10) / CharWidth);
            colIndex = Math.Max(0, colIndex);

            int lineCount = Document.GetLineCount();
            if (lineIndex >= 0 && lineIndex < lineCount)
            {
                long start = Document.GetLineStart(lineIndex);
                long end = (lineIndex + 1 < lineCount) ? Document.GetLineStart(lineIndex + 1) : Document.Length;
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
        }

        public int GetCaretAbsoluteOffset()
        {
            return CaretAbsoluteOffset;
        }

        public void MoveCaret(int line, int col)
        {
            if (Document == null) return;
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
        }

        public void EnsureCaretVisible()
        {
            if (Document == null || Bounds.Height <= 0 || Bounds.Width <= 0) return;

            double dy = 0;
            double caretTopY = CaretLine * LineHeight;
            double caretBottomY = (CaretLine + 1) * LineHeight;

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

        private ExtraCaret CreateExtraCaretFromOffset(int absoluteOffset)
        {
            if (Document == null) return new ExtraCaret { AbsoluteOffset = absoluteOffset, Line = 0, Col = 0 };
            int lineCount = Document.GetLineCount();
            if (lineCount <= 0) return new ExtraCaret { AbsoluteOffset = 0, Line = 0, Col = 0 };

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
            long end = (targetLine + 1 < lineCount) ? Document.GetLineStart(targetLine + 1) : Document.Length;
            int lineLen = (int)(end - lineStart);

            int col = absoluteOffset - (int)lineStart;
            int clampedCol = col;

            if (lineLen > 0)
            {
                var lineSpan = Document.GetLine(targetLine, out var isCont, out var rented);
                int printableLen = lineLen;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                
                clampedCol = Math.Max(0, Math.Min(col, printableLen));
            }
            else
            {
                clampedCol = 0;
            }

            return new ExtraCaret
            {
                AbsoluteOffset = (int)lineStart + clampedCol,
                Line = targetLine,
                Col = clampedCol
            };
        }

        private void DeduplicateCarets()
        {
            int primaryOffset = GetCaretAbsoluteOffset();
            var uniqueOffsets = new System.Collections.Generic.HashSet<int> { primaryOffset };
            for (int i = _extraCarets.Count - 1; i >= 0; i--)
            {
                int offset = _extraCarets[i].AbsoluteOffset;
                if (uniqueOffsets.Contains(offset))
                {
                    _extraCarets.RemoveAt(i);
                }
                else
                {
                    uniqueOffsets.Add(offset);
                }
            }
        }

        public System.Collections.Generic.List<int> GetCaretOffsetsDescending()
        {
            var offsets = new System.Collections.Generic.List<int> { GetCaretAbsoluteOffset() };
            foreach (var ec in _extraCarets)
            {
                offsets.Add(ec.AbsoluteOffset);
            }
            offsets.Sort((a, b) => b.CompareTo(a)); // Descending order
            var unique = new System.Collections.Generic.List<int>();
            foreach (var o in offsets)
            {
                if (unique.Count == 0 || unique[^1] != o)
                {
                    unique.Add(o);
                }
            }
            return unique;
        }

        private int AdjustOffset(int offset, int editOffset, int addedLength, int deletedLength)
        {
            if (addedLength > 0)
            {
                if (offset >= editOffset)
                {
                    offset += addedLength;
                }
            }
            else if (deletedLength > 0)
            {
                if (offset > editOffset)
                {
                    int shift = Math.Min(offset - editOffset, deletedLength);
                    offset -= shift;
                }
            }
            return offset;
        }

        public void AdjustCaret(int editOffset, int addedLength, int deletedLength)
        {
            _selectionStartOffset = -1;
            _selectionEndOffset = -1;
            if (Document == null) return;
            UpdateLineStates(0);

            // Adjust primary caret
            int caretOffset = GetCaretAbsoluteOffset();
            caretOffset = AdjustOffset(caretOffset, editOffset, addedLength, deletedLength);
            MoveCaretToOffset(caretOffset);

            // Adjust extra carets
            for (int i = 0; i < _extraCarets.Count; i++)
            {
                var ec = _extraCarets[i];
                int ecOffset = AdjustOffset(ec.AbsoluteOffset, editOffset, addedLength, deletedLength);
                _extraCarets[i] = CreateExtraCaretFromOffset(ecOffset);
            }

            DeduplicateCarets();
            InvalidateVisual();
        }

        internal void AddExtraCaretForTest(int absoluteOffset)
        {
            var newCaret = CreateExtraCaretFromOffset(absoluteOffset);
            _extraCarets.Add(newCaret);
            InvalidateVisual();
        }

        internal void ClearExtraCaretsForTest()
        {
            _extraCarets.Clear();
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

        private (int Line, int Col) GetNewCaretPosLeft(int line, int col)
        {
            if (Document == null) return (0, 0);
            if (col > 0)
            {
                return (line, col - 1);
            }
            else if (line > 0)
            {
                long start = Document.GetLineStart(line - 1);
                long end = Document.GetLineStart(line);
                int prevLen = (int)(end - start);
                var lineSpan = Document.GetLine(line - 1, out var isCont, out var rented);
                int printableLen = prevLen;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
                if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
                if (rented != null) ArrayPool<char>.Shared.Return(rented);
                return (line - 1, printableLen);
            }
            return (line, col);
        }

        private (int Line, int Col) GetNewCaretPosRight(int line, int col)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            long start = Document.GetLineStart(line);
            long end = (line + 1 < lineCount) ? Document.GetLineStart(line + 1) : Document.Length;
            int lineLen = (int)(end - start);
            var lineSpan = Document.GetLine(line, out var isCont, out var rented);
            int printableLen = lineLen;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\n') printableLen--;
            if (printableLen > 0 && lineSpan[printableLen - 1] == '\r') printableLen--;
            if (rented != null) ArrayPool<char>.Shared.Return(rented);

            if (col < printableLen)
            {
                return (line, col + 1);
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
            if (line > 0)
            {
                return ClampToPrintable(line - 1, col);
            }
            return (line, col);
        }

        private (int Line, int Col) GetNewCaretPosDown(int line, int col)
        {
            if (Document == null) return (0, 0);
            int lineCount = Document.GetLineCount();
            if (line + 1 < lineCount)
            {
                return ClampToPrintable(line + 1, col);
            }
            return (line, col);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DocumentProperty)
            {
                UpdateLineStates(0);
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


        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();

            var pos = e.GetPosition(this);
            double gutterWidth = GetGutterWidth();

            if (pos.X < gutterWidth)
            {
                int clickedLine = (int)((pos.Y + ScrollY) / LineHeight) + 1;
                if (Document != null && clickedLine >= 1 && clickedLine <= Document.GetLineCount())
                {
                    ToggleBreakpoint(clickedLine);
                }
                e.Handled = true;
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                int clickOffset = GetOffsetFromPointer(pos);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    var newCaret = CreateExtraCaretFromOffset(clickOffset);
                    int primaryOffset = GetCaretAbsoluteOffset();
                    if (newCaret.AbsoluteOffset == primaryOffset)
                    {
                        // Keep primary caret, do nothing or keep it as is
                    }
                    else
                    {
                        int existingIdx = _extraCarets.FindIndex(c => c.AbsoluteOffset == newCaret.AbsoluteOffset);
                        if (existingIdx >= 0)
                        {
                            _extraCarets.RemoveAt(existingIdx);
                        }
                        else
                        {
                            _extraCarets.Add(newCaret);
                        }
                        InvalidateVisual();
                    }
                    _isDragging = false;
                }
                else
                {
                    _extraCarets.Clear();
                    _selectionStartOffset = clickOffset;
                    _selectionEndOffset = clickOffset;
                    _isDragging = true;
                    MoveCaretToOffset(clickOffset);
                }
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
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
                case Key.Escape:
                    if (_extraCarets.Count > 0)
                    {
                        _extraCarets.Clear();
                        InvalidateVisual();
                        e.Handled = true;
                    }
                    break;
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
                        var (newLine, newCol) = GetNewCaretPosUp(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        for (int i = 0; i < _extraCarets.Count; i++)
                        {
                            var ec = _extraCarets[i];
                            var (el, ecCol) = GetNewCaretPosUp(ec.Line, ec.Col);
                            long start = Document.GetLineStart(el);
                            _extraCarets[i] = new ExtraCaret { Line = el, Col = ecCol, AbsoluteOffset = (int)start + ecCol };
                        }
                        DeduplicateCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Down:
                    {
                        var (newLine, newCol) = GetNewCaretPosDown(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        for (int i = 0; i < _extraCarets.Count; i++)
                        {
                            var ec = _extraCarets[i];
                            var (el, ecCol) = GetNewCaretPosDown(ec.Line, ec.Col);
                            long start = Document.GetLineStart(el);
                            _extraCarets[i] = new ExtraCaret { Line = el, Col = ecCol, AbsoluteOffset = (int)start + ecCol };
                        }
                        DeduplicateCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Left:
                    {
                        var (newLine, newCol) = GetNewCaretPosLeft(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        for (int i = 0; i < _extraCarets.Count; i++)
                        {
                            var ec = _extraCarets[i];
                            var (el, ecCol) = GetNewCaretPosLeft(ec.Line, ec.Col);
                            long start = Document.GetLineStart(el);
                            _extraCarets[i] = new ExtraCaret { Line = el, Col = ecCol, AbsoluteOffset = (int)start + ecCol };
                        }
                        DeduplicateCarets();
                        e.Handled = true;
                    }
                    break;
                case Key.Right:
                    {
                        var (newLine, newCol) = GetNewCaretPosRight(CaretLine, CaretCol);
                        MoveCaret(newLine, newCol);
                        for (int i = 0; i < _extraCarets.Count; i++)
                        {
                            var ec = _extraCarets[i];
                            var (el, ecCol) = GetNewCaretPosRight(ec.Line, ec.Col);
                            long start = Document.GetLineStart(el);
                            _extraCarets[i] = new ExtraCaret { Line = el, Col = ecCol, AbsoluteOffset = (int)start + ecCol };
                        }
                        DeduplicateCarets();
                        e.Handled = true;
                    }
                    break;

                case Key.Back:
                    {
                        var offsets = GetCaretOffsetsDescending();
                        foreach (var offset in offsets)
                        {
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
                        var offsets = GetCaretOffsetsDescending();
                        foreach (var offset in offsets)
                        {
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
                        var offsets = GetCaretOffsetsDescending();
                        foreach (var offset in offsets)
                        {
                            TextInputReceived?.Invoke(offset, "\n");
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

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (Document == null || string.IsNullOrEmpty(e.Text) || e.Text == "\r" || e.Text == "\n")
                return;

            var offsets = GetCaretOffsetsDescending();
            foreach (var offset in offsets)
            {
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

            if (_isDragging)
            {
                int dragOffset = GetOffsetFromPointer(_lastMousePos);
                MoveCaretToOffset(dragOffset);
                _selectionEndOffset = dragOffset;
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
            double gutterWidth = GetGutterWidth();

            // Calculate visible line range
            int startLine = (int)(ScrollY / LineHeight);
            int endLine = (int)((ScrollY + height) / LineHeight) + 1;

            startLine = Math.Max(0, Math.Min(startLine, lineCount - 1));
            endLine = Math.Max(0, Math.Min(endLine, lineCount - 1));

            // Render text
            for (int i = startLine; i <= endLine; i++)
            {
                double yOffset = (i * LineHeight) - ScrollY;
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
                            double y = (i * LineHeight) - ScrollY;
                            context.FillRectangle(new SolidColorBrush(Color.Parse("#264F78")), new Rect(startX, y, endX - startX, LineHeight));
                        }
                    }
                }

                if (renderLen > 0)
                {
                    string textStr = lineSpan.Slice(0, renderLen).ToString();
                    var formatted = new FormattedText(
                        textStr,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        Brushes.LightGray
                    );

                    LineState startState = i > 0 && (i - 1) < _lineStates.Count ? _lineStates[i - 1] : LineState.Normal;
                    string extension = System.IO.Path.GetExtension(doc.FilePath ?? "");
                    var lexer = new DocumentLexer(lineSpan.Slice(0, renderLen), extension, startState);
                    while (lexer.NextToken(out var token, out var nextState))
                    {
                        var brush = GetTokenBrush(token.Type);
                        if (brush != null)
                        {
                            formatted.SetForegroundBrush(brush, token.Start, token.Length);
                        }
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
                                double y = (i * LineHeight) - ScrollY + LineHeight - 2;
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
                if (CaretLine >= startLine && CaretLine <= endLine)
                {
                    double caretX = gutterWidth + 10 + (CaretCol * CharWidth) - ScrollX;
                    double caretY = (CaretLine * LineHeight) - ScrollY;
                    context.DrawLine(pen, new Point(caretX, caretY + 2), new Point(caretX, caretY + LineHeight - 2));
                }

                foreach (var ec in _extraCarets)
                {
                    if (ec.Line >= startLine && ec.Line <= endLine)
                    {
                        double caretX = gutterWidth + 10 + (ec.Col * CharWidth) - ScrollX;
                        double caretY = (ec.Line * LineHeight) - ScrollY;
                        context.DrawLine(pen, new Point(caretX, caretY + 2), new Point(caretX, caretY + LineHeight - 2));
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
                for (int i = startLine; i <= endLine; i++)
                {
                    double yOffset = (i * LineHeight) - ScrollY;

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

                    string lineNumStr = (i + 1).ToString();
                    var formattedNum = new FormattedText(
                        lineNumStr,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize - 1.0,
                        new SolidColorBrush(Color.Parse("#858585"))
                    );
                    double xOffset = gutterWidth - 5.0 - formattedNum.Width;
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
        }

        private int GetOffsetFromPointer(Point pos)
        {
            if (Document == null) return 0;
            int lineCount = Document.GetLineCount();
            
            double targetY = pos.Y + ScrollY;
            double targetX = pos.X + ScrollX;

            int lineIndex = (int)(targetY / LineHeight);
            lineIndex = Math.Max(0, Math.Min(lineIndex, lineCount - 1));

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
    }
}
