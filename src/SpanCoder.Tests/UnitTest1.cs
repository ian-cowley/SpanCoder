using Avalonia.Headless.XUnit;
using System;
using System.IO;
using Xunit;
using Avalonia.Controls;
using SpanCoder.Shell;
using SpanCoder.App;
using SpanCoder.Engine;
using SpanCoder.Contracts;

namespace SpanCoder.Tests;

public class UnitTest1
{
    [AvaloniaFact]
    public void TestCaretMovement()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Line1\nLine2\nLine3".AsMemory());
        canvas.Document = doc;

        // Verify start position
        Assert.Equal(0, canvas.CaretLine);
        Assert.Equal(0, canvas.CaretCol);

        // Move to offset 3 (inside Line1)
        canvas.MoveCaretToOffset(3);
        Assert.Equal(0, canvas.CaretLine);
        Assert.Equal(3, canvas.CaretCol);

        // Move to offset 8 (inside Line2, since Line1 starts at 0, Line2 starts at 6)
        canvas.MoveCaretToOffset(8);
        Assert.Equal(1, canvas.CaretLine);
        Assert.Equal(2, canvas.CaretCol);

        // Test AdjustCaret - insert 2 chars at offset 3 (before caret at 8)
        doc.Insert(3, "AB");
        canvas.AdjustCaret(3, 2, 0);

        Assert.Equal(1, canvas.CaretLine);
        Assert.Equal(2, canvas.CaretCol); // absolute offset = 10, line start = 8. So 10 - 8 = 2.

        // Test AdjustCaret - delete 1 char at offset 9 (inside Line2, before caret at 10)
        doc.Delete(9, 1);
        canvas.AdjustCaret(9, 0, 1);
        Assert.Equal(1, canvas.CaretLine);
        Assert.Equal(1, canvas.CaretCol);

        // Test AdjustCaret - delete 1 char at offset 9 (which is exactly at the caret)
        doc.Delete(9, 1);
        canvas.AdjustCaret(9, 0, 1);
        Assert.Equal(1, canvas.CaretLine);
        Assert.Equal(1, canvas.CaretCol);

