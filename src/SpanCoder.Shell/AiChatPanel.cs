using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class ChatMessageItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _content = "";
        public string Role { get; set; } = "";
        public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);

        public string Content
        {
            get => _content;
            set
            {
                _content = value;
                OnPropertyChanged(nameof(Content));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class ToolExecutionItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _status = "running";
        private string _output = "";
        private bool _isExpanded = true;

        public string ToolName { get; set; } = "";
        public string Arguments { get; set; } = ""; // mapped to tool call ID

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public string Output
        {
            get => _output;
            set
            {
                _output = value;
                OnPropertyChanged(nameof(Output));
                OnPropertyChanged(nameof(HasOutput));
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                _isExpanded = value;
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(ToggleText));
            }
        }

        public bool HasOutput => !string.IsNullOrEmpty(Output);

        public string ToggleText => IsExpanded ? "Hide Output" : "Show Output";

        public string StatusText => Status.ToUpper();

        public IBrush StatusColor => Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ? Brushes.LimeGreen :
                                     Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ? Brushes.Red :
                                     Brushes.Orange;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class AiChatPanel : Grid
    {
        private IEngineConnection? _engine;
        private readonly ScrollViewer _chatScrollViewer;
        private readonly ItemsControl _chatItemsControl;
        private readonly ObservableCollection<object> _chatItems = new();
        
        // Settings UI
        internal readonly ComboBox _providerComboBox;
        internal readonly TextBox _modelTextBox;
        internal readonly TextBox _apiKeyTextBox;
        internal readonly CheckBox _yoloCheckBox;
        internal readonly Expander _settingsExpander;

        // Input UI
        private readonly TextBox _inputTextBox;
        private readonly Button _sendButton;
        private readonly Button _stopButton;
        private readonly TextBlock _statusLabel;

        private ChatMessageItem? _currentAssistantMessage;
        private bool _isRunning;
        private bool _isUpdatingUi;

        public AiChatPanel()
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 0: Settings Expander
            RowDefinitions.Add(new RowDefinition(GridLength.Star)); // 1: Chat History ListBox
            RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 2: Status & Input panel

            // 1. Settings Expander
            var settingsGrid = new Grid();
            settingsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            settingsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            settingsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            settingsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var providerLabel = new TextBlock { Text = "Provider:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), FontSize = 12, Foreground = Brushes.Gray };
            _providerComboBox = new ComboBox
            {
                ItemsSource = new[] { "OpenAI", "Gemini", "Ollama" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 4)
            };
            settingsGrid.Children.Add(providerLabel);
            Grid.SetRow(providerLabel, 0); Grid.SetColumn(providerLabel, 0);
            settingsGrid.Children.Add(_providerComboBox);
            Grid.SetRow(_providerComboBox, 0); Grid.SetColumn(_providerComboBox, 1);

            var modelLabel = new TextBlock { Text = "Model:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), FontSize = 12, Foreground = Brushes.Gray };
            _modelTextBox = new TextBox { Margin = new Thickness(0, 4) };
            settingsGrid.Children.Add(modelLabel);
            Grid.SetRow(modelLabel, 1); Grid.SetColumn(modelLabel, 0);
            settingsGrid.Children.Add(_modelTextBox);
            Grid.SetRow(_modelTextBox, 1); Grid.SetColumn(_modelTextBox, 1);

            var apiKeyLabel = new TextBlock { Text = "API Key:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), FontSize = 12, Foreground = Brushes.Gray };
            _apiKeyTextBox = new TextBox { PasswordChar = '*', Margin = new Thickness(0, 4) };
            settingsGrid.Children.Add(apiKeyLabel);
            Grid.SetRow(apiKeyLabel, 2); Grid.SetColumn(apiKeyLabel, 0);
            settingsGrid.Children.Add(_apiKeyTextBox);
            Grid.SetRow(_apiKeyTextBox, 2); Grid.SetColumn(_apiKeyTextBox, 1);

            _settingsExpander = new Expander
            {
                Header = new TextBlock { Text = "Model Configuration", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.LightGray },
                Content = settingsGrid,
                IsExpanded = false,
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                Margin = new Thickness(4)
            };
            Children.Add(_settingsExpander);
            Grid.SetRow(_settingsExpander, 0);

            // 2. Chat ItemsControl & ScrollViewer
            _chatItemsControl = new ItemsControl
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            // Setup Data Template for chat items
            _chatItemsControl.ItemTemplate = new FuncDataTemplate<object>((item, names) =>
            {
                if (item is ChatMessageItem msg)
                {
                    var title = new TextBlock
                    {
                        Text = msg.IsUser ? "User" : "SpanCoder AI",
                        FontWeight = FontWeight.Bold,
                        FontSize = 11,
                        Foreground = msg.IsUser ? new SolidColorBrush(Color.Parse("#519ABA")) : new SolidColorBrush(Color.Parse("#D8A0DF")),
                        Margin = new Thickness(0, 0, 0, 4)
                    };

                    // Use ContentControl to host the dynamic Markdown rendering
                    var contentContainer = new ContentControl
                    {
                        Content = RenderMarkdown(msg.Content),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };

                    msg.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(ChatMessageItem.Content))
                        {
                            contentContainer.Content = RenderMarkdown(msg.Content);
                        }
                    };

                    var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
                    stack.Children.Add(title);
                    stack.Children.Add(contentContainer);

                    var border = new Border
                    {
                        Background = new SolidColorBrush(msg.IsUser ? Color.Parse("#202023") : Color.Parse("#1A1A1C")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#2D2D30")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8),
                        Margin = new Thickness(2, 4),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    border.Child = stack;
                    return border;
                }
                else if (item is ToolExecutionItem tool)
                {
                    var headerGrid = new Grid();
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                    headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                    var title = new TextBlock
                    {
                        Text = $"🔧 Tool: {tool.ToolName}",
                        FontWeight = FontWeight.Bold,
                        FontSize = 11,
                        Foreground = Brushes.LightGray,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    headerGrid.Children.Add(title);
                    Grid.SetColumn(title, 0);

                    var toggleButton = new Button
                    {
                        Content = tool.ToggleText,
                        FontSize = 9,
                        Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                        Foreground = Brushes.LightGray,
                        Margin = new Thickness(0, 0, 8, 0),
                        Padding = new Thickness(6, 2),
                        IsVisible = tool.HasOutput
                    };
                    headerGrid.Children.Add(toggleButton);
                    Grid.SetColumn(toggleButton, 1);

                    var status = new TextBlock
                    {
                        Text = tool.StatusText,
                        FontWeight = FontWeight.Bold,
                        FontSize = 10,
                        Foreground = tool.StatusColor,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    headerGrid.Children.Add(status);
                    Grid.SetColumn(status, 2);

                    var outputBlock = new TextBlock
                    {
                        Text = tool.Output,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                        TextWrapping = TextWrapping.Wrap
                    };
                    outputBlock.IsVisible = tool.HasOutput;

                    var outputScroll = new ScrollViewer
                    {
                        MinHeight = 35,
                        MaxHeight = 400,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Background = new SolidColorBrush(Color.Parse("#141416")),
                        Padding = new Thickness(6),
                        Margin = new Thickness(0, 6, 0, 0),
                        IsVisible = tool.IsExpanded && tool.HasOutput
                    };
                    outputScroll.Content = outputBlock;

                    toggleButton.Click += (s, e) =>
                    {
                        tool.IsExpanded = !tool.IsExpanded;
                    };

                    tool.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(ToolExecutionItem.Status))
                        {
                            status.Text = tool.StatusText;
                            status.Foreground = tool.StatusColor;
                        }
                        else if (e.PropertyName == nameof(ToolExecutionItem.Output))
                        {
                            outputBlock.Text = tool.Output;
                            outputBlock.IsVisible = tool.HasOutput;
                            toggleButton.IsVisible = tool.HasOutput;
                            outputScroll.IsVisible = tool.IsExpanded && tool.HasOutput;
                        }
                        else if (e.PropertyName == nameof(ToolExecutionItem.IsExpanded))
                        {
                            toggleButton.Content = tool.ToggleText;
                            outputScroll.IsVisible = tool.IsExpanded && tool.HasOutput;
                        }
                    };

                    var stack = new StackPanel();
                    stack.Children.Add(headerGrid);
                    stack.Children.Add(outputScroll);

                    var border = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#1A1A1E")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#2D2D32")),
                        BorderThickness = new Thickness(1, 1, 1, 1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8),
                        Margin = new Thickness(2, 4)
                    };
                    border.Child = stack;
                    return border;
                }

                return new ContentControl();
            }, true);

            _chatItemsControl.ItemsSource = _chatItems;

            _chatScrollViewer = new ScrollViewer
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Content = _chatItemsControl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };
            Children.Add(_chatScrollViewer);
            Grid.SetRow(_chatScrollViewer, 1);

            // 3. Status & Input panel
            var inputGrid = new Grid();
            inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            inputGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            // Row 0: YOLO Mode & Status
            var row0 = new Grid();
            row0.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row0.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            _yoloCheckBox = new CheckBox
            {
                Content = "YOLO Mode (Auto-Approve)",
                FontSize = 11,
                Foreground = Brushes.Gray,
                IsChecked = false
            };
            row0.Children.Add(_yoloCheckBox);
            Grid.SetColumn(_yoloCheckBox, 0);

            _statusLabel = new TextBlock
            {
                Text = "Idle",
                FontSize = 11,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            row0.Children.Add(_statusLabel);
            Grid.SetColumn(_statusLabel, 1);

            inputGrid.Children.Add(row0);
            Grid.SetRow(row0, 0);

            // Row 1: Input TextBox
            _inputTextBox = new TextBox
            {
                Watermark = "Ask AI to write code or run commands...",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 60,
                Margin = new Thickness(4),
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D"))
            };
            _inputTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
                {
                    SendCurrentMessage();
                    e.Handled = true;
                }
            };
            inputGrid.Children.Add(_inputTextBox);
            Grid.SetRow(_inputTextBox, 1);

            // Row 2: Send & Stop buttons
            var buttonsGrid = new Grid();
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            _sendButton = new Button
            {
                Content = "Send Request",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#007ACC")),
                Foreground = Brushes.White,
                Margin = new Thickness(4)
            };
            _sendButton.Click += (s, e) => SendCurrentMessage();
            buttonsGrid.Children.Add(_sendButton);
            Grid.SetColumn(_sendButton, 0);

            _stopButton = new Button
            {
                Content = "Stop Agent",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#A51D24")),
                Foreground = Brushes.White,
                Margin = new Thickness(4),
                IsEnabled = false
            };
            _stopButton.Click += (s, e) => StopActiveAgent();
            buttonsGrid.Children.Add(_stopButton);
            Grid.SetColumn(_stopButton, 1);

            inputGrid.Children.Add(buttonsGrid);
            Grid.SetRow(buttonsGrid, 2);

            var inputBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(4),
                Background = new SolidColorBrush(Color.Parse("#1E1E1F"))
            };
            inputBorder.Child = inputGrid;
            Children.Add(inputBorder);
            Grid.SetRow(inputBorder, 2);

            // Initialize configuration
            LoadSettings();

            // Hook up settings changes save
            _providerComboBox.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingUi) return;

                // Save current settings to the old provider before switching
                string oldProvider = SettingsManager.Get("ai.provider");
                if (!string.IsNullOrEmpty(oldProvider))
                {
                    SaveSettingsForProvider(oldProvider);
                }

                if (_providerComboBox.SelectedItem is string provider)
                {
                    SettingsManager.Set("ai.provider", provider);
                    UpdateModelAndKeyInputs(provider);
                }
            };
            _modelTextBox.LostFocus += (s, e) => SaveSettings();
            _apiKeyTextBox.LostFocus += (s, e) => SaveSettings();
        }

        public void SetEngineConnection(IEngineConnection engine)
        {
            _engine = engine;
        }

        private void LoadSettings()
        {
            _isUpdatingUi = true;
            try
            {
                string provider = SettingsManager.Get("ai.provider");
                if (string.IsNullOrEmpty(provider)) provider = "OpenAI";
                _providerComboBox.SelectedItem = provider;

                UpdateModelAndKeyInputsInternal(provider);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateModelAndKeyInputs(string provider)
        {
            _isUpdatingUi = true;
            try
            {
                UpdateModelAndKeyInputsInternal(provider);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void UpdateModelAndKeyInputsInternal(string provider)
        {
            if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                _modelTextBox.Text = SettingsManager.Get("ai.openai.model");
                _apiKeyTextBox.Text = SettingsManager.Get("ai.openai.apikey");
            }
            else if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                _modelTextBox.Text = SettingsManager.Get("ai.gemini.model");
                _apiKeyTextBox.Text = SettingsManager.Get("ai.gemini.apikey");
            }
            else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                _modelTextBox.Text = SettingsManager.Get("ai.ollama.model");
                _apiKeyTextBox.Text = SettingsManager.Get("ai.ollama.apikey");
            }
        }

        private void SaveSettings()
        {
            if (_isUpdatingUi) return;
            if (_providerComboBox.SelectedItem is not string provider) return;

            SettingsManager.Set("ai.provider", provider);
            SaveSettingsForProvider(provider);
        }

        private void SaveSettingsForProvider(string provider)
        {
            if (provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                SettingsManager.Set("ai.openai.model", _modelTextBox.Text ?? "");
                SettingsManager.Set("ai.openai.apikey", _apiKeyTextBox.Text ?? "");
            }
            else if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
            {
                SettingsManager.Set("ai.gemini.model", _modelTextBox.Text ?? "");
                SettingsManager.Set("ai.gemini.apikey", _apiKeyTextBox.Text ?? "");
            }
            else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            {
                SettingsManager.Set("ai.ollama.model", _modelTextBox.Text ?? "");
                SettingsManager.Set("ai.ollama.apikey", _apiKeyTextBox.Text ?? "");
            }
        }

        private void SendCurrentMessage()
        {
            if (_isRunning) return;

            // Make sure any edited values in textboxes are saved first
            SaveSettings();

            string prompt = _inputTextBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(prompt)) return;

            if (_engine == null)
            {
                AddErrorMessage("Error: Engine is not connected.");
                return;
            }

            string provider = _providerComboBox.SelectedItem as string ?? "OpenAI";
            string model = _modelTextBox.Text ?? "";
            bool yolo = _yoloCheckBox.IsChecked == true;

            // Build history list (only include user and assistant text messages for safety)
            var history = new List<AiMessage>();
            foreach (var item in _chatItems)
            {
                if (item is ChatMessageItem msg)
                {
                    history.Add(new AiMessage
                    {
                        Role = msg.Role,
                        Content = msg.Content
                    });
                }
            }

            // Clear input
            _inputTextBox.Text = "";

            // Add user message to UI
            _chatItems.Add(new ChatMessageItem
            {
                Role = "user",
                Content = prompt
            });

            // Create placeholder for assistant streaming response
            _currentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = ""
            };
            _chatItems.Add(_currentAssistantMessage);

            // Scroll to end
            ScrollToEnd();

            // Set running state
            SetRunningState(true);

            // Serialize & send packet
            var request = new AiChatRequest
            {
                Prompt = prompt,
                Provider = provider,
                Model = model,
                YoloMode = yolo,
                History = history
            };

            try
            {
                string json = JsonSerializer.Serialize(request, typeof(AiChatRequest), LocalContractsJsonContext.Default);
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize + 8 + json.Length * sizeof(char)];
                int len = BinaryMessageSerializer.WriteStringPayload(buffer, MessageTypes.AiChatRequest, json);

                byte[] finalBuffer = new byte[len];
                Array.Copy(buffer, 0, finalBuffer, 0, len);

                _engine.Send(finalBuffer);
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Serialization Error: {ex.Message}");
                SetRunningState(false);
            }
        }

        private void StopActiveAgent()
        {
            if (!_isRunning || _engine == null) return;

            try
            {
                byte[] buffer = new byte[BinaryMessageSerializer.HeaderSize];
                int len = BinaryMessageSerializer.WriteAiStopCommand(buffer);
                _engine.Send(buffer);

                AddErrorMessage("Agent stopped by user.");
            }
            catch (Exception ex)
            {
                AddErrorMessage($"Error sending stop command: {ex.Message}");
            }

            SetRunningState(false);
        }

        private void SetRunningState(bool running)
        {
            _isRunning = running;
            _inputTextBox.IsEnabled = !running;
            _sendButton.IsEnabled = !running;
            _stopButton.IsEnabled = running;
            _statusLabel.Text = running ? "Running..." : "Idle";
        }

        private void AddErrorMessage(string text)
        {
            _chatItems.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = text
            });
            ScrollToEnd();
        }

        public void HandleChatResponse(string json)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var response = JsonSerializer.Deserialize<AiChatResponse>(json, LocalContractsJsonContext.Default.AiChatResponse);
                    if (response == null) return;

                    if (!string.IsNullOrEmpty(response.Error))
                    {
                        AddErrorMessage(response.Error);
                        SetRunningState(false);
                        return;
                    }

                    if (!string.IsNullOrEmpty(response.TextToken))
                    {
                        if (_currentAssistantMessage == null)
                        {
                            _currentAssistantMessage = new ChatMessageItem
                            {
                                Role = "assistant",
                                Content = ""
                            };
                            _chatItems.Add(_currentAssistantMessage);
                        }
                        _currentAssistantMessage.Content += response.TextToken;
                        ScrollToEnd();
                    }

                    if (response.IsDone)
                    {
                        _currentAssistantMessage = null;
                        SetRunningState(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AiChatPanel] Error parsing chat response: {ex.Message}");
                }
            });
        }

        public void HandleToolExecutionEvent(string json)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var ev = JsonSerializer.Deserialize<AiToolExecutionEvent>(json, LocalContractsJsonContext.Default.AiToolExecutionEvent);
                    if (ev == null) return;

                    // Locate if we already have this tool call item in the list
                    var toolItem = _chatItems
                        .OfType<ToolExecutionItem>()
                        .FirstOrDefault(t => t.Arguments == ev.Arguments);

                    if (toolItem != null)
                    {
                        toolItem.Status = ev.Status;
                        toolItem.Output = ev.Output;
                        if (ev.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || 
                            ev.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                        {
                            toolItem.IsExpanded = false;
                        }
                    }
                    else
                    {
                        toolItem = new ToolExecutionItem
                        {
                            ToolName = ev.ToolName,
                            Arguments = ev.Arguments, // tool call ID
                            Status = ev.Status,
                            Output = ev.Output
                        };
                        if (ev.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || 
                            ev.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                        {
                            toolItem.IsExpanded = false;
                        }
                        _chatItems.Add(toolItem);

                        // Reset current assistant message to force a new chronological block below the tool
                        _currentAssistantMessage = null;
                    }

                    ScrollToEnd();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AiChatPanel] Error parsing tool execution event: {ex.Message}");
                }
            });
        }

        private void ScrollToEnd()
        {
            Dispatcher.UIThread.Post(() => _chatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }

        private static Control RenderMarkdown(string markdown)
        {
            var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
            
            if (string.IsNullOrEmpty(markdown))
            {
                return stack;
            }

            // Split by code blocks
            var parts = markdown.Split(new[] { "```" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (i % 2 == 1)
                {
                    // Code block
                    int firstNewLine = part.IndexOf('\n');
                    string code = part;
                    if (firstNewLine >= 0)
                    {
                        code = part.Substring(firstNewLine + 1);
                    }

                    var codeBlock = new TextBlock
                    {
                        Text = code.TrimEnd(),
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#E5C07B")),
                        TextWrapping = TextWrapping.NoWrap
                    };

                    var scroll = new ScrollViewer
                    {
                        Content = codeBlock,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Background = new SolidColorBrush(Color.Parse("#141416")),
                        Padding = new Thickness(8),
                        Margin = new Thickness(0, 4, 0, 4)
                    };

                    var border = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#141416")),
                        BorderBrush = new SolidColorBrush(Color.Parse("#2D2D32")),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Child = scroll,
                        Margin = new Thickness(0, 2, 0, 2),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    stack.Children.Add(border);
                }
                else
                {
                    // Paragraphs, Headings, Lists, Dividers
                    var lines = part.Split('\n');
                    var currentParagraph = new System.Text.StringBuilder();

                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();

                        // 1. Empty line -> flush current paragraph
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            if (currentParagraph.Length > 0)
                            {
                                stack.Children.Add(RenderInlineMarkdown(currentParagraph.ToString()));
                                currentParagraph.Clear();
                            }
                            continue;
                        }

                        // 2. Heading check
                        int hashCount = 0;
                        while (hashCount < line.Length && line[hashCount] == '#') hashCount++;
                        if (hashCount > 0 && hashCount < line.Length && line[hashCount] == ' ')
                        {
                            if (currentParagraph.Length > 0)
                            {
                                stack.Children.Add(RenderInlineMarkdown(currentParagraph.ToString()));
                                currentParagraph.Clear();
                            }

                            string headingText = line.Substring(hashCount + 1).Trim();
                            double fontSize = hashCount switch
                            {
                                1 => 16,
                                2 => 14,
                                3 => 12.5,
                                _ => 11.5
                            };

                            var headingBlock = new TextBlock
                            {
                                FontWeight = FontWeight.Bold,
                                FontSize = fontSize,
                                Foreground = Brushes.White,
                                Margin = new Thickness(0, 8, 0, 2)
                            };
                            ParseInlineMarkdown(headingBlock, headingText);
                            stack.Children.Add(headingBlock);
                            continue;
                        }

                        // 3. Horizontal Rule
                        if (line == "---" || line == "***" || line == "___")
                        {
                            if (currentParagraph.Length > 0)
                            {
                                stack.Children.Add(RenderInlineMarkdown(currentParagraph.ToString()));
                                currentParagraph.Clear();
                            }

                            var divider = new Border
                            {
                                Height = 1,
                                Background = new SolidColorBrush(Color.Parse("#2D2D30")),
                                Margin = new Thickness(0, 8, 0, 8),
                                HorizontalAlignment = HorizontalAlignment.Stretch
                            };
                            stack.Children.Add(divider);
                            continue;
                        }

                        // 4. List check
                        var match = System.Text.RegularExpressions.Regex.Match(rawLine, @"^\s*(\*|-|•|\d+\.)\s+(.*)");
                        if (match.Success)
                        {
                            if (currentParagraph.Length > 0)
                            {
                                stack.Children.Add(RenderInlineMarkdown(currentParagraph.ToString()));
                                currentParagraph.Clear();
                            }

                            string marker = match.Groups[1].Value;
                            string itemText = match.Groups[2].Value;
                            stack.Children.Add(RenderListItem(marker, itemText));
                            continue;
                        }

                        // 5. Default line -> append to current paragraph
                        if (currentParagraph.Length > 0)
                        {
                            currentParagraph.Append(" ");
                        }
                        currentParagraph.Append(line);
                    }

                    if (currentParagraph.Length > 0)
                    {
                        stack.Children.Add(RenderInlineMarkdown(currentParagraph.ToString()));
                    }
                }
            }

            return stack;
        }

        private static void ParseInlineMarkdown(TextBlock textBlock, string text)
        {
            if (string.IsNullOrEmpty(text) || textBlock.Inlines == null)
                return;

            int i = 0;
            while (i < text.Length)
            {
                // 1. Check for bold `**` or `__`
                if (i + 1 < text.Length && ((text[i] == '*' && text[i + 1] == '*') || (text[i] == '_' && text[i + 1] == '_')))
                {
                    char marker = text[i];
                    int start = i + 2;
                    int end = text.IndexOf($"{marker}{marker}", start);
                    if (end != -1)
                    {
                        var boldRun = new Avalonia.Controls.Documents.Run(text.Substring(start, end - start))
                        {
                            FontWeight = FontWeight.Bold
                        };
                        textBlock.Inlines.Add(boldRun);
                        i = end + 2;
                        continue;
                    }
                }

                // 2. Check for italic `*` or `_`
                if (text[i] == '*' || text[i] == '_')
                {
                    char marker = text[i];
                    int start = i + 1;
                    int end = text.IndexOf(marker, start);
                    if (end != -1 && end > start)
                    {
                        var italicRun = new Avalonia.Controls.Documents.Run(text.Substring(start, end - start))
                        {
                            FontStyle = FontStyle.Italic
                        };
                        textBlock.Inlines.Add(italicRun);
                        i = end + 1;
                        continue;
                    }
                }

                // 3. Check for inline code `
                if (text[i] == '`')
                {
                    int start = i + 1;
                    int end = text.IndexOf('`', start);
                    if (end != -1)
                    {
                        var codeRun = new Avalonia.Controls.Documents.Run(text.Substring(start, end - start))
                        {
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = new SolidColorBrush(Color.Parse("#E5C07B")),
                            Background = new SolidColorBrush(Color.Parse("#262629"))
                        };
                        textBlock.Inlines.Add(codeRun);
                        i = end + 1;
                        continue;
                    }
                }

                // 4. Default: accumulate plain text
                int nextSpecial = text.IndexOfAny(new[] { '*', '_', '`' }, i);
                if (nextSpecial == -1)
                {
                    textBlock.Inlines.Add(new Avalonia.Controls.Documents.Run(text.Substring(i)));
                    break;
                }
                else
                {
                    textBlock.Inlines.Add(new Avalonia.Controls.Documents.Run(text.Substring(i, nextSpecial - i)));
                    i = nextSpecial;
                }
            }
        }

        private static Control RenderInlineMarkdown(string text)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brushes.LightGray
            };
            ParseInlineMarkdown(textBlock, text);
            return textBlock;
        }

        private static Control RenderListItem(string marker, string text)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            // Standardize bullet marker
            string displayMarker = marker switch
            {
                "*" => "•",
                "-" => "•",
                "•" => "•",
                _ => marker // Numbered list like "1."
            };

            var bullet = new TextBlock
            {
                Text = displayMarker,
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Top
            };
            grid.Children.Add(bullet);
            Grid.SetColumn(bullet, 0);

            var content = RenderInlineMarkdown(text);
            grid.Children.Add(content);
            Grid.SetColumn(content, 1);

            return grid;
        }
    }

    #region Local JSON Context (Native AOT Safe)
    [JsonSerializable(typeof(AiChatRequest))]
    [JsonSerializable(typeof(AiChatResponse))]
    [JsonSerializable(typeof(AiToolExecutionEvent))]
    [JsonSerializable(typeof(AiMessage))]
    [JsonSerializable(typeof(List<AiMessage>))]
    public partial class LocalContractsJsonContext : JsonSerializerContext
    {
    }
    #endregion
}
