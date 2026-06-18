#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SpanCoder.SourceGenerators
{
    [Generator]
    public class CommandGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var methodProvider = context.SyntaxProvider.CreateSyntaxProvider(
                (node, _) => node is MethodDeclarationSyntax && ((MethodDeclarationSyntax)node).AttributeLists.Count > 0,
                (ctx, _) => GetCommandMethodInfo(ctx)
            ).Where(m => m != null).Select((m, _) => m!);

            var classProvider = context.SyntaxProvider.CreateSyntaxProvider(
                (node, _) => node is ClassDeclarationSyntax && ((ClassDeclarationSyntax)node).AttributeLists.Count > 0,
                (ctx, _) => GetPluginClassInfo(ctx)
            ).Where(c => c != null).Select((c, _) => c!);

            var combined = methodProvider.Collect().Combine(classProvider.Collect());

            // Register the source output
            context.RegisterSourceOutput(combined, (spc, source) =>
            {
                var commands = source.Left;
                var plugins = source.Right;

                if (commands.IsDefaultOrEmpty)
                {
                    GenerateEmptyRegistry(spc);
                }
                else
                {
                    GenerateRegistry(spc, commands);
                }

                if (!plugins.IsDefaultOrEmpty)
                {
                    GeneratePluginHost(spc, plugins[0], commands);
                }
            });
        }

        private static CommandMethodInfo? GetCommandMethodInfo(GeneratorSyntaxContext context)
        {
            var methodSyntax = (MethodDeclarationSyntax)context.Node;
            var methodSymbol = context.SemanticModel.GetDeclaredSymbol(methodSyntax) as IMethodSymbol;
            if (methodSymbol == null) return null;

            AttributeData? commandAttr = null;
            var menuItems = new List<AttributeData>();

            foreach (var attr in methodSymbol.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                if (attrName == "SpanCoder.Contracts.CommandAttribute")
                {
                    commandAttr = attr;
                }
                else if (attrName == "SpanCoder.Contracts.MenuItemAttribute")
                {
                    menuItems.Add(attr);
                }
            }

            if (commandAttr == null) return null;

            // Extract constructor arguments
            string id = commandAttr.ConstructorArguments.Length > 0 ? commandAttr.ConstructorArguments[0].Value as string ?? "" : "";
            string displayName = commandAttr.ConstructorArguments.Length > 1 ? commandAttr.ConstructorArguments[1].Value as string ?? "" : "";
            string category = commandAttr.ConstructorArguments.Length > 2 ? commandAttr.ConstructorArguments[2].Value as string ?? "" : "";
            string defaultShortcut = commandAttr.ConstructorArguments.Length > 3 ? commandAttr.ConstructorArguments[3].Value as string ?? "" : "";

            // Handle named arguments (optional fallback)
            foreach (var arg in commandAttr.NamedArguments)
            {
                if (arg.Key == "Category") category = arg.Value.Value as string ?? "";
                if (arg.Key == "DefaultShortcut") defaultShortcut = arg.Value.Value as string ?? "";
            }

            var menuList = new List<MenuItemInfo>();
            foreach (var attr in menuItems)
            {
                string menuCmdId = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
                string menuPath = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as string ?? "" : "";
                int order = 100;

                if (attr.ConstructorArguments.Length > 2)
                    order = Convert.ToInt32(attr.ConstructorArguments[2].Value ?? 100);

                foreach (var arg in attr.NamedArguments)
                {
                    if (arg.Key == "OrderPriority") order = Convert.ToInt32(arg.Value.Value ?? 100);
                }

                menuList.Add(new MenuItemInfo(menuCmdId, menuPath, order));
            }

            string fullyQualifiedName = methodSymbol.ContainingType.ToDisplayString();
            string methodName = methodSymbol.Name;
            bool hasParameter = methodSymbol.Parameters.Length > 0;
            string parameterType = hasParameter ? methodSymbol.Parameters[0].Type.ToDisplayString() : "";

            return new CommandMethodInfo(
                id, displayName, category, defaultShortcut,
                fullyQualifiedName, methodName, hasParameter, parameterType,
                menuList
            );
        }

        private static void GenerateEmptyRegistry(SourceProductionContext spc)
        {
            var code = @"// <auto-generated />
using System;
using SpanCoder.Contracts;

namespace SpanCoder.Contracts
{
    public static class GeneratedCommandRegistry
    {
        public static readonly CommandDescriptor[] Commands = Array.Empty<CommandDescriptor>();
        public static readonly MenuItemDescriptor[] MenuItems = Array.Empty<MenuItemDescriptor>();

        public static bool Dispatch(string commandId, object context)
        {
            return false;
        }
    }
}
";
            spc.AddSource("GeneratedCommandRegistry.g.cs", SourceText.From(code, Encoding.UTF8));
        }

        private static void GenerateRegistry(SourceProductionContext spc, System.Collections.Immutable.ImmutableArray<CommandMethodInfo> commands)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using System;");
            sb.AppendLine("using SpanCoder.Contracts;");
            sb.AppendLine();
            sb.AppendLine("namespace SpanCoder.Contracts");
            sb.AppendLine("{");
            sb.AppendLine("    public static class GeneratedCommandRegistry");
            sb.AppendLine("    {");

            // Write Commands Array
            sb.AppendLine("        public static readonly CommandDescriptor[] Commands = new CommandDescriptor[]");
            sb.AppendLine("        {");
            foreach (var cmd in commands)
            {
                sb.AppendLine($"            new CommandDescriptor(\"{cmd.Id}\", \"{cmd.DisplayName}\", \"{cmd.Category}\", \"{cmd.DefaultShortcut}\"),");
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Write MenuItems Array
            sb.AppendLine("        public static readonly MenuItemDescriptor[] MenuItems = new MenuItemDescriptor[]");
            sb.AppendLine("        {");
            foreach (var cmd in commands)
            {
                foreach (var menu in cmd.MenuItems)
                {
                    sb.AppendLine($"            new MenuItemDescriptor(\"{menu.CommandId}\", \"{menu.MenuPath}\", {menu.OrderPriority}),");
                }
            }
            sb.AppendLine("        };");
            sb.AppendLine();

            // Write Dispatch Method
            sb.AppendLine("        public static bool Dispatch(string commandId, object context)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (commandId)");
            sb.AppendLine("            {");

            foreach (var cmd in commands)
            {
                sb.AppendLine($"                case \"{cmd.Id}\":");
                if (cmd.HasParameter)
                {
                    sb.AppendLine($"                    {cmd.FullyQualifiedClassName}.{cmd.MethodName}(({cmd.ParameterType})context);");
                }
                else
                {
                    sb.AppendLine($"                    {cmd.FullyQualifiedClassName}.{cmd.MethodName}();");
                }
                sb.AppendLine("                    return true;");
            }

            sb.AppendLine("                default:");
            sb.AppendLine("                    return false;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource("GeneratedCommandRegistry.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }

        private static PluginClassInfo? GetPluginClassInfo(GeneratorSyntaxContext context)
        {
            var classSyntax = (ClassDeclarationSyntax)context.Node;
            var classSymbol = context.SemanticModel.GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
            if (classSymbol == null) return null;

            AttributeData? pluginAttr = null;
            var panelAttrs = new List<AttributeData>();
            var settingAttrs = new List<AttributeData>();
            var statusBarAttrs = new List<AttributeData>();

            foreach (var attr in classSymbol.GetAttributes())
            {
                var attrName = attr.AttributeClass?.ToDisplayString();
                if (attrName == "SpanCoder.Contracts.SpanCoderPluginAttribute")
                {
                    pluginAttr = attr;
                }
                else if (attrName == "SpanCoder.Contracts.CustomPanelAttribute")
                {
                    panelAttrs.Add(attr);
                }
                else if (attrName == "SpanCoder.Contracts.SettingAttribute")
                {
                    settingAttrs.Add(attr);
                }
                else if (attrName == "SpanCoder.Contracts.StatusBarItemAttribute")
                {
                    statusBarAttrs.Add(attr);
                }
            }

            if (pluginAttr == null) return null;

            string pluginId = pluginAttr.ConstructorArguments.Length > 0 ? pluginAttr.ConstructorArguments[0].Value as string ?? "" : "";
            string ns = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            string className = classSymbol.Name;

            var panels = new List<PanelInfo>();
            foreach (var attr in panelAttrs)
            {
                string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
                string title = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as string ?? "" : "";
                panels.Add(new PanelInfo(id, title));
            }

            var settings = new List<SettingInfo>();
            foreach (var attr in settingAttrs)
            {
                string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
                string displayName = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as string ?? "" : "";
                string type = attr.ConstructorArguments.Length > 2 ? attr.ConstructorArguments[2].Value as string ?? "string" : "string";
                string defaultValue = attr.ConstructorArguments.Length > 3 ? attr.ConstructorArguments[3].Value as string ?? "" : "";
                settings.Add(new SettingInfo(id, displayName, type, defaultValue));
            }

            var statusBarItems = new List<StatusBarItemInfo>();
            foreach (var attr in statusBarAttrs)
            {
                string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
                string text = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as string ?? "" : "";
                string tooltip = attr.ConstructorArguments.Length > 2 ? attr.ConstructorArguments[2].Value as string ?? "" : "";
                string commandId = attr.ConstructorArguments.Length > 3 ? attr.ConstructorArguments[3].Value as string ?? "" : "";
                int alignment = attr.ConstructorArguments.Length > 4 ? Convert.ToInt32(attr.ConstructorArguments[4].Value ?? 1) : 1;
                int orderPriority = attr.ConstructorArguments.Length > 5 ? Convert.ToInt32(attr.ConstructorArguments[5].Value ?? 100) : 100;
                statusBarItems.Add(new StatusBarItemInfo(id, text, tooltip, commandId, alignment, orderPriority));
            }

            return new PluginClassInfo(pluginId, ns, className, panels, settings, statusBarItems);
        }

        private static void GeneratePluginHost(
            SourceProductionContext spc, 
            PluginClassInfo plugin, 
            System.Collections.Immutable.ImmutableArray<CommandMethodInfo> commands)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated />");
            sb.AppendLine("using System;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Net.Sockets;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine("using System.Text.Json;");
            sb.AppendLine("using System.Threading;");
            sb.AppendLine("using System.Threading.Tasks;");
            sb.AppendLine("using SpanCoder.Contracts;");
            sb.AppendLine();
            sb.AppendLine("namespace " + plugin.Namespace);
            sb.AppendLine("{");
            sb.AppendLine("    public static class SpanCoderPluginHost");
            sb.AppendLine("    {");
            sb.AppendLine("        private static TcpClient _client;");
            sb.AppendLine("        private static NetworkStream _stream;");
            sb.AppendLine("        private static readonly object _writeLock = new object();");
            sb.AppendLine();
            sb.AppendLine("        public static void Start(string[] args)");
            sb.AppendLine("        {");
            sb.AppendLine("            int port = 0;");
            sb.AppendLine("            for (int i = 0; i < args.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (args[i] == \"--port\" && i + 1 < args.Length)");
            sb.AppendLine("                {");
            sb.AppendLine("                    int.TryParse(args[i + 1], out port);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            if (port == 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                Console.WriteLine(\"Usage: plugin --port <port>\");");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                _client = new TcpClient();");
            sb.AppendLine("                _client.Connect(\"127.0.0.1\", port);");
            sb.AppendLine("                _stream = _client.GetStream();");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex)");
            sb.AppendLine("            {");
            sb.AppendLine("                Console.WriteLine(\"[Plugin] Failed to connect to host: \" + ex.Message);");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            // Send RegisterExtension message");
            sb.AppendLine("            SendRegistration();");
            sb.AppendLine();
            sb.AppendLine("            // Start Read Loop");
            sb.AppendLine("            try");
            sb.AppendLine("            {");
            sb.AppendLine("                byte[] headerBuffer = new byte[BinaryMessageSerializer.HeaderSize];");
            sb.AppendLine("                while (_client.Connected)");
            sb.AppendLine("                {");
            sb.AppendLine("                    ReadExactly(_stream, headerBuffer, 0, headerBuffer.Length);");
            sb.AppendLine("                    if (!BinaryMessageSerializer.TryParseHeader(headerBuffer, out var header))");
            sb.AppendLine("                        throw new InvalidDataException(\"Invalid header\");");
            sb.AppendLine();
            sb.AppendLine("                    byte[] payload = new byte[header.Length];");
            sb.AppendLine("                    Array.Copy(headerBuffer, 0, payload, 0, headerBuffer.Length);");
            sb.AppendLine("                    if (header.Length > headerBuffer.Length)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        ReadExactly(_stream, payload, headerBuffer.Length, header.Length - headerBuffer.Length);");
            sb.AppendLine("                    }");
            sb.AppendLine();
            sb.AppendLine("                    if (header.Type == MessageTypes.ExecuteExtensionCommand)");
            sb.AppendLine("                    {");
            sb.AppendLine("                        string commandId = BinaryMessageSerializer.ParseExecuteExtensionCommand(payload);");
            sb.AppendLine("                        DispatchCommand(commandId);");
            sb.AppendLine("                    }");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine("            catch (Exception ex)");
            sb.AppendLine("            {");
            sb.AppendLine("                Console.WriteLine(\"[Plugin] Closed: \" + ex.Message);");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)");
            sb.AppendLine("        {");
            sb.AppendLine("            int total = 0;");
            sb.AppendLine("            while (total < count)");
            sb.AppendLine("            {");
            sb.AppendLine("                int r = stream.Read(buffer, offset + total, count - total);");
            sb.AppendLine("                if (r <= 0) throw new IOException(\"Connection closed\");");
            sb.AppendLine("                total += r;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public static void UpdatePanel(string panelId, string content)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (_stream == null) return;");
            sb.AppendLine("            byte[] temp = new byte[BinaryMessageSerializer.HeaderSize + 8 + (panelId.Length + content.Length) * sizeof(char)];");
            sb.AppendLine("            int len = BinaryMessageSerializer.WriteUpdateExtensionPanel(temp, panelId, content);");
            sb.AppendLine("            byte[] payload = new byte[len];");
            sb.AppendLine("            Array.Copy(temp, 0, payload, 0, len);");
            sb.AppendLine("            lock (_writeLock)");
            sb.AppendLine("            {");
            sb.AppendLine("                _stream.Write(payload, 0, len);");
            sb.AppendLine("                _stream.Flush();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static void SendRegistration()");
            sb.AppendLine("        {");

            var jsonBuilder = new StringBuilder();
            jsonBuilder.Append("{");
            jsonBuilder.Append($"\\\"id\\\":\\\"{plugin.PluginId}\\\",");
            
            jsonBuilder.Append("\\\"commands\\\":[");
            for (int i = 0; i < commands.Length; i++)
            {
                var cmd = commands[i];
                if (i > 0) jsonBuilder.Append(",");
                jsonBuilder.Append("{");
                jsonBuilder.Append($"\\\"id\\\":\\\"{cmd.Id}\\\",");
                jsonBuilder.Append($"\\\"displayName\\\":\\\"{cmd.DisplayName}\\\",");
                jsonBuilder.Append($"\\\"category\\\":\\\"{cmd.Category}\\\",");
                jsonBuilder.Append($"\\\"defaultShortcut\\\":\\\"{cmd.DefaultShortcut}\\\"");
                jsonBuilder.Append("}");
            }
            jsonBuilder.Append("],");

            jsonBuilder.Append("\\\"menuItems\\\":[");
            bool firstMenu = true;
            for (int i = 0; i < commands.Length; i++)
            {
                var cmd = commands[i];
                foreach (var menu in cmd.MenuItems)
                {
                    if (!firstMenu) jsonBuilder.Append(",");
                    firstMenu = false;
                    jsonBuilder.Append("{");
                    jsonBuilder.Append($"\\\"commandId\\\":\\\"{menu.CommandId}\\\",");
                    jsonBuilder.Append($"\\\"menuPath\\\":\\\"{menu.MenuPath}\\\",");
                    jsonBuilder.Append($"\\\"orderPriority\\\":{menu.OrderPriority}");
                    jsonBuilder.Append("}");
                }
            }
            jsonBuilder.Append("],");

            jsonBuilder.Append("\\\"panels\\\":[");
            for (int i = 0; i < plugin.Panels.Count; i++)
            {
                var p = plugin.Panels[i];
                if (i > 0) jsonBuilder.Append(",");
                jsonBuilder.Append("{");
                jsonBuilder.Append($"\\\"id\\\":\\\"{p.Id}\\\",");
                jsonBuilder.Append($"\\\"title\\\":\\\"{p.Title}\\\"");
                jsonBuilder.Append("}");
            }
            jsonBuilder.Append("],");
            jsonBuilder.Append("\\\"languages\\\":[],");
            jsonBuilder.Append("\\\"toolbarItems\\\":[],");

            jsonBuilder.Append("\\\"settings\\\":[");
            for (int i = 0; i < plugin.Settings.Count; i++)
            {
                var s = plugin.Settings[i];
                if (i > 0) jsonBuilder.Append(",");
                jsonBuilder.Append("{");
                jsonBuilder.Append($"\\\"id\\\":\\\"{s.Id}\\\",");
                jsonBuilder.Append($"\\\"displayName\\\":\\\"{s.DisplayName}\\\",");
                jsonBuilder.Append($"\\\"type\\\":\\\"{s.Type}\\\",");
                jsonBuilder.Append($"\\\"defaultValue\\\":\\\"{s.DefaultValue}\\\"");
                jsonBuilder.Append("}");
            }
            jsonBuilder.Append("],");

            jsonBuilder.Append("\\\"statusBarItems\\\":[");
            for (int i = 0; i < plugin.StatusBarItems.Count; i++)
            {
                var s = plugin.StatusBarItems[i];
                if (i > 0) jsonBuilder.Append(",");
                jsonBuilder.Append("{");
                jsonBuilder.Append($"\\\"id\\\":\\\"{s.Id}\\\",");
                jsonBuilder.Append($"\\\"text\\\":\\\"{s.Text}\\\",");
                jsonBuilder.Append($"\\\"tooltip\\\":\\\"{s.Tooltip}\\\",");
                jsonBuilder.Append($"\\\"commandId\\\":\\\"{s.CommandId}\\\",");
                jsonBuilder.Append($"\\\"alignment\\\":{s.Alignment},");
                jsonBuilder.Append($"\\\"orderPriority\\\":{s.OrderPriority}");
                jsonBuilder.Append("}");
            }
            jsonBuilder.Append("]");
            jsonBuilder.Append("}");

            sb.AppendLine("            string manifestJson = \"" + jsonBuilder.ToString() + "\";");
            sb.AppendLine("            byte[] jsonBytes = Encoding.UTF8.GetBytes(manifestJson);");
            sb.AppendLine("            byte[] temp = new byte[BinaryMessageSerializer.HeaderSize + sizeof(int) + jsonBytes.Length];");
            sb.AppendLine("            int len = BinaryMessageSerializer.WriteRegisterExtension(temp, jsonBytes);");
            sb.AppendLine("            lock (_writeLock)");
            sb.AppendLine("            {");
            sb.AppendLine("                _stream.Write(temp, 0, len);");
            sb.AppendLine("                _stream.Flush();");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static void DispatchCommand(string commandId)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch (commandId)");
            sb.AppendLine("            {");
            foreach (var cmd in commands)
            {
                sb.AppendLine("                case \"" + cmd.Id + "\":");
                sb.AppendLine("                    " + cmd.FullyQualifiedClassName + "." + cmd.MethodName + "();");
                sb.AppendLine("                    break;");
            }
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource("SpanCoderPluginHost.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
        }
    }

    public sealed class CommandMethodInfo
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public string DefaultShortcut { get; }
        public string FullyQualifiedClassName { get; }
        public string MethodName { get; }
        public bool HasParameter { get; }
        public string ParameterType { get; }
        public List<MenuItemInfo> MenuItems { get; }

        public CommandMethodInfo(
            string id, string displayName, string category, string defaultShortcut,
            string className, string methodName, bool hasParameter, string parameterType,
            List<MenuItemInfo> menuItems)
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            DefaultShortcut = defaultShortcut;
            FullyQualifiedClassName = className;
            MethodName = methodName;
            HasParameter = hasParameter;
            ParameterType = parameterType;
            MenuItems = menuItems;
        }
    }

    public sealed class MenuItemInfo
    {
        public string CommandId { get; }
        public string MenuPath { get; }
        public int OrderPriority { get; }

        public MenuItemInfo(string commandId, string menuPath, int orderPriority)
        {
            CommandId = commandId;
            MenuPath = menuPath;
            OrderPriority = orderPriority;
        }
    }

    public sealed class PluginClassInfo
    {
        public string PluginId { get; }
        public string Namespace { get; }
        public string ClassName { get; }
        public List<PanelInfo> Panels { get; }
        public List<SettingInfo> Settings { get; }
        public List<StatusBarItemInfo> StatusBarItems { get; }

        public PluginClassInfo(string pluginId, string ns, string className, List<PanelInfo> panels, List<SettingInfo> settings, List<StatusBarItemInfo> statusBarItems)
        {
            PluginId = pluginId;
            Namespace = ns;
            ClassName = className;
            Panels = panels;
            Settings = settings;
            StatusBarItems = statusBarItems;
        }
    }

    public sealed class SettingInfo
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Type { get; }
        public string DefaultValue { get; }

        public SettingInfo(string id, string displayName, string type, string defaultValue)
        {
            Id = id;
            DisplayName = displayName;
            Type = type;
            DefaultValue = defaultValue;
        }
    }

    public sealed class StatusBarItemInfo
    {
        public string Id { get; }
        public string Text { get; }
        public string Tooltip { get; }
        public string CommandId { get; }
        public int Alignment { get; }
        public int OrderPriority { get; }

        public StatusBarItemInfo(string id, string text, string tooltip, string commandId, int alignment, int orderPriority)
        {
            Id = id;
            Text = text;
            Tooltip = tooltip;
            CommandId = commandId;
            Alignment = alignment;
            OrderPriority = orderPriority;
        }
    }

    public sealed class PanelInfo
    {
        public string Id { get; }
        public string Title { get; }

        public PanelInfo(string id, string title)
        {
            Id = id;
            Title = title;
        }
    }
}
