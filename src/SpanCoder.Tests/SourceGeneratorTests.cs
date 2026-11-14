using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SpanCoder.Contracts;
using SpanCoder.SourceGenerators;
using Xunit;

namespace SpanCoder.Tests
{
    public class SourceGeneratorTests
    {
        private static (Compilation OutputCompilation, GeneratorDriverRunResult RunResult) RunGenerator(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);

            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CommandAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Net.Sockets.TcpClient).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
                MetadataReference.CreateFromFile(Assembly.Load("System.Net.Primitives").Location),
            };

            var compilation = CSharpCompilation.Create(
                assemblyName: "GeneratorTestAssembly",
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var generator = new CommandGenerator();
            GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            return (outputCompilation, driver.GetRunResult());
        }

        [Fact]
        public void EmptyCompilation_GeneratesEmptyCommandRegistry()
        {
            var (outputCompilation, runResult) = RunGenerator("namespace Demo { public class Empty {} }");

            var errors = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.Empty(errors);

            var generated = Assert.Single(runResult.GeneratedTrees);
            Assert.EndsWith("GeneratedCommandRegistry.g.cs", generated.FilePath);

            string text = generated.GetText().ToString();
            Assert.Contains("Array.Empty<CommandDescriptor>()", text);
            Assert.Contains("Array.Empty<MenuItemDescriptor>()", text);
            Assert.Contains("return false;", text);
        }

        [Fact]
        public void CommandsAndMenuItems_GenerateCompilableAndExecutableRegistry()
        {
            const string source = @"
using SpanCoder.Contracts;

namespace DemoApp
{
    public static class SampleCommands
    {
        public static int NoArgCalls;
        public static string? LastContext;

        [Command(""demo.ping"", ""Ping"", ""Diagnostics"", ""Ctrl+P"")]
        [MenuItem(""demo.ping"", ""Tools/Ping"", 42)]
        public static void Ping()
        {
            NoArgCalls++;
        }

        [Command(""demo.echo"", ""Echo"", ""Tools"", ""Ctrl+E"")]
        [MenuItem(""demo.echo"", ""Tools/Echo"", 99)]
        public static void Echo(string ctx)
        {
            LastContext = ctx;
        }
    }
}";

            var (outputCompilation, runResult) = RunGenerator(source);

            var errors = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();
            Assert.Empty(errors);

            var registryTree = runResult.GeneratedTrees.Single(t => t.FilePath.EndsWith("GeneratedCommandRegistry.g.cs"));
            string text = registryTree.GetText().ToString();
            Assert.Contains("new CommandDescriptor(\"demo.ping\", \"Ping\", \"Diagnostics\", \"Ctrl+P\")", text);
            Assert.Contains("new CommandDescriptor(\"demo.echo\", \"Echo\", \"Tools\", \"Ctrl+E\")", text);
            Assert.Contains("new MenuItemDescriptor(\"demo.ping\", \"Tools/Ping\", 42)", text);
            Assert.Contains("new MenuItemDescriptor(\"demo.echo\", \"Tools/Echo\", 99)", text);

            using var ms = new MemoryStream();
            var emitResult = outputCompilation.Emit(ms);
            Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics));

            ms.Position = 0;
            var asm = Assembly.Load(ms.ToArray());

            var registryType = asm.GetType("SpanCoder.Contracts.GeneratedCommandRegistry")!;
            var commands = (CommandDescriptor[])registryType.GetField("Commands")!.GetValue(null)!;
            var menuItems = (MenuItemDescriptor[])registryType.GetField("MenuItems")!.GetValue(null)!;
            Assert.Equal(2, commands.Length);
            Assert.Equal(2, menuItems.Length);

            var dispatch = registryType.GetMethod("Dispatch")!;
            Assert.True((bool)dispatch.Invoke(null, new object[] { "demo.ping", "" })!);
            Assert.True((bool)dispatch.Invoke(null, new object[] { "demo.echo", "hello" })!);
            Assert.False((bool)dispatch.Invoke(null, new object[] { "demo.unknown", "" })!);

            var sampleType = asm.GetType("DemoApp.SampleCommands")!;
            Assert.Equal(1, (int)sampleType.GetField("NoArgCalls")!.GetValue(null)!);
            Assert.Equal("hello", (string?)sampleType.GetField("LastContext")!.GetValue(null));
        }

        [Fact]
        public void PluginAttribute_GeneratesSpanCoderPluginHostWithManifest()
        {
            const string source = @"
using SpanCoder.Contracts;

namespace DemoPlugin
{
    [SpanCoderPlugin(""sample-plugin"")]
    [CustomPanel(""sample-panel"", ""Sample Panel"")]
    [Setting(""sample.enabled"", ""Enable Sample"", ""boolean"", ""true"")]
    [StatusBarItem(""sample-status"", ""Ready"", ""Sample Tooltip"", ""sample.run"", 1, 50)]
    public static class MyPlugin
    {
        [Command(""sample.run"", ""Run Sample"", ""Sample"", ""Ctrl+R"")]
        [MenuItem(""sample.run"", ""Plugins/Run Sample"", 10)]
        public static void Run() { }
    }
}";

            var (_, runResult) = RunGenerator(source);

            var hostTree = runResult.GeneratedTrees.SingleOrDefault(t => t.FilePath.EndsWith("SpanCoderPluginHost.g.cs"));
            Assert.NotNull(hostTree);

            string hostText = hostTree!.GetText().ToString();
            Assert.Contains("namespace DemoPlugin", hostText);
            Assert.Contains("public static class SpanCoderPluginHost", hostText);
            Assert.Contains("sample-plugin", hostText);
            Assert.Contains("sample-panel", hostText);
            Assert.Contains("sample.enabled", hostText);
            Assert.Contains("sample-status", hostText);
            Assert.Contains("DemoPlugin.MyPlugin.Run();", hostText);
        }

        [Fact]
        public void GeneratorInfoModels_PreserveConstructorProperties()
        {
            var menu = new MenuItemInfo("cmd.id", "File/Test", 25);
            var cmd = new CommandMethodInfo("cmd.id", "Test", "File", "Ctrl+T", "Ns.Type", "Run", true, "string", new List<MenuItemInfo> { menu });
            var panel = new PanelInfo("p1", "Panel 1");
            var setting = new SettingInfo("s1", "Setting 1", "boolean", "false");
            var status = new StatusBarItemInfo("st1", "Idle", "Tip", "cmd.id", 0, 10);
            var plugin = new PluginClassInfo("plug", "Ns", "PlugClass", new List<PanelInfo> { panel }, new List<SettingInfo> { setting }, new List<StatusBarItemInfo> { status });

            Assert.Equal("cmd.id", cmd.Id);
            Assert.True(cmd.HasParameter);
            Assert.Equal("string", cmd.ParameterType);
            Assert.Single(cmd.MenuItems);
            Assert.Equal("plug", plugin.PluginId);
            Assert.Single(plugin.Panels);
            Assert.Single(plugin.Settings);
            Assert.Single(plugin.StatusBarItems);
        }
    }
}
