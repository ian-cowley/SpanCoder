using System;
using System.Collections.Generic;
using System.IO;
using SpanCoder.Contracts;
using SpanCoder.Shell;
using Xunit;

namespace SpanCoder.Tests
{
    public class ShellComponentsTests
    {
        [Fact]
        public void LanguageConfigurationRegistry_BuiltInAndCustomRoundtrip()
        {
            var cs = LanguageConfigurationRegistry.Get(".cs");
            Assert.Equal("//", cs.LineComment);
            Assert.Equal("/*", cs.BlockCommentStart);
            Assert.Equal("*/", cs.BlockCommentEnd);

            var html = LanguageConfigurationRegistry.Get("HTML");
            Assert.Null(html.LineComment);
            Assert.Equal("<!--", html.BlockCommentStart);
            Assert.Equal("-->", html.BlockCommentEnd);

            var unknown = LanguageConfigurationRegistry.Get(".unknown_ext_xyz");
            Assert.Null(unknown.LineComment);
            Assert.Null(unknown.BlockCommentStart);

            LanguageConfigurationRegistry.Register(new LanguageConfigDescriptor(
                ".zig", "//", null, null,
                new List<string> { "fn", "const" },
                new List<string> { "u32", "usize" }));

            var zig = LanguageConfigurationRegistry.Get("zig");
            Assert.Equal("//", zig.LineComment);
            Assert.Contains("fn", zig.Keywords);
            Assert.Contains("u32", zig.Types);

            LanguageConfigurationRegistry.Unregister(".zig");
            Assert.Null(LanguageConfigurationRegistry.Get(".zig").LineComment);
        }

        [Fact]
        public void CoberturaParser_HandlesEmptyMissingAndRelativePaths()
        {
            Assert.Empty(CoberturaParser.Parse(""));
            Assert.Empty(CoberturaParser.ParseFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml")));

            const string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage>
  <packages>
    <package name=""Demo"">
      <classes>
        <class name=""Demo.Foo"" filename=""src/Foo.cs"">
          <lines>
            <line number=""10"" hits=""3"" />
            <line number=""11"" hits=""0"" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>";

            string root = Path.Combine(Path.GetTempPath(), "SpanCoder_Cov_" + Guid.NewGuid().ToString("N"));
            var parsed = CoberturaParser.Parse(xml, root);
            string expectedPath = Path.GetFullPath(Path.Combine(root, "src/Foo.cs"));

            Assert.True(parsed.TryGetValue(expectedPath, out var lines));
            Assert.True(lines![10]);
            Assert.False(lines[11]);
        }

        [Fact]
        public void DiffAlgorithm_EdgeCases_EmptyAndIdentical()
        {
            Assert.Empty(DiffAlgorithm.ComputeDiff(Array.Empty<string>(), Array.Empty<string>()));

            var allAdded = DiffAlgorithm.ComputeDiff(Array.Empty<string>(), new[] { "a", "b" });
            Assert.Equal(2, allAdded.Count);
            Assert.All(allAdded, l => Assert.Equal(DiffType.Added, l.Type));

            var allDeleted = DiffAlgorithm.ComputeDiff(new[] { "a", "b" }, Array.Empty<string>());
            Assert.Equal(2, allDeleted.Count);
            Assert.All(allDeleted, l => Assert.Equal(DiffType.Deleted, l.Type));

            var unchanged = DiffAlgorithm.ComputeDiff(new[] { "x", "y" }, new[] { "x", "y" });
            Assert.Equal(2, unchanged.Count);
            Assert.All(unchanged, l => Assert.Equal(DiffType.Unchanged, l.Type));
        }

        [Fact]
        public void DocumentLexer_TracksMultiLineStatesAcrossLanguages()
        {
            Assert.Equal(LineState.InHtmlComment, DocumentLexer.ComputeEndState("<!-- start".AsSpan(), ".html", LineState.Normal));
            Assert.Equal(LineState.Normal, DocumentLexer.ComputeEndState("end -->".AsSpan(), ".html", LineState.InHtmlComment));

            Assert.Equal(LineState.InJsBlockComment, DocumentLexer.ComputeEndState("/* js comment".AsSpan(), ".js", LineState.Normal));
            Assert.Equal(LineState.Normal, DocumentLexer.ComputeEndState("done */".AsSpan(), ".js", LineState.InJsBlockComment));

            var plain = new PlainLexer("hello world".AsSpan());
            Assert.True(plain.NextToken(out var tok));
            Assert.Equal(TokenType.Text, tok.Type);
            Assert.Equal(11, tok.Length);
            Assert.False(plain.NextToken(out _));
        }

        [Fact]
        public void KeybindingsManager_SetResetAndEventNotification()
        {
            const string cmdId = "test.shell.customCommand";
            int eventCount = 0;
            Action handler = () => eventCount++;
            KeybindingsManager.KeybindingsChanged += handler;

            try
            {
                KeybindingsManager.ResetShortcut(cmdId);
                Assert.False(KeybindingsManager.HasCustomShortcut(cmdId));
                Assert.Equal("Ctrl+K", KeybindingsManager.GetShortcut(cmdId, "Ctrl+K"));

                KeybindingsManager.SetShortcut(cmdId, "Ctrl+Alt+K");
                Assert.True(KeybindingsManager.HasCustomShortcut(cmdId));
                Assert.Equal("Ctrl+Alt+K", KeybindingsManager.GetShortcut(cmdId, "Ctrl+K"));
                Assert.Equal(1, eventCount);

                KeybindingsManager.ResetShortcut(cmdId);
                Assert.False(KeybindingsManager.HasCustomShortcut(cmdId));
                Assert.Equal("Ctrl+K", KeybindingsManager.GetShortcut(cmdId, "Ctrl+K"));
                Assert.Equal(2, eventCount);
            }
            finally
            {
                KeybindingsManager.KeybindingsChanged -= handler;
                KeybindingsManager.ResetShortcut(cmdId);
            }
        }
    }
}
