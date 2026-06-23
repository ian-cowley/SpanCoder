using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SpanCoder.Shell
{
    public class TerminalLine
    {
        public ObservableCollection<TerminalRun> Runs { get; } = new();
    }

    public class TerminalRun
    {
        public string Text { get; set; } = "";
        public IBrush Brush { get; set; } = Brushes.LightGray;
    }

    public class TerminalControl : Grid
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly ItemsControl _itemsControl;
        private readonly ObservableCollection<TerminalLine> _lines = new();
        public ObservableCollection<TerminalLine> Lines => _lines;
        private PtyHost? _pty;
        private IBrush _currentBrush = Brushes.LightGray;
        private bool _carriageReturned;
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
        private readonly object _decoderLock = new();

        // ANSI Color palette
        private static readonly IBrush ColorBlack = Brushes.Black;
        private static readonly IBrush ColorRed = new SolidColorBrush(Color.Parse("#E51400"));
        private static readonly IBrush ColorGreen = new SolidColorBrush(Color.Parse("#23D160"));
        private static readonly IBrush ColorYellow = new SolidColorBrush(Color.Parse("#FFDD57"));
        private static readonly IBrush ColorBlue = new SolidColorBrush(Color.Parse("#209CEE"));
        private static readonly IBrush ColorMagenta = new SolidColorBrush(Color.Parse("#FF3860"));
        private static readonly IBrush ColorCyan = new SolidColorBrush(Color.Parse("#00D1B2"));
        private static readonly IBrush ColorWhite = Brushes.White;
        private static readonly IBrush ColorGray = Brushes.Gray;

        public TerminalControl()
        {
            Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
            Focusable = true;

            _scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            _itemsControl = new ItemsControl
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Focusable = false
            };

            // Custom ItemTemplate to render lines with styled Inlines
            _itemsControl.ItemTemplate = new FuncDataTemplate<TerminalLine>((line, names) =>
            {
                var tb = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = Brushes.LightGray
                };

                if (line == null) return tb;

                // Bind text runs
                void SyncRuns()
                {
                    if (tb.Inlines != null)
                    {
                        tb.Inlines.Clear();
                        foreach (var run in line.Runs)
                        {
                            tb.Inlines.Add(new Avalonia.Controls.Documents.Run { Text = run.Text, Foreground = run.Brush });
                        }
                        if (line == _lines.LastOrDefault())
                        {
                            tb.Inlines.Add(new Avalonia.Controls.Documents.Run { Text = "█", Foreground = Brushes.LightGray });
                        }
                    }
                }

                SyncRuns();
                line.Runs.CollectionChanged += (s, e) => SyncRuns();

                return tb;
            }, true);

            _itemsControl.ItemsSource = _lines;
            _scrollViewer.Content = _itemsControl;
            Children.Add(_scrollViewer);

            // Add first empty line
            _lines.Add(new TerminalLine());

            // Auto Scroll on new items
            _lines.CollectionChanged += Lines_CollectionChanged;
        }

        public void BindPty(PtyHost pty)
        {
            _pty = pty;
            _pty.DataReceived += Pty_DataReceived;
            _pty.Exited += Pty_Exited;
        }

        private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                // Refresh runs of the previously last line to remove its cursor
                if (_lines.Count > 1)
                {
                    var prevLine = _lines[_lines.Count - 2];
                    var lastRun = prevLine.Runs.LastOrDefault();
                    if (lastRun != null)
                    {
                        int idx = prevLine.Runs.IndexOf(lastRun);
                        prevLine.Runs[idx] = new TerminalRun { Text = lastRun.Text, Brush = lastRun.Brush };
                    }
                    else
                    {
                        prevLine.Runs.Add(new TerminalRun { Text = "" });
                        prevLine.Runs.RemoveAt(0);
                    }
                }
            }

            if (_lines.Count > 0)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _scrollViewer.ScrollToEnd();
                });
            }
        }

        private void Pty_DataReceived(byte[] data, int length)
        {
            string text;
            lock (_decoderLock)
            {
                int charCount = _utf8Decoder.GetCharCount(data, 0, length);
                char[] chars = new char[charCount];
                _utf8Decoder.GetChars(data, 0, length, chars, 0);
                text = new string(chars);
            }
            Dispatcher.UIThread.Post(() => ProcessOutputText(text));
        }

        private void Pty_Exited()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ProcessOutputText("\n[Process Exited]\n");
            });
        }

        public void ProcessOutputText(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '\x1B') // Escape sequence start
                {
                    if (i + 1 < text.Length && text[i + 1] == '[')
                    {
                        int seqEnd = -1;
                        for (int j = i + 2; j < text.Length; j++)
                        {
                            char seqChar = text[j];
                            if ((seqChar >= 'a' && seqChar <= 'z') || (seqChar >= 'A' && seqChar <= 'Z'))
                            {
                                seqEnd = j;
                                break;
                            }
                        }

                        if (seqEnd != -1)
                        {
                            string seq = text.Substring(i + 2, seqEnd - (i + 2));
                            char command = text[seqEnd];
                            HandleAnsiEscape(seq, command);
                            i = seqEnd + 1;
                            continue;
                        }
                    }
                }

                if (c == '\n')
                {
                    _lines.Add(new TerminalLine());
                    if (_lines.Count > 1000) // Keep scrollback capped
                    {
                        _lines.RemoveAt(0);
                    }
                    _carriageReturned = false;
                }
                else if (c == '\r')
                {
                    _carriageReturned = true;
                }
                else if (c == '\b') // Backspace
                {
                    var activeLine = _lines.LastOrDefault();
                    if (activeLine != null && activeLine.Runs.Count > 0)
                    {
                        var lastRun = activeLine.Runs.Last();
                        if (lastRun.Text.Length > 0)
                        {
                            lastRun.Text = lastRun.Text.Substring(0, lastRun.Text.Length - 1);
                            if (lastRun.Text.Length == 0)
                            {
                                activeLine.Runs.Remove(lastRun);
                            }
                            else
                            {
                                // Trigger notification
                                int idx = activeLine.Runs.IndexOf(lastRun);
                                activeLine.Runs[idx] = new TerminalRun { Text = lastRun.Text, Brush = lastRun.Brush };
                            }
                        }
                    }
                }
                else if (c != '\a') // Ignore bell
                {
                    var activeLine = _lines.LastOrDefault();
                    if (activeLine == null)
                    {
                        activeLine = new TerminalLine();
                        _lines.Add(activeLine);
                    }

                    if (_carriageReturned)
                    {
                        activeLine.Runs.Clear();
                        _carriageReturned = false;
                    }

                    AppendToLine(activeLine, c.ToString());
                }

                i++;
            }
        }

        private void AppendToLine(TerminalLine line, string text)
        {
            var lastRun = line.Runs.LastOrDefault();
            if (lastRun != null && lastRun.Brush == _currentBrush)
            {
                lastRun.Text += text;
                // Trigger collection refresh
                int idx = line.Runs.IndexOf(lastRun);
                line.Runs[idx] = new TerminalRun { Text = lastRun.Text, Brush = lastRun.Brush };
            }
            else
            {
                line.Runs.Add(new TerminalRun { Text = text, Brush = _currentBrush });
            }
        }

        private void HandleAnsiEscape(string seq, char command)
        {
            if (command == 'm') // Graphics rendering (colors)
            {
                string[] parts = seq.Split(';');
                foreach (var part in parts)
                {
                    if (int.TryParse(part, out int code))
                    {
                        if (code == 0) _currentBrush = Brushes.LightGray; // Reset
                        else if (code == 30) _currentBrush = ColorBlack;
                        else if (code == 31) _currentBrush = ColorRed;
                        else if (code == 32) _currentBrush = ColorGreen;
                        else if (code == 33) _currentBrush = ColorYellow;
                        else if (code == 34) _currentBrush = ColorBlue;
                        else if (code == 35) _currentBrush = ColorMagenta;
                        else if (code == 36) _currentBrush = ColorCyan;
                        else if (code == 37) _currentBrush = ColorWhite;
                        else if (code == 90) _currentBrush = ColorGray;
                    }
                }
            }
            else if (command == 'J') // Clear Screen
            {
                if (seq == "2" || seq == "2J")
                {
                    _lines.Clear();
                    _lines.Add(new TerminalLine());
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            e.Handled = true;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (!string.IsNullOrEmpty(e.Text) && _pty != null)
            {
                _pty.Write(e.Text);
            }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_pty == null) return;

            string? seq = GetKeySequence(e.Key, e.KeyModifiers);
            if (seq != null)
            {
                _pty.Write(seq);
                e.Handled = true;
            }
        }

        private string? GetKeySequence(Key key, KeyModifiers modifiers)
        {
            switch (key)
            {
                case Key.Enter:
                    return "\r";
                case Key.Back:
                    return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "\u0008" : "\u007f";
                case Key.Tab:
                    return "\t";
                case Key.Escape:
                    return "\x1B";
                case Key.Up:
                    return "\x1B[A";
                case Key.Down:
                    return "\x1B[B";
                case Key.Right:
                    return "\x1B[C";
                case Key.Left:
                    return "\x1B[D";
                case Key.Home:
                    return "\x1B[H";
                case Key.End:
                    return "\x1B[F";
            }
            return null;
        }
    }
}