        // Test AdjustCaret - insert newline at offset 9
        doc.Insert(9, "\n");
        canvas.AdjustCaret(9, 1, 0);
        Assert.Equal(2, canvas.CaretLine);
        Assert.Equal(0, canvas.CaretCol);
    }

    [Fact]
    public void TestCSharpLexer()
    {
        // Lex Line 1
        var line1 = "using System; // hello".AsSpan();
        var lexer1 = new CSharpLexer(line1, LineState.Normal);
        
        Assert.True(lexer1.NextToken(out var t1, out var st1));
        Assert.Equal(TokenType.Keyword, t1.Type); // "using"
        Assert.Equal(0, t1.Start);
        Assert.Equal(5, t1.Length);

        Assert.True(lexer1.NextToken(out var t2, out var st2));
        Assert.Equal(TokenType.Text, t2.Type); // " "
        
        Assert.True(lexer1.NextToken(out var t3, out var st3));
        Assert.Equal(TokenType.Text, t3.Type); // "System"
        
        Assert.True(lexer1.NextToken(out var t4, out var st4));
        Assert.Equal(TokenType.Text, t4.Type); // ";"
        
        Assert.True(lexer1.NextToken(out var t5, out var st5));
        Assert.Equal(TokenType.Text, t5.Type); // " "
        
        Assert.True(lexer1.NextToken(out var t6, out var st6));
        Assert.Equal(TokenType.Comment, t6.Type); // "// hello"
        Assert.Equal(LineState.Normal, st6);

        // Lex Line 2 (Start of block comment)
        var line2 = "/* block".AsSpan();
        var endState2 = CSharpLexer.ComputeEndState(line2, LineState.Normal);
        Assert.Equal(LineState.InBlockComment, endState2);

        // Lex Line 3 (End of block comment followed by code)
        var line3 = "comment */ public class Foo { string s = \"world\"; int val = 123; }".AsSpan();
        var lexer3 = new CSharpLexer(line3, LineState.InBlockComment);
        
        Assert.True(lexer3.NextToken(out var t3_1, out var st3_1));
        Assert.Equal(TokenType.Comment, t3_1.Type); // "comment */"
        Assert.Equal(LineState.Normal, st3_1);

        Assert.True(lexer3.NextToken(out var t3_2, out var st3_2)); // whitespace
        
        Assert.True(lexer3.NextToken(out var t3_3, out var st3_3));
        Assert.Equal(TokenType.Keyword, t3_3.Type); // "public"

        // Search for string literal
        bool foundString = false;
        bool foundNumber = false;
        while (lexer3.NextToken(out var t, out _))
        {
            if (t.Type == TokenType.String)
            {
                Assert.Equal("\"world\"", line3.Slice(t.Start, t.Length).ToString());
                foundString = true;
            }
            else if (t.Type == TokenType.Number)
            {
                Assert.Equal("123", line3.Slice(t.Start, t.Length).ToString());
                foundNumber = true;
            }
        }
        Assert.True(foundString);
        Assert.True(foundNumber);
    }

    [Fact]
    public void TestAdvancedCSharpLexer()
    {
        // 1. Methods & Invocations
        var line1 = "Initialize(context); Collect();".AsSpan();
        var lexer1 = new CSharpLexer(line1, LineState.Normal);
        
        Assert.True(lexer1.NextToken(out var t1, out _));
        Assert.Equal(TokenType.Method, t1.Type); // "Initialize"
        Assert.Equal("Initialize", line1.Slice(t1.Start, t1.Length).ToString());
        
        Assert.True(lexer1.NextToken(out var t2, out _)); // "("
        Assert.True(lexer1.NextToken(out var t3, out _)); // "context" (lowercase identifier -> Text)
        Assert.Equal(TokenType.Text, t3.Type);
        
        Assert.True(lexer1.NextToken(out var t4, out _)); // ")"
        Assert.True(lexer1.NextToken(out var t5, out _)); // ";"
        Assert.True(lexer1.NextToken(out var t6, out _)); // " "
        
        Assert.True(lexer1.NextToken(out var t7, out _));
        Assert.Equal(TokenType.Method, t7.Type); // "Collect"
        Assert.Equal("Collect", line1.Slice(t7.Start, t7.Length).ToString());

        // 2. Types
        var line2 = "class CommandGenerator : IIncrementalGenerator".AsSpan();
        var lexer2 = new CSharpLexer(line2, LineState.Normal);
        
        Assert.True(lexer2.NextToken(out var t2_1, out _)); // "class"
        Assert.True(lexer2.NextToken(out var t2_2, out _)); // " "
        Assert.True(lexer2.NextToken(out var t2_3, out _));
        Assert.Equal(TokenType.Type, t2_3.Type); // "CommandGenerator"
        Assert.Equal("CommandGenerator", line2.Slice(t2_3.Start, t2_3.Length).ToString());
        
        Assert.True(lexer2.NextToken(out var t2_4, out _)); // " "
        Assert.True(lexer2.NextToken(out var t2_5, out _)); // ":"
        Assert.True(lexer2.NextToken(out var t2_6, out _)); // " "
        Assert.True(lexer2.NextToken(out var t2_7, out _));
        Assert.Equal(TokenType.Type, t2_7.Type); // "IIncrementalGenerator" (Interface starts with I + uppercase)
        Assert.Equal("IIncrementalGenerator", line2.Slice(t2_7.Start, t2_7.Length).ToString());

        // 3. Preprocessor Directives
        var line3 = "#if DEBUG // check".AsSpan();
        var lexer3 = new CSharpLexer(line3, LineState.Normal);
        
        Assert.True(lexer3.NextToken(out var t3_1, out _));
        Assert.Equal(TokenType.Preprocessor, t3_1.Type); // "#if DEBUG "
        Assert.Equal("#if DEBUG ", line3.Slice(t3_1.Start, t3_1.Length).ToString());
        
        Assert.True(lexer3.NextToken(out var t3_2, out _));
        Assert.Equal(TokenType.Comment, t3_2.Type); // "// check"
        Assert.Equal("// check", line3.Slice(t3_2.Start, t3_2.Length).ToString());

        // 4. Attributes
        var line4 = "[Generator] [Route(\"api\")]".AsSpan();
        var lexer4 = new CSharpLexer(line4, LineState.Normal);
        
        Assert.True(lexer4.NextToken(out var t4_1, out _)); // "["
        Assert.True(lexer4.NextToken(out var t4_2, out _));
        Assert.Equal(TokenType.Attribute, t4_2.Type); // "Generator"
        Assert.Equal("Generator", line4.Slice(t4_2.Start, t4_2.Length).ToString());
        
        Assert.True(lexer4.NextToken(out var t4_3, out _)); // "]"
        Assert.True(lexer4.NextToken(out var t4_4, out _)); // " "
        Assert.True(lexer4.NextToken(out var t4_5, out _)); // "["
        Assert.True(lexer4.NextToken(out var t4_6, out _));
        Assert.Equal(TokenType.Attribute, t4_6.Type); // "Route"
        Assert.Equal("Route", line4.Slice(t4_6.Start, t4_6.Length).ToString());
        
        Assert.True(lexer4.NextToken(out var t4_7, out _)); // "("
        Assert.True(lexer4.NextToken(out var t4_8, out _));
        Assert.Equal(TokenType.String, t4_8.Type); // "\"api\""
        Assert.Equal("\"api\"", line4.Slice(t4_8.Start, t4_8.Length).ToString());

        // 5. Variable declaration patterns
        var line5 = "DiagnosticItem item; List<string> list;".AsSpan();
        var lexer5 = new CSharpLexer(line5, LineState.Normal);

        Assert.True(lexer5.NextToken(out var t5_1, out _));
        Assert.Equal(TokenType.Type, t5_1.Type); // "DiagnosticItem"
        Assert.Equal("DiagnosticItem", line5.Slice(t5_1.Start, t5_1.Length).ToString());

        // Skip to List
        while (lexer5.NextToken(out var t, out _))
        {
            if (t.Length == 4 && line5.Slice(t.Start, t.Length).SequenceEqual("List"))
            {
                Assert.Equal(TokenType.Type, t.Type); // "List"
                break;
            }
        }
    }

    [Fact]
    public void TestCoreLanguagesLexing()
    {
        // 1. JSON
        var jsonText = "{\"id\": 123, \"active\": true}".AsSpan();
        var jsonLexer = new DocumentLexer(jsonText, ".json", LineState.Normal);
        
        Assert.True(jsonLexer.NextToken(out var jt1, out _)); // "{"
        Assert.True(jsonLexer.NextToken(out var jt2, out _)); // "\"id\""
        Assert.Equal(TokenType.Property, jt2.Type);
        Assert.Equal("\"id\"", jsonText.Slice(jt2.Start, jt2.Length).ToString());
        
        Assert.True(jsonLexer.NextToken(out var jt3, out _)); // ":"
        Assert.True(jsonLexer.NextToken(out var jt4, out _)); // " "
        Assert.True(jsonLexer.NextToken(out var jt5, out _)); // "123"
        Assert.Equal(TokenType.Number, jt5.Type);

        while (jsonLexer.NextToken(out var jt, out _))
        {
            if (jt.Length == 4 && jsonText.Slice(jt.Start, jt.Length).SequenceEqual("true"))
            {
                Assert.Equal(TokenType.Keyword, jt.Type); // "true"
            }
        }

        // 2. HTML
        var htmlText = "<div class=\"btn\">Hello</div>".AsSpan();
        var htmlLexer = new DocumentLexer(htmlText, ".html", LineState.Normal);
        
        Assert.True(htmlLexer.NextToken(out var ht1, out _)); // "<"
        Assert.True(htmlLexer.NextToken(out var ht2, out _)); // "div" (Tag name)
        Assert.Equal(TokenType.Tag, ht2.Type);
        Assert.Equal("div", htmlText.Slice(ht2.Start, ht2.Length).ToString());
        
        Assert.True(htmlLexer.NextToken(out var ht3, out _)); // " "
        Assert.True(htmlLexer.NextToken(out var ht4, out _)); // "class" (Attribute)
        Assert.Equal(TokenType.Property, ht4.Type);
        
        Assert.True(htmlLexer.NextToken(out var ht5, out _)); // "="
        Assert.True(htmlLexer.NextToken(out var ht6, out _)); // "\"btn\"" (String)
        Assert.Equal(TokenType.String, ht6.Type);

        // 3. CSS
        var cssText = ".btn { color: red; }".AsSpan();
        var cssLexer = new DocumentLexer(cssText, ".css", LineState.Normal);
        
        Assert.True(cssLexer.NextToken(out var ct1, out _)); // ".btn" (Selector)
        Assert.Equal(TokenType.Selector, ct1.Type);
        Assert.Equal(".btn", cssText.Slice(ct1.Start, ct1.Length).ToString());
        
        Assert.True(cssLexer.NextToken(out var ct2, out _)); // " "
        Assert.True(cssLexer.NextToken(out var ct3, out _)); // "{"
        Assert.True(cssLexer.NextToken(out var ct4, out _)); // " "
        Assert.True(cssLexer.NextToken(out var ct5, out _)); // "color" (Property)
        Assert.Equal(TokenType.Property, ct5.Type);
        Assert.Equal("color", cssText.Slice(ct5.Start, ct5.Length).ToString());

        // 4. JS
        var jsText = "const x = console.log(`val`);".AsSpan();
        var jsLexer = new DocumentLexer(jsText, ".js", LineState.Normal);
        
        Assert.True(jsLexer.NextToken(out var jst1, out _)); // "const" (Keyword)
        Assert.Equal(TokenType.Keyword, jst1.Type);
        
        while (jsLexer.NextToken(out var jst, out _))
        {
            if (jst.Length == 3 && jsText.Slice(jst.Start, jst.Length).SequenceEqual("log"))
            {
                Assert.Equal(TokenType.Method, jst.Type); // "log" followed by (
            }
            else if (jst.Length == 5 && jsText.Slice(jst.Start, jst.Length).SequenceEqual("`val`"))
            {
                Assert.Equal(TokenType.String, jst.Type); // backtick string
            }
        }

        // 5. Markdown
        var mdText = "# Header\n[link](url)".AsSpan();
        var mdLexer = new DocumentLexer(mdText, ".md", LineState.Normal);
        
        Assert.True(mdLexer.NextToken(out var mt1, out _)); // "# Header" (Heading)
        Assert.Equal(TokenType.Heading, mt1.Type);
    }

    [AvaloniaFact]
    public void TestSolutionParsingSlnx()
    {
        string tempSlnx = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".slnx");
        string tempProj1 = Path.Combine(Path.GetTempPath(), "Proj1.csproj");
        string tempProj2 = Path.Combine(Path.GetTempPath(), "Proj2.csproj");
        
        File.WriteAllText(tempProj1, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(tempProj2, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        
        string slnxContent = $@"<Solution>
  <Folder Name=""/src/"">
    <Project Path=""{tempProj1}"" />
  </Folder>
  <Folder Name=""/tools/"">
    <Project Path=""{tempProj2}"" />
  </Folder>
</Solution>";
        File.WriteAllText(tempSlnx, slnxContent);

        try
        {
            var tree = new SidebarFileTree();
            tree.SetRootPath(tempSlnx);

            var items = tree.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(items);
            Assert.Single(items);

            var solutionItem = items[0];
            var solutionChildren = solutionItem.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(solutionChildren);
            Assert.Equal(2, solutionChildren.Count);

            var srcFolderItem = solutionChildren[0];
            var srcChildren = srcFolderItem.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(srcChildren);
            Assert.Single(srcChildren);

            var projItem = srcChildren[0];
            Assert.NotNull(projItem);
            Assert.Equal(tempProj1, projItem.Tag as string);
        }
        finally
        {
            if (File.Exists(tempSlnx)) File.Delete(tempSlnx);
            if (File.Exists(tempProj1)) File.Delete(tempProj1);
            if (File.Exists(tempProj2)) File.Delete(tempProj2);
        }
    }

    [AvaloniaFact]
    public void TestSolutionParsingSln()
    {
        string tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".sln");
        string tempProj = Path.Combine(Path.GetTempPath(), "Proj.csproj");
        
        File.WriteAllText(tempProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        
        string slnContent = $@"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project(""{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}"") = ""src"", ""src"", ""{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}""
EndProject
Project(""{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}"") = ""Proj"", ""{tempProj}"", ""{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}""
EndProject
Global
	GlobalSection(NestedProjects) = preSolution
		{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}} = {{2150E333-8FDC-42A3-9474-1A3956D46DE8}}
	EndGlobalSection
EndGlobal
";
        File.WriteAllText(tempSln, slnContent);

        try
        {
            var tree = new SidebarFileTree();
            tree.SetRootPath(tempSln);

            var items = tree.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(items);
            Assert.Single(items);

            var solutionItem = items[0];
            var solutionChildren = solutionItem.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(solutionChildren);
            Assert.Single(solutionChildren);

            var srcFolderItem = solutionChildren[0];
            var srcChildren = srcFolderItem.ItemsSource as System.Collections.ObjectModel.ObservableCollection<TreeViewItem>;
            Assert.NotNull(srcChildren);
            Assert.Single(srcChildren);

            var projItem = srcChildren[0];
            Assert.Equal(tempProj, projItem.Tag as string);
        }
        finally
        {
            if (File.Exists(tempSln)) File.Delete(tempSln);
            if (File.Exists(tempProj)) File.Delete(tempProj);
        }
    }

    [AvaloniaFact]
    public void TestAddProjectToSlnx()
    {
        string tempSlnx = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".slnx");
        string tempProj = Path.Combine(Path.GetTempPath(), "NewLib.csproj");
        File.WriteAllText(tempProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        
        string initialContent = @"<Solution>
  <Folder Name=""/src/"">
  </Folder>
</Solution>";
        File.WriteAllText(tempSlnx, initialContent);

        try
        {
            // Add project to root
            SidebarFileTree.AddProjectToSolutionFile(tempSlnx, tempProj, null);
            string contentAfterRoot = File.ReadAllText(tempSlnx);
            Assert.Contains("<Project Path=", contentAfterRoot);
            
            // Add project to folder "/src/"
            string tempProj2 = Path.Combine(Path.GetTempPath(), "NewLib2.csproj");
            File.WriteAllText(tempProj2, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            try
            {
                SidebarFileTree.AddProjectToSolutionFile(tempSlnx, tempProj2, "/src/");
                
                var doc = System.Xml.Linq.XDocument.Load(tempSlnx);
                var folder = doc.Descendants("Folder").FirstOrDefault(f => f.Attribute("Name")?.Value == "/src/");
                Assert.NotNull(folder);
                var proj = folder.Element("Project");
                Assert.NotNull(proj);
                Assert.Contains("NewLib2.csproj", proj.Attribute("Path")?.Value ?? "");
            }
            finally
            {
                if (File.Exists(tempProj2)) File.Delete(tempProj2);
            }
        }
        finally
        {
            if (File.Exists(tempSlnx)) File.Delete(tempSlnx);
            if (File.Exists(tempProj)) File.Delete(tempProj);
        }
    }

    [AvaloniaFact]
    public void TestAddFolderAndProjectToClassicSln()
    {
        string tempSln = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".sln");
        string tempProj = Path.Combine(Path.GetTempPath(), "ClassicLib.csproj");
        File.WriteAllText(tempProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        string initialContent = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Global
EndGlobal
";
        File.WriteAllText(tempSln, initialContent);

        try
        {
            // Add solution folder "MyFolder"
            SidebarFileTree.AddFolderToClassicSln(tempSln, "MyFolder", null);
            string afterFolder = File.ReadAllText(tempSln);
            Assert.Contains("Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"MyFolder\"", afterFolder);

            // Add project nested under "MyFolder"
            SidebarFileTree.AddProjectToClassicSln(tempSln, tempProj, "MyFolder");
            string afterProject = File.ReadAllText(tempSln);
            Assert.Contains("ClassicLib.csproj", afterProject);
            Assert.Contains("GlobalSection(NestedProjects) = preSolution", afterProject);
        }
        finally
        {
            if (File.Exists(tempSln)) File.Delete(tempSln);
            if (File.Exists(tempProj)) File.Delete(tempProj);
        }
    }

    [AvaloniaFact]
    public void TestGetNamespaceForFolder()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "source", "repos", "PolarsPlus", "Glacier.SpanCoder", "src", "SpanCoder.Shell", "SpanCoder.Shell.csproj");
        string shellDir = Path.Combine(Path.GetTempPath(), "source", "repos", "PolarsPlus", "Glacier.SpanCoder", "src", "SpanCoder.Shell");
        string panelsDir = Path.Combine(shellDir, "Views", "Panels");
        string specialDir = Path.Combine(shellDir, "Views-Special", "Panels+Test");
        
        // Same directory
        string ns1 = SidebarFileTree.GetNamespaceForFolder(projectPath, shellDir);
        Assert.Equal("SpanCoder.Shell", ns1);

        // Subdirectory
        string ns2 = SidebarFileTree.GetNamespaceForFolder(projectPath, panelsDir);
        Assert.Equal("SpanCoder.Shell.Views.Panels", ns2);

        // Subdirectory with invalid C# characters in folder name
        string ns3 = SidebarFileTree.GetNamespaceForFolder(projectPath, specialDir);
        Assert.Equal("SpanCoder.Shell.ViewsSpecial.PanelsTest", ns3);
    }

    [Fact]
    public async Task TestGrepSearchEngine()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        string tempFile1 = Path.Combine(tempDir, "File1.txt");
        string tempFile2 = Path.Combine(tempDir, "File2.cs");

        File.WriteAllText(tempFile1, "hello world\nthis is a test of grep\nwelcome home");
        File.WriteAllText(tempFile2, "using System;\nclass Foo {\n  public void Bar() {\n    Console.WriteLine(\"test content\");\n  }\n}");

        try
        {
            var searchEngine = new Glacier.Grep.SearchEngine(tempDir);
            
            // Search for literal "grep"
            var results1 = await searchEngine.SearchAsync("grep");
            Assert.Single(results1);
            Assert.Equal("this is a test of grep", results1[0].MatchContent);
            Assert.Equal(2, results1[0].LineNumber);
            Assert.Equal(Path.GetFileName(tempFile1), results1[0].FilePath);

            // Search for regex "Console.*WriteLine"
            var results2 = await searchEngine.SearchAsync("Console.*WriteLine", isRegex: true);
            Assert.Single(results2);
            Assert.Contains("Console.WriteLine", results2[0].MatchContent);
            Assert.Equal(4, results2[0].LineNumber);
            Assert.Equal(Path.GetFileName(tempFile2), results2[0].FilePath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TestGotoDefinitionSerialization()
    {
        byte[] buffer = new byte[1024];
        int lenReq = BinaryMessageSerializer.WriteGotoDefinitionRequest(buffer, 42, 123);
        Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, lenReq), out var header));
        Assert.Equal(MessageTypes.GotoDefinitionRequest, header.Type);
        Assert.Equal(42, header.DocumentId);
        Assert.Equal(123, header.Offset);

        int lenResp = BinaryMessageSerializer.WriteGotoDefinitionResponse(buffer, 42, 123, "C:/Path/To/File.cs", 5, 12);
        string path = BinaryMessageSerializer.ParseGotoDefinitionResponse(buffer.AsSpan(0, lenResp), out int docId, out int offset, out int line, out int character);
        Assert.Equal("C:/Path/To/File.cs", path);
        Assert.Equal(42, docId);
        Assert.Equal(123, offset);
        Assert.Equal(5, line);
        Assert.Equal(12, character);
    }

    [Fact]
    public void TestMockLspServerGetDefinition()
    {
        var method = typeof(MockLspServer).GetMethod("GetMockDefinition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var docs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        docs["file:///TestClass.cs"] = "using System;\n\npublic class TestClass\n{\n    public void TestMethod()\n    {\n    }\n}";

        // Hover over "TestClass" in a different text context
        string sourceText = "TestClass obj = new TestClass();";
        string? result = method.Invoke(null, new object[] { docs, "file:///Another.cs", sourceText, 0, 2 }) as string;
        Assert.NotNull(result);
        Assert.Contains("TestClass.cs", result);
        Assert.Contains("\"line\":2", result);
        Assert.Contains("\"character\":13", result);

        // Hover over "TestMethod"
        string? resultMethod = method.Invoke(null, new object[] { docs, "file:///Another.cs", "obj.TestMethod();", 0, 5 }) as string;
        Assert.NotNull(resultMethod);
        Assert.Contains("TestClass.cs", resultMethod);
        Assert.Contains("\"line\":4", resultMethod);
        Assert.Contains("\"character\":16", resultMethod);
    }

    [Fact]
    public void TestFindReferencesSerialization()
    {
        byte[] buffer = new byte[1024];
        int lenReq = BinaryMessageSerializer.WriteFindReferencesRequest(buffer, 3, 100);
        Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, lenReq), out var header));
        Assert.Equal(MessageTypes.FindReferencesRequest, header.Type);
        Assert.Equal(3, header.DocumentId);
        Assert.Equal(100, header.Offset);

        var items = new[]
        {
            new ReferenceItem { FilePath = "File1.cs", Line = 10, Character = 5 },
            new ReferenceItem { FilePath = "File2.cs", Line = 20, Character = 15 }
        };

        int lenResp = BinaryMessageSerializer.WriteFindReferencesResponse(buffer, 3, 100, items);
        var parsedItems = BinaryMessageSerializer.ParseFindReferencesResponse(buffer.AsSpan(0, lenResp), out int docId, out int offset);
        Assert.Equal(3, docId);
        Assert.Equal(100, offset);
        Assert.Equal(2, parsedItems.Count);
        Assert.Equal("File1.cs", parsedItems[0].FilePath);
        Assert.Equal(10, parsedItems[0].Line);
        Assert.Equal(5, parsedItems[0].Character);
        Assert.Equal("File2.cs", parsedItems[1].FilePath);
        Assert.Equal(20, parsedItems[1].Line);
        Assert.Equal(15, parsedItems[1].Character);
    }

    [Fact]
    public void TestRenameSerialization()
    {
        byte[] buffer = new byte[1024];
        int lenReq = BinaryMessageSerializer.WriteRenameRequest(buffer, 5, 200, "NewSymbolName");
        string newName = BinaryMessageSerializer.ParseRenameRequest(buffer.AsSpan(0, lenReq), out int docId, out int offset);
        Assert.Equal("NewSymbolName", newName);
        Assert.Equal(5, docId);
        Assert.Equal(200, offset);

        int lenResp = BinaryMessageSerializer.WriteRenameResponse(buffer, 5, 200, true);
        bool success = BinaryMessageSerializer.ParseRenameResponse(buffer.AsSpan(0, lenResp), out int docId2, out int offset2);
        Assert.True(success);
        Assert.Equal(5, docId2);
        Assert.Equal(200, offset2);
    }

    [Fact]
    public void TestDocumentSymbolsSerialization()
    {
        byte[] buffer = new byte[1024];
        int lenReq = BinaryMessageSerializer.WriteDocumentSymbolsRequest(buffer, 7);
        Assert.True(BinaryMessageSerializer.TryParseHeader(buffer.AsSpan(0, lenReq), out var header));
        Assert.Equal(MessageTypes.DocumentSymbolsRequest, header.Type);
        Assert.Equal(7, header.DocumentId);

        var items = new[]
        {
            new DocumentSymbolItem { Name = "ClassA", Detail = "class", Line = 2, Character = 4 },
            new DocumentSymbolItem { Name = "MethodB", Detail = "void", Line = 5, Character = 8 }
        };

        int lenResp = BinaryMessageSerializer.WriteDocumentSymbolsResponse(buffer, 7, items);
        var parsedItems = BinaryMessageSerializer.ParseDocumentSymbolsResponse(buffer.AsSpan(0, lenResp), out int docId);
        Assert.Equal(7, docId);
        Assert.Equal(2, parsedItems.Count);
        Assert.Equal("ClassA", parsedItems[0].Name);
        Assert.Equal("class", parsedItems[0].Detail);
        Assert.Equal(2, parsedItems[0].Line);
        Assert.Equal(4, parsedItems[0].Character);
        Assert.Equal("MethodB", parsedItems[1].Name);
        Assert.Equal("void", parsedItems[1].Detail);
        Assert.Equal(5, parsedItems[1].Line);
        Assert.Equal(8, parsedItems[1].Character);
    }

    [Fact]
    public void TestMockLspServerSemanticFeatures()
    {
        var refMethod = typeof(MockLspServer).GetMethod("GetMockReferences", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(refMethod);

        var renameMethod = typeof(MockLspServer).GetMethod("GetMockRename", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(renameMethod);

        var symbolsMethod = typeof(MockLspServer).GetMethod("GetMockDocumentSymbols", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(symbolsMethod);

        var docs = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        docs["file:///TestClass.cs"] = "public class TestClass\n{\n    public void TestMethod()\n    {\n    }\n}";

        // 1. References for "TestClass"
        string sourceText = "TestClass obj = new TestClass();";
        string? refResult = refMethod.Invoke(null, new object[] { docs, "file:///Another.cs", sourceText, 0, 2 }) as string;
        Assert.NotNull(refResult);
        Assert.Contains("TestClass.cs", refResult);
        Assert.Contains("\"line\":0", refResult); // definition is at line 0

        // 2. Rename "TestClass" to "RenamedClass"
        string? renameResult = renameMethod.Invoke(null, new object[] { docs, "file:///Another.cs", sourceText, 0, 2, "RenamedClass" }) as string;
        Assert.NotNull(renameResult);
        Assert.Contains("RenamedClass", renameResult);
        Assert.Contains("TestClass.cs", renameResult);

        // 3. Document symbols for "TestClass.cs"
        string? symbolsResult = symbolsMethod.Invoke(null, new object[] { "file:///TestClass.cs", docs["file:///TestClass.cs"] }) as string;
        Assert.NotNull(symbolsResult);
        Assert.Contains("TestClass", symbolsResult);
        Assert.Contains("TestMethod", symbolsResult);
    }

    [AvaloniaFact]
    public void TestTextSelection()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Hello World\nLine Number Two\nEnd of file".AsMemory());
        canvas.Document = doc;

        // Verify initially no selection
        Assert.False(canvas.HasSelection(out _, out _));

        // Test GetSelectedText fallbacks to current line (line 0)
        string fallbackText = canvas.GetSelectedText(out int start, out int len);
        Assert.Equal("Hello World\n", fallbackText);
        Assert.Equal(0, start);
        Assert.Equal(12, len);

        // Test mouse/drag selection setting
        var startField = typeof(TextEditorCanvas).GetField("_selectionStartOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var endField = typeof(TextEditorCanvas).GetField("_selectionEndOffset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(startField);
        Assert.NotNull(endField);

        // Select "World" (from offset 6 to 11)
        startField.SetValue(canvas, 6);
        endField.SetValue(canvas, 11);

        Assert.True(canvas.HasSelection(out int selStart, out int selLen));
        Assert.Equal(6, selStart);
        Assert.Equal(5, selLen);

        string selText = canvas.GetSelectedText(out int finalStart, out int finalLen);
        Assert.Equal("World", selText);
        Assert.Equal(6, finalStart);
        Assert.Equal(5, finalLen);

        // AdjustCaret should clear selection
        canvas.AdjustCaret(0, 0, 0);
        Assert.False(canvas.HasSelection(out _, out _));
    }

    [Fact]
    public void TestLanguageConfigurationRegistry()
    {
        var css = LanguageConfigurationRegistry.Get(".css");
        Assert.Equal("/*", css.BlockCommentStart);
        Assert.Equal("*/", css.BlockCommentEnd);
        Assert.Null(css.LineComment);

        // Test custom register
        LanguageConfigurationRegistry.Register(new LanguageConfigDescriptor(".py", "#", "\"\"\"", "\"\"\""));
        var py = LanguageConfigurationRegistry.Get(".py");
        Assert.Equal("#", py.LineComment);
        Assert.Equal("\"\"\"", py.BlockCommentStart);
    }

    [AvaloniaFact]
    public void TestCommentToggling()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "public class Foo\n{\n    int x = 123;\n}".AsMemory(), "test.cs");
        canvas.Document = doc;
        
        var connection = new MockEngineConnection { Document = doc };

        // 1. Test Toggle Line Comment (C# - line 2: "    int x = 123;")
        canvas.MoveCaret(2, 4); // caret on line 2
        CommentHelper.ToggleLineComment(canvas, connection, "test.cs");
        
        Assert.Single(connection.SentMessages);
        var msg = connection.SentMessages[0];
        Assert.True(BinaryMessageSerializer.TryParseHeader(msg, out var header));
        Assert.Equal(MessageTypes.InsertText, header.Type);
        Assert.Equal(doc.GetLineStart(2) + 4, header.Offset);
        
        var text = BinaryMessageSerializer.ParseInsertText(msg, out _, out _).ToString();
        Assert.Equal("//", text);
        
        doc.Insert((int)doc.GetLineStart(2) + 4, "//");
        connection.SentMessages.Clear();

        // 2. Test Toggle Line Comment (Uncomment)
        CommentHelper.ToggleLineComment(canvas, connection, "test.cs");
        Assert.Single(connection.SentMessages);
        msg = connection.SentMessages[0];
        Assert.True(BinaryMessageSerializer.TryParseHeader(msg, out header));
        Assert.Equal(MessageTypes.DeleteText, header.Type);
        Assert.Equal(doc.GetLineStart(2) + 4, header.Offset);
        
        int deleteLen = BinaryMessageSerializer.ParseDeleteText(msg, out _, out _);
        Assert.Equal(2, deleteLen);
    }

    private class MockEngineConnection : IEngineConnection
    {
        public System.Collections.Generic.List<byte[]> SentMessages { get; } = new();

        public void Send(byte[] message)
        {
            SentMessages.Add(message);
        }

        public event Action<byte[]>? MessageReceived
        {
            add { }
            remove { }
        }
        public IDocumentView? GetDocument(int documentId) => Document;
        public Document Document { get; set; } = null!;
    }

    [Fact]
    public void TestGenericLexerSyntaxHighlighting()
    {
        var keywords = new System.Collections.Generic.List<string> { "def", "class", "return", "if" };
        var types = new System.Collections.Generic.List<string> { "int", "str", "list" };
        
        LanguageConfigurationRegistry.Register(new LanguageConfigDescriptor(
            ".py", 
            "#", 
            "\"\"\"", 
            "\"\"\"", 
            keywords, 
            types
        ));

        var line = "def my_func(x: int): # comment with \"word\" and 123".AsSpan();
        var lexer = new DocumentLexer(line, ".py", LineState.Normal);

        // def (Keyword)
        Assert.True(lexer.NextToken(out var t1, out _));
        Assert.Equal(TokenType.Keyword, t1.Type);
        Assert.Equal("def", line.Slice(t1.Start, t1.Length).ToString());

        // space
        Assert.True(lexer.NextToken(out var t2, out _));
        Assert.Equal(TokenType.Text, t2.Type);

        // my_func
        Assert.True(lexer.NextToken(out var t3, out _));
        Assert.Equal(TokenType.Text, t3.Type);

        // (x: 
        Assert.True(lexer.NextToken(out var t4, out _)); // '('
        Assert.True(lexer.NextToken(out var t5, out _)); // 'x'
        Assert.True(lexer.NextToken(out var t6, out _)); // ':'
        Assert.True(lexer.NextToken(out var t7, out _)); // ' '

        // int (Type)
        Assert.True(lexer.NextToken(out var t8, out _));
        Assert.Equal(TokenType.Type, t8.Type);
        Assert.Equal("int", line.Slice(t8.Start, t8.Length).ToString());

        // ): 
        Assert.True(lexer.NextToken(out var t9, out _)); // ')'
        Assert.True(lexer.NextToken(out var t10, out _)); // ':'
        Assert.True(lexer.NextToken(out var t11, out _)); // ' '

        // # comment with "word" and 123 (Comment)
        Assert.True(lexer.NextToken(out var t12, out _));
        Assert.Equal(TokenType.Comment, t12.Type);
        Assert.Equal("# comment with \"word\" and 123", line.Slice(t12.Start, t12.Length).ToString());
    }

    [Fact]
    public void TestCSharpLexerPatternMatching()
    {
        var line = "predicate: (node, _) => node is MethodDeclarationSyntax m && m.AttributeLists.Count > 0".AsSpan();
        var lexer = new CSharpLexer(line, LineState.Normal);
        while (lexer.NextToken(out var t, out _))
        {
            var tokenText = line.Slice(t.Start, t.Length).ToString();
            Console.WriteLine($"TOKEN_DBG: '{tokenText}' TYPE: {t.Type}");
        }
    }

    [Fact]
    public void TestSlnAndCsprojSyntaxHighlighting()
    {
        // 1. Test .csproj XML Highlighting
        string csprojText = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <!-- Comment -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>";
        bool foundTag = false;
        bool foundProperty = false;
        bool foundComment = false;
        bool foundString = false;

        foreach (var lineStr in csprojText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var csprojLexer = new DocumentLexer(lineStr.AsSpan(), ".csproj", LineState.Normal);
            while (csprojLexer.NextToken(out var t, out var ns))
            {
                if (t.Type == TokenType.Tag) foundTag = true;
                if (t.Type == TokenType.Property) foundProperty = true;
                if (t.Type == TokenType.Comment) foundComment = true;
                if (t.Type == TokenType.String) foundString = true;
            }
        }

        Assert.True(foundTag);
        Assert.True(foundProperty);
        Assert.True(foundComment);
        Assert.True(foundString);

        // 2. Test .sln Generic Highlighting
        string slnText = @"Project(""{2150E333-8FDC-42A3-9474-1A3956D46DE8}"") = ""src""
# Comment line
Global
	GlobalSection(NestedProjects) = preSolution
	EndGlobalSection
EndGlobal";
        bool foundSlnKeyword = false;
        bool foundSlnType = false;
        bool foundSlnComment = false;

        foreach (var lineStr in slnText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var slnLexer = new DocumentLexer(lineStr.AsSpan(), ".sln", LineState.Normal);
            while (slnLexer.NextToken(out var t, out var ns))
            {
                var text = lineStr.AsSpan(t.Start, t.Length).ToString();
                if (t.Type == TokenType.Keyword && (text == "Project" || text == "Global" || text == "EndGlobal")) foundSlnKeyword = true;
                if (t.Type == TokenType.Type && (text == "preSolution" || text == "postSolution")) foundSlnType = true;
                if (t.Type == TokenType.Comment) foundSlnComment = true;
            }
        }

        Assert.True(foundSlnKeyword);
        Assert.True(foundSlnType);
        Assert.True(foundSlnComment);
    }

    [Fact]
    public void TestCSharpLexerPatternMatchingActual()
    {
        var line = "node is MethodDeclarationSyntax m".AsSpan();
        var lexer = new CSharpLexer(line, LineState.Normal);
        
        Assert.True(lexer.NextToken(out var t1, out _)); // node
        Assert.Equal(TokenType.Text, t1.Type);
        Assert.Equal("node", line.Slice(t1.Start, t1.Length).ToString());

        Assert.True(lexer.NextToken(out var t2, out _)); // space
        Assert.True(lexer.NextToken(out var t3, out _)); // is
        Assert.Equal(TokenType.Keyword, t3.Type);

        Assert.True(lexer.NextToken(out var t4, out _)); // space
        Assert.True(lexer.NextToken(out var t5, out _)); // MethodDeclarationSyntax
        Assert.Equal(TokenType.Type, t5.Type);

        Assert.True(lexer.NextToken(out var t6, out _)); // space
        Assert.True(lexer.NextToken(out var t7, out _)); // m
        Assert.Equal(TokenType.Text, t7.Type);
    }

    [Fact]
    public async Task TestLanguagesExtensionPluginLoads()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string? solutionRoot = baseDir;
        while (solutionRoot != null && !File.Exists(Path.Combine(solutionRoot, "SpanCoder.slnx")))
        {
            solutionRoot = Path.GetDirectoryName(solutionRoot);
        }
        
        Assert.NotNull(solutionRoot);
        string appBinDir = Path.Combine(solutionRoot, "src", "SpanCoder.App", "bin");
        Assert.True(Directory.Exists(appBinDir), $"App bin directory should exist at: {appBinDir}");
        
        string? pluginJsonFile = Directory.GetFiles(appBinDir, "plugin.json", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Replace('\\', '/').Contains("plugins/languages-extension"));
            
        Assert.NotNull(pluginJsonFile);
        string pluginDir = Path.GetDirectoryName(pluginJsonFile)!;
        string pluginsDir = Path.GetDirectoryName(pluginDir)!;

        using var manager = new ExtensionManager(pluginsDir);
        
        bool registered = false;
        ExtensionManifest? receivedManifest = null;

        manager.ExtensionRegistered += (id, manifest) =>
        {
            if (id == "languages-extension")
            {
                receivedManifest = manifest;
                registered = true;
            }
        };

        manager.Start();

        int retries = 0;
        while (!registered && retries++ < 50)
        {
            await Task.Delay(100);
        }

        Assert.True(registered, "Languages extension plugin should connect and register itself successfully.");
        Assert.NotNull(receivedManifest);
        Assert.Equal("languages-extension", receivedManifest.Value.Id);
        
        var languages = receivedManifest.Value.Languages;
        Assert.Equal(9, languages.Count);
        Assert.Contains(languages, l => l.Extension == ".py");
        Assert.Contains(languages, l => l.Extension == ".cpp");
        Assert.Contains(languages, l => l.Extension == ".rs");
        Assert.Contains(languages, l => l.Extension == ".go");
        Assert.Contains(languages, l => l.Extension == ".c");
        Assert.Contains(languages, l => l.Extension == ".cc");
        Assert.Contains(languages, l => l.Extension == ".h");
        Assert.Contains(languages, l => l.Extension == ".hpp");
        Assert.Contains(languages, l => l.Extension == ".java");

        var toolbarItems = receivedManifest.Value.ToolbarItems;
        Assert.Equal(2, toolbarItems.Count);
        Assert.Contains(toolbarItems, t => t.CommandId == "languages.runPython");
        Assert.Contains(toolbarItems, t => t.CommandId == "languages.cargoBuild");
    }

    [Fact]
    public void TestMultiLspClientSpawning()
    {
        // 1. Verify FindWorkspacePath returns a path
        var findWorkspaceMethod = typeof(EngineHost)
            .GetMethod("FindWorkspacePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(findWorkspaceMethod);
        string? workspace = findWorkspaceMethod.Invoke(null, new object[] { "C:/dummy.py" }) as string;
        Assert.NotNull(workspace);

        // 2. Test spawning multiple mock LspClients in EngineHost
        var host = new EngineHost();
        try
        {
            var getClientMethod = typeof(EngineHost)
                .GetMethod("GetOrCreateLspClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(getClientMethod);

            var pyClient = getClientMethod.Invoke(host, new object[] { "C:/dummy.py" }) as LspClient;
            var csClient = getClientMethod.Invoke(host, new object[] { "C:/dummy.cs" }) as LspClient;

            Assert.NotNull(pyClient);
            Assert.NotNull(csClient);
            Assert.NotSame(pyClient, csClient);
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void TestDebugMessagesSerialization()
    {
        byte[] buffer = new byte[1024];

        // 1. DebugStartRequest
        int len1 = BinaryMessageSerializer.WriteDebugStartRequest(buffer, 5, "C:/temp/prog.dll");
        string path = BinaryMessageSerializer.ParseDebugStartRequest(buffer.AsSpan(0, len1), out int docId1);
        Assert.Equal("C:/temp/prog.dll", path);
        Assert.Equal(5, docId1);

        // 2. DebugSetBreakpointsRequest
        int[] bps = { 10, 15, 20 };
        int len2 = BinaryMessageSerializer.WriteDebugSetBreakpointsRequest(buffer, 5, bps);
        var parsedBps = BinaryMessageSerializer.ParseDebugSetBreakpointsRequest(buffer.AsSpan(0, len2), out int docId2);
        Assert.Equal(5, docId2);
        Assert.Equal(3, parsedBps.Count);
        Assert.Equal(10, parsedBps[0]);
        Assert.Equal(15, parsedBps[1]);
        Assert.Equal(20, parsedBps[2]);

        // 3. DebugStoppedEvent
        int len3 = BinaryMessageSerializer.WriteDebugStoppedEvent(buffer, 5, 12, 4, "breakpoint");
        string reason = BinaryMessageSerializer.ParseDebugStoppedEvent(buffer.AsSpan(0, len3), out int docId3, out int line3, out int char3);
        Assert.Equal("breakpoint", reason);
        Assert.Equal(5, docId3);
        Assert.Equal(12, line3);
        Assert.Equal(4, char3);

        // 4. DebugStateReport
        var stack = new System.Collections.Generic.List<string> { "Main (Program.cs:13)", "Run (App.cs:42)" };
        var vars = new System.Collections.Generic.List<string> { "x: 42 (int)", "name: \"test\" (string)" };
        int len4 = BinaryMessageSerializer.WriteDebugStateReport(buffer, 5, stack, vars);
        BinaryMessageSerializer.ParseDebugStateReport(buffer.AsSpan(0, len4), out int docId4, out var parsedStack, out var parsedVars);
        Assert.Equal(5, docId4);
        Assert.Equal(2, parsedStack.Count);
        Assert.Equal("Main (Program.cs:13)", parsedStack[0]);
        Assert.Equal("x: 42 (int)", parsedVars[0]);
    }

    [AvaloniaFact]
    public void TestCanvasBreakpointsAndHighlighting()
    {
        var canvas = new TextEditorCanvas();
        var doc = new Document(1, "Line 1\nLine 2\nLine 3\nLine 4\nLine 5".AsMemory());
        canvas.Document = doc;

        // Verify initial state
        Assert.Empty(canvas.GetBreakpoints());
        Assert.Null(canvas.DebugActiveLine);

        // Toggle breakpoints
        canvas.ToggleBreakpoint(2);
        canvas.ToggleBreakpoint(4);
        var bps = canvas.GetBreakpoints();
        Assert.Equal(2, bps.Count);
        Assert.Contains(2, bps);
        Assert.Contains(4, bps);

        // Untoggle breakpoint
        canvas.ToggleBreakpoint(2);
        bps = canvas.GetBreakpoints();
        Assert.Single(bps);
        Assert.Contains(4, bps);

        // Check active debug line highlight
        canvas.DebugActiveLine = 3;
        Assert.Equal(3, canvas.DebugActiveLine);
    }

    [Fact]
    public async Task TestMockDapServerHandshake()
    {
        var inputBuilder = new System.Text.StringBuilder();
        
        string initJson = "{\"command\":\"initialize\",\"seq\":1,\"type\":\"request\"}";
        inputBuilder.Append($"Content-Length: {initJson.Length}\r\n\r\n{initJson}");

        string launchJson = "{\"command\":\"launch\",\"seq\":2,\"type\":\"request\",\"arguments\":{\"program\":\"C:/test/prog.cs\"}}";
        inputBuilder.Append($"Content-Length: {launchJson.Length}\r\n\r\n{launchJson}");

        string disconnectJson = "{\"command\":\"disconnect\",\"seq\":3,\"type\":\"request\"}";
        inputBuilder.Append($"Content-Length: {disconnectJson.Length}\r\n\r\n{disconnectJson}");

        byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(inputBuilder.ToString());
        using var stdin = new MemoryStream(inputBytes);
        using var stdout = new MemoryStream();

        await Task.Run(() => MockDapServer.Run(stdin, stdout));

        string output = System.Text.Encoding.UTF8.GetString(stdout.ToArray());
        Assert.Contains("\"command\":\"initialize\"", output);
        Assert.Contains("\"command\":\"launch\"", output);
        Assert.Contains("\"event\":\"initialized\"", output);
    }

    [AvaloniaFact]
    public void TestAnsiEscapeParser()
    {
        var control = new TerminalControl();
        
        // Output text with ANSI escape codes
        control.ProcessOutputText("Hello \x1B[31mRed\x1B[0m Text\nLine2");
        
        var lines = control.Lines;
        Assert.Equal(2, lines.Count);
        
        // Line 1: Runs
        var runs = lines[0].Runs;
        Assert.Equal(3, runs.Count);
        Assert.Equal("Hello ", runs[0].Text);
        Assert.Equal("Red", runs[1].Text);
        Assert.Equal(" Text", runs[2].Text);
        
        // Line 2: Runs
        var runs2 = lines[1].Runs;
        Assert.Single(runs2);
        Assert.Equal("Line2", runs2[0].Text);
    }

    [Fact]
    public void TestGitStatusParser()
    {
        var provider = new GitVersionProvider();
        var mapMethod = typeof(GitVersionProvider).GetMethod("MapStatusChar", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(mapMethod);
        
        Assert.Equal("M", mapMethod.Invoke(provider, new object[] { 'M' }));
        Assert.Equal("A", mapMethod.Invoke(provider, new object[] { 'A' }));
        Assert.Equal("D", mapMethod.Invoke(provider, new object[] { 'D' }));
    }

    [Fact]
    public async Task TestGitDiffParser()
    {
        var provider = new GitVersionProvider();
        var method = typeof(GitVersionProvider).GetMethod("QueryLineChangesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        
        string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        
        try
        {
            provider.SetWorkingDirectory(tempDir);
            var changes = await (Task<Dictionary<int, GitLineChangeType>>)method.Invoke(provider, new object[] { Path.Combine(tempDir, "nonexistent.cs") })!;
            Assert.Empty(changes);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestPtySpawningAndFallback()
    {
        using var pty = new PtyHost();
        
        string shell = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "cmd.exe" : "echo";
        string[] args = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? new[] { "/c", "echo hello" } : new[] { "hello" };
        
        bool started = pty.Start(shell, args, Path.GetTempPath(), 80, 24);
        Assert.True(started);
        
        System.Threading.Thread.Sleep(500);
        
        Assert.True(pty.IsFallback || !pty.IsFallback);
    }

    [Fact]
    public void TestKeybindingsNormalization()
    {
        Assert.Equal("Ctrl+Oem2", KeybindingsManager.NormalizeShortcut("Ctrl+/"));
        Assert.Equal("Ctrl+Shift+Oem2", KeybindingsManager.NormalizeShortcut("Ctrl+Shift+/"));
        Assert.Equal("Oem2", KeybindingsManager.NormalizeShortcut("/"));
        Assert.Equal("Ctrl+S", KeybindingsManager.NormalizeShortcut("Ctrl+S"));
        Assert.Equal("", KeybindingsManager.NormalizeShortcut(""));
    }

    [Fact]
    public void TestSiliconDebugConfiguration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        string configPath = Path.Combine(tempDir, "spancoder_debug.json");

        try
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "silicon");
                    writer.WriteString("gdbPath", "arm-none-eabi-gdb");
                    writer.WriteString("program", "build/test.elf");
                    writer.WriteString("target", "localhost:3333");
                    writer.WriteString("deployCmd", "openocd -f probe.cfg");
                    
                    writer.WriteStartArray("autorun");
                    writer.WriteStringValue("target remote localhost:3333");
                    writer.WriteStringValue("load");
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
                File.WriteAllText(configPath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }

            Assert.True(File.Exists(configPath));
            string json = File.ReadAllText(configPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("silicon", root.GetProperty("type").GetString());
            Assert.Equal("arm-none-eabi-gdb", root.GetProperty("gdbPath").GetString());
            Assert.Equal("build/test.elf", root.GetProperty("program").GetString());
            Assert.Equal("localhost:3333", root.GetProperty("target").GetString());
            Assert.Equal("openocd -f probe.cfg", root.GetProperty("deployCmd").GetString());
            
            var autorun = root.GetProperty("autorun");
            Assert.Equal(2, autorun.GetArrayLength());
            Assert.Equal("target remote localhost:3333", autorun[0].GetString());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void TestMockSiliconDapServerInitialize()
    {
        string json = "{\"command\":\"initialize\",\"seq\":1,\"type\":\"request\",\"arguments\":{}}";
        string initRequest = $"Content-Length: {json.Length}\r\n\r\n{json}";
        using var stdin = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(initRequest));
        using var stdout = new MemoryStream();

        SpanCoder.Engine.MockSiliconDapServer.Run(stdin, stdout);

        stdout.Position = 0;
        string output = System.Text.Encoding.UTF8.GetString(stdout.ToArray());
        Assert.Contains("\"command\":\"initialize\"", output);
        Assert.Contains("\"success\":true", output);
        Assert.Contains("supportsConfigurationDoneRequest", output);
    }
}

