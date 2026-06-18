using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using SpanCoder.Engine;
using SpanCoder.Contracts;
using SpanCoder.Shell;

namespace SpanCoder.App
{
    public class App : Application
    {
        private readonly Stopwatch? _startupStopwatch;
        private IEngineConnection? _engine;
        private ExtensionManager? _extensionManager;

        public App()
        {
        }

        public App(Stopwatch? startupStopwatch)
        {
            _startupStopwatch = startupStopwatch;
        }

        public override void Initialize()
        {
            // Set up Fluent Theme without XAML
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                string? remoteHost = null;
                int remotePort = 0;
                string? pathMapping = null;

                if (desktop.Args != null)
                {
                    for (int i = 0; i < desktop.Args.Length; i++)
                    {
                        if (desktop.Args[i] == "--connect" && i + 1 < desktop.Args.Length)
                        {
                            var connectVal = desktop.Args[i + 1];
                            int colonIdx = connectVal.LastIndexOf(':');
                            if (colonIdx > 0 && int.TryParse(connectVal.Substring(colonIdx + 1), out int port))
                            {
                                remoteHost = connectVal.Substring(0, colonIdx);
                                remotePort = port;
                            }
                            i++;
                        }
                        else if (desktop.Args[i] == "--map-path" && i + 1 < desktop.Args.Length)
                        {
                            pathMapping = desktop.Args[i + 1];
                            i++;
                        }
                    }
                }

                // Instantiate and start IPC socket connection to spawned or remote engine
                IpcEngineConnection ipc;
                if (!string.IsNullOrEmpty(remoteHost))
                {
                    ipc = new IpcEngineConnection(remoteHost, remotePort, pathMapping);
                }
                else
                {
                    ipc = new IpcEngineConnection();
                }
                ipc.Start();
                _engine = ipc;

                // Build UI Window and connect to engine
                var window = new ShellWindow(_startupStopwatch ?? Stopwatch.StartNew());
                window.InitializeLayout();
                window.ConnectEngine(_engine);

                // Instantiate and start extension manager
                var pluginsDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
                _extensionManager = new ExtensionManager(pluginsDir);
                window.ConnectExtensions(_extensionManager);
                _extensionManager.Start();

                window.Closed += (sender, args) =>
                {
                    if (_engine is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    _extensionManager?.Dispose();
                };

                desktop.MainWindow = window;
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
