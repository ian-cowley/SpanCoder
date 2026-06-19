using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SpanCoder.Shell
{
    public class SidebarFileTree : TreeView
    {
        protected override Type StyleKeyOverride => typeof(TreeView);

        private string? _rootPath;
        public string? RootPath => _rootPath;
        private readonly ObservableCollection<TreeViewItem> _rootItems = new();

        public event Action<string>? FileSelected;
        public event Action<string>? GitDiffSelected;

        private class SlnEntry
        {
            public string Guid { get; set; } = "";
            public string TypeGuid { get; set; } = "";
            public string Name { get; set; } = "";
            public string Path { get; set; } = "";
            public string ParentGuid { get; set; } = "";
            public TreeViewItem? Item { get; set; }
            public ObservableCollection<TreeViewItem>? Children { get; set; }
        }

        private readonly Window? _ownerWindow;

        public SidebarFileTree(Window? ownerWindow = null)
        {
            _ownerWindow = ownerWindow;
            this.Background = Avalonia.Media.Brushes.Transparent;
            this.Foreground = Avalonia.Media.Brushes.LightGray;
            this.ItemsSource = _rootItems;
            this.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

            this.AddHandler(PointerPressedEvent, new EventHandler<PointerPressedEventArgs>((s, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
                {
                    var visual = e.Source as Avalonia.Visual;
                    var item = visual?.FindAncestorOfType<TreeViewItem>();
                    if (item != null && item.Tag is string path)
                    {
                        bool isExpandableFolder = Directory.Exists(path) || path.StartsWith("SolutionFolder:");
                        if (isExpandableFolder)
                        {
                            var headerVisual = item.Header as Avalonia.Visual;
                            if (headerVisual != null && IsDescendantOf(visual, headerVisual))
                            {
                                item.IsExpanded = !item.IsExpanded;
                                e.Handled = true;
                            }
                        }
                        else
                        {
                            FileSelected?.Invoke(path);
                        }
                    }
                }
            }), RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private static bool IsDescendantOf(Avalonia.Visual? child, Avalonia.Visual parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = current.GetVisualParent();
            }
            return false;
        }

        public void SetRootPath(string path)
        {
            _rootPath = path;
            try
            {
                _rootItems.Clear();

                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                if (File.Exists(path) && path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    LoadSlnx(path);
                }
                else if (File.Exists(path) && path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    LoadClassicSln(path);
                }
                else if (Directory.Exists(path))
                {
                    var rootItem = CreateTreeItem(path, true);
                    _rootItems.Add(rootItem);
                    rootItem.IsExpanded = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting root path: {ex}");
                throw;
            }
        }

        private void LoadSlnx(string path)
        {
            string solutionDir = Path.GetDirectoryName(path) ?? "";
            string solutionName = Path.GetFileNameWithoutExtension(path);

            var solutionPanel = CreateNodeHeader($"Solution '{solutionName}'", "M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5", "#7F52FF", true);
            var solutionText = solutionPanel.Children.OfType<TextBlock>().First();

            var solutionRootItem = new TreeViewItem
            {
                Header = solutionPanel,
                Tag = path,
                IsExpanded = true,
                ContextMenu = CreateSolutionContextMenu(path, null)
            };
            _rootItems.Add(solutionRootItem);

            try
            {
                var doc = XDocument.Load(path);
                var solutionEl = doc.Element("Solution");
                if (solutionEl == null) return;

                var childrenItems = new ObservableCollection<TreeViewItem>();
                solutionRootItem.ItemsSource = childrenItems;

                var folderItems = new Dictionary<string, TreeViewItem>();
                ParseElements(solutionEl, childrenItems, solutionDir, folderItems);

                int projectCount = doc.Descendants("Project").Count();
                solutionText.Text = $"Solution '{solutionName}' ({projectCount} projects)";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading .slnx solution: {ex}");
                solutionText.Text = $"Solution '{solutionName}' (Error loading)";
            }
        }

        private void ParseElements(XElement parentEl, ObservableCollection<TreeViewItem> parentCollection, string solutionDir, Dictionary<string, TreeViewItem> folderItems)
        {
            foreach (var node in parentEl.Elements())
            {
                if (node.Name == "Folder")
                {
                    string folderName = node.Attribute("Name")?.Value ?? "";
                    string displayName = folderName.Trim('/');

                    var folderPanel = CreateNodeHeader(displayName, "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z", "#DCA842");

                    var folderItem = new TreeViewItem
                    {
                        Header = folderPanel,
                        Tag = "SolutionFolder:" + folderName,
                        IsExpanded = true,
                        ContextMenu = CreateSolutionContextMenu(_rootPath ?? "", folderName)
                    };
                    var folderChildren = new ObservableCollection<TreeViewItem>();
                    folderItem.ItemsSource = folderChildren;
                    parentCollection.Add(folderItem);

                    ParseElements(node, folderChildren, solutionDir, folderItems);
                }
                else if (node.Name == "Project")
                {
                    string projectPath = node.Attribute("Path")?.Value ?? "";
                    AddProjectToCollection(projectPath, parentCollection, solutionDir);
                }
            }
        }

        private void LoadClassicSln(string path)
        {
            string solutionDir = Path.GetDirectoryName(path) ?? "";
            string solutionName = Path.GetFileNameWithoutExtension(path);

            var solutionPanel = CreateNodeHeader($"Solution '{solutionName}'", "M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5", "#7F52FF", true);
            var solutionText = solutionPanel.Children.OfType<TextBlock>().First();

            var solutionRootItem = new TreeViewItem
            {
                Header = solutionPanel,
                Tag = path,
                IsExpanded = true,
                ContextMenu = CreateSolutionContextMenu(path, null)
            };
            _rootItems.Add(solutionRootItem);

            var childrenItems = new ObservableCollection<TreeViewItem>();
            solutionRootItem.ItemsSource = childrenItems;

            var entries = new Dictionary<string, SlnEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var lines = File.ReadAllLines(path);
                bool inNestedProjects = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Project(", StringComparison.Ordinal))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^Project\(""\{([A-Fa-f0-9\-]+)\}""\)\s*=\s*""([^""]+)""\s*,\s*""([^""]+)""\s*,\s*""\{([A-Fa-f0-9\-]+)\}""");
                        if (match.Success)
                        {
                            var entry = new SlnEntry
                            {
                                TypeGuid = match.Groups[1].Value,
                                Name = match.Groups[2].Value,
                                Path = match.Groups[3].Value.Replace('\\', '/'),
                                Guid = match.Groups[4].Value
                            };
                            entries[entry.Guid] = entry;
                        }
                    }
                    else if (trimmed.StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal))
                    {
                        inNestedProjects = true;
                    }
                    else if (inNestedProjects && trimmed.StartsWith("EndGlobalSection", StringComparison.Ordinal))
                    {
                        inNestedProjects = false;
                    }
                    else if (inNestedProjects)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\{([A-Fa-f0-9\-]+)\}\s*=\s*\{([A-Fa-f0-9\-]+)\}");
                        if (match.Success)
                        {
                            string childGuid = match.Groups[1].Value;
                            string parentGuid = match.Groups[2].Value;
                            if (entries.TryGetValue(childGuid, out var child))
                            {
                                child.ParentGuid = parentGuid;
                            }
                        }
                    }
                }

                const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";
                int projectCount = 0;

                foreach (var entry in entries.Values)
                {
                    if (entry.TypeGuid.Equals(SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        var folderPanel = CreateNodeHeader(entry.Name, "M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z", "#DCA842");

                        entry.Item = new TreeViewItem
                        {
                            Header = folderPanel,
                            Tag = "SolutionFolder:" + entry.Name,
                            IsExpanded = true,
                            ContextMenu = CreateSolutionContextMenu(path, entry.Name)
                        };
                        entry.Children = new ObservableCollection<TreeViewItem>();
                        entry.Item.ItemsSource = entry.Children;
                    }
                    else
                    {
                        var item = CreateProjectItem(entry.Path, solutionDir, entry.Name);
                        if (item != null)
                        {
                            entry.Item = item;
                            projectCount++;
                        }
                    }
                }

                foreach (var entry in entries.Values)
                {
                    if (entry.Item == null) continue;

                    if (!string.IsNullOrEmpty(entry.ParentGuid) && entries.TryGetValue(entry.ParentGuid, out var parent))
                    {
                        if (parent.Children != null)
                        {
                            parent.Children.Add(entry.Item);
                        }
                    }
                    else
                    {
                        childrenItems.Add(entry.Item);
                    }
                }

                solutionText.Text = $"Solution '{solutionName}' ({projectCount} projects)";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading classic solution: {ex}");
                solutionText.Text = $"Solution '{solutionName}' (Error loading)";
            }
        }

        private TreeViewItem? CreateProjectItem(string relativeProjectPath, string solutionDir, string projectName)
        {
            string projectPath = Path.GetFullPath(Path.Combine(solutionDir, relativeProjectPath.Replace('\\', '/')));
            if (!File.Exists(projectPath)) return null;

            string projectDir = Path.GetDirectoryName(projectPath) ?? "";

            var projectPanel = CreateNodeHeader(projectName, "M19 13H5v-2h14v2zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z", "#519ABA", true);

            var projectItem = new TreeViewItem
            {
                Header = projectPanel,
                Tag = projectPath,
                ContextMenu = CreateProjectContextMenu(projectPath)
            };

            var children = new ObservableCollection<TreeViewItem>();
            children.Add(new TreeViewItem 
            { 
                Header = new TextBlock 
                { 
                    Text = "Loading...", 
                    Foreground = Avalonia.Media.Brushes.Gray,
                    FontSize = 11
                } 
            });
            projectItem.ItemsSource = children;

            projectItem.PropertyChanged += (sender, args) =>
            {
                if (args.Property == TreeViewItem.IsExpandedProperty && args.NewValue is bool expanded && expanded)
                {
                    LoadFolder(projectItem, children);
                }
            };

            return projectItem;
        }

        private void AddProjectToCollection(string relativeProjectPath, ObservableCollection<TreeViewItem> collection, string solutionDir)
        {
            string projectName = Path.GetFileNameWithoutExtension(relativeProjectPath);
            var item = CreateProjectItem(relativeProjectPath, solutionDir, projectName);
            if (item != null)
            {
                collection.Add(item);
            }
        }

        private TreeViewItem CreateTreeItem(string path, bool isDir)
        {
            var iconPath = isDir 
                ? "M2,4 C2,2.9 2.9,2 4,2 L9,2 L11,5 L20,5 C21.1,5 22,5.9 22,7 L22,18 C22,19.1 21.1,20 20,20 L4,20 C2.9,20 2,19.1 2,18 L2,4 Z"
                : "M6,2 C4.9,2 4,2.9 4,4 L4,20 C4,21.1 4.9,22 6,22 L18,22 C19.1,22 20,21.1 20,20 L20,8 L14,2 L6,2 Z M13,3.5 L18.5,9 L13,9 L13,3.5 Z";

            var extension = Path.GetExtension(path).ToLower();
            var iconColor = "#90A4AE"; // Default file color: cool grey
            if (isDir)
            {
                iconColor = "#DCA842"; // Folder yellow
            }
            else
            {
                switch (extension)
                {
                    case ".cs":
                        iconColor = "#519ABA"; // C# Blue
                        break;
                    case ".json":
                        iconColor = "#CBCB41"; // JSON Yellow
                        break;
                    case ".md":
                        iconColor = "#4EB09E"; // Markdown Teal
                        break;
                    case ".xml":
                        iconColor = "#E37933"; // XML Orange
                        break;
                    case ".txt":
                    case ".log":
                        iconColor = "#73C990"; // Green
                        break;
                }
            }

            var panel = CreateNodeHeader(Path.GetFileName(path), iconPath, iconColor);

            var item = new TreeViewItem
            {
                Header = panel,
                Tag = path
            };

            if (isDir)
            {
                var children = new ObservableCollection<TreeViewItem>();
                // Add a dummy node to enable expansion arrow
                children.Add(new TreeViewItem 
                { 
                    Header = new TextBlock 
                    { 
                        Text = "Loading...", 
                        Foreground = Avalonia.Media.Brushes.Gray,
                        FontSize = 11
                    } 
                });
                item.ItemsSource = children;
                
                // Subscribe to IsExpanded property changes
                item.PropertyChanged += (sender, args) =>
                {
                    if (args.Property == TreeViewItem.IsExpandedProperty && args.NewValue is bool expanded && expanded)
                    {
                        LoadFolder(item, children);
                    }
                };
            }

            string? projectPath = FindContainingProjectPath(path);
            if (projectPath != null)
            {
                if (isDir)
                {
                    item.ContextMenu = CreateProjectFolderContextMenu(projectPath, path);
                }
                else
                {
                    item.ContextMenu = CreateProjectFileContextMenu(path);
                }
            }

            return item;
        }

        private void LoadFolder(TreeViewItem item, ObservableCollection<TreeViewItem> children)
        {
            if (item.Tag is string path)
            {
                try
                {
                    bool isDummy = children.Count == 1 && 
                                   (children[0].Header?.ToString() == "Loading..." || 
                                    (children[0].Header is TextBlock tb && tb.Text == "Loading..."));

                    if (isDummy)
                    {
                        children.Clear();
                        string folderPath = path;
                        if (File.Exists(path) && (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || 
                                                 path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) || 
                                                 path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) || 
                                                 path.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)))
                        {
                            folderPath = Path.GetDirectoryName(path) ?? path;
                        }
                        // Get Directories
                        var dirs = Directory.GetDirectories(folderPath);
                        foreach (var dir in dirs)
                        {
                            string name = Path.GetFileName(dir);
                            // Skip common project noise
                            if (name == ".git" || name == "bin" || name == "obj" || name == ".vs" || name == ".idea" || name == "node_modules")
                                continue;

                            children.Add(CreateTreeItem(dir, true));
                        }

                        // Get Files
                        var files = Directory.GetFiles(folderPath);
                        foreach (var file in files)
                        {
                            children.Add(CreateTreeItem(file, false));
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error loading folder: {ex}");
                    children.Add(new TreeViewItem 
                    { 
                        Header = new TextBlock 
                        { 
                            Text = $"Error: {ex.Message}", 
                            Foreground = Avalonia.Media.Brushes.Red 
                        } 
                    });
                }
            }
        }

        private static Avalonia.Media.Geometry? SafeParseGeometry(string data)
        {
            try
            {
                return Avalonia.Media.StreamGeometry.Parse(data);
            }
            catch
            {
                return null;
            }
        }

        private ContextMenu CreateSolutionContextMenu(string solutionPath, string? targetFolder = null)
        {
            var menu = new ContextMenu();

            var buildItem = new MenuItem { Header = "Build" };
            buildItem.Click += async (s, e) => await HandleBuildSolution(solutionPath, "");
            menu.Items.Add(buildItem);

            var rebuildItem = new MenuItem { Header = "Rebuild" };
            rebuildItem.Click += async (s, e) => await HandleBuildSolution(solutionPath, "--no-incremental");
            menu.Items.Add(rebuildItem);

            var cleanItem = new MenuItem { Header = "Clean" };
            cleanItem.Click += async (s, e) => await HandleCleanSolution(solutionPath);
            menu.Items.Add(cleanItem);

            menu.Items.Add(new Separator());

            var addMenu = new MenuItem { Header = "Add" };
            menu.Items.Add(addMenu);

            var newProjectItem = new MenuItem { Header = "New Project..." };
            var existingProjectItem = new MenuItem { Header = "Existing Project..." };
            var solutionFolderItem = new MenuItem { Header = "New Solution Folder..." };

            addMenu.Items.Add(newProjectItem);
            addMenu.Items.Add(existingProjectItem);
            addMenu.Items.Add(solutionFolderItem);

            newProjectItem.Click += async (s, e) => await HandleAddNewProject(solutionPath, targetFolder);
            existingProjectItem.Click += async (s, e) => await HandleAddExistingProject(solutionPath, targetFolder);
            solutionFolderItem.Click += async (s, e) => await HandleAddSolutionFolder(solutionPath, targetFolder);

            return menu;
        }

        private async Task HandleBuildSolution(string solutionPath, string extraArgs)
        {
            string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
            string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
            string args = $"build \"{solutionPath}\"";
            if (!string.IsNullOrEmpty(extraArgs))
            {
                args += " " + extraArgs;
            }
            string taskName = string.IsNullOrEmpty(extraArgs) ? "Build" : "Rebuild";
            await RunDotnetCommand(solutionDir, args, $"{taskName} {solutionName}");
        }

        private async Task HandleCleanSolution(string solutionPath)
        {
            string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
            string solutionName = Path.GetFileNameWithoutExtension(solutionPath);
            await RunDotnetCommand(solutionDir, $"clean \"{solutionPath}\"", $"Clean {solutionName}");
        }

        private async Task HandleAddSolutionFolder(string solutionPath, string? targetFolder)
        {
            if (_ownerWindow == null) return;
            var folderName = await InputDialog.PromptAsync(_ownerWindow, "Add Solution Folder", "Enter solution folder name:");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            try
            {
                if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    var doc = XDocument.Load(solutionPath);
                    var solutionEl = doc.Element("Solution");
                    if (solutionEl != null)
                    {
                        string entryName = string.IsNullOrEmpty(targetFolder) ? $"/{folderName}/" : folderName;
                        if (string.IsNullOrEmpty(targetFolder))
                        {
                            solutionEl.Add(new XElement("Folder", new XAttribute("Name", entryName)));
                        }
                        else
                        {
                            var parent = doc.Descendants("Folder").FirstOrDefault(f => (f.Attribute("Name")?.Value ?? "") == targetFolder);
                            if (parent != null)
                            {
                                parent.Add(new XElement("Folder", new XAttribute("Name", entryName)));
                            }
                            else
                            {
                                solutionEl.Add(new XElement("Folder", new XAttribute("Name", $"/{folderName}/")));
                            }
                        }
                        doc.Save(solutionPath);
                    }
                }
                else
                {
                    AddFolderToClassicSln(solutionPath, folderName, targetFolder);
                }

                SetRootPath(solutionPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding solution folder: {ex}");
            }
        }

        private async Task HandleAddExistingProject(string solutionPath, string? targetFolder)
        {
            if (_ownerWindow == null) return;
            try
            {
                var files = await _ownerWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Add Existing Project",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("MSBuild Projects") { Patterns = new[] { "*.csproj", "*.fsproj", "*.vbproj", "*.vcxproj" } }
                    }
                });

                if (files != null && files.Count > 0)
                {
                    var projectPath = files[0].Path.LocalPath;
                    AddProjectToSolutionFile(solutionPath, projectPath, targetFolder);
                    SetRootPath(solutionPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding existing project: {ex}");
            }
        }

        internal static void AddProjectToSolutionFile(string solutionPath, string projectPath, string? targetFolder)
        {
            if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
                string relativePath = Path.GetRelativePath(solutionDir, projectPath).Replace('\\', '/');

                var doc = XDocument.Load(solutionPath);
                var solutionEl = doc.Element("Solution");
                if (solutionEl != null)
                {
                    var prjEl = new XElement("Project", new XAttribute("Path", relativePath));
                    if (string.IsNullOrEmpty(targetFolder))
                    {
                        solutionEl.Add(prjEl);
                    }
                    else
                    {
                        var parent = doc.Descendants("Folder").FirstOrDefault(f => (f.Attribute("Name")?.Value ?? "") == targetFolder);
                        if (parent != null)
                        {
                            parent.Add(prjEl);
                        }
                        else
                        {
                            solutionEl.Add(prjEl);
                        }
                    }
                    doc.Save(solutionPath);
                }
            }
            else
            {
                AddProjectToClassicSln(solutionPath, projectPath, targetFolder);
            }
        }

        private async Task HandleAddNewProject(string solutionPath, string? targetFolder)
        {
            if (_ownerWindow == null) return;
            var result = await NewProjectDialog.PromptAsync(_ownerWindow);
            if (result == null) return;

            string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
            string projectDir = "";
            if (!string.IsNullOrEmpty(targetFolder))
            {
                string folderPart = targetFolder.Trim('/');
                projectDir = Path.Combine(solutionDir, folderPart, result.Value.Name);
            }
            else
            {
                projectDir = Path.Combine(solutionDir, result.Value.Name);
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"new {result.Value.Template} -n {result.Value.Name} -o \"{projectDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }

                string projectFile = Path.Combine(projectDir, result.Value.Name + ".csproj");
                if (File.Exists(projectFile))
                {
                    AddProjectToSolutionFile(solutionPath, projectFile, targetFolder);
                    SetRootPath(solutionPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error creating new project: {ex}");
            }
        }

        internal static void AddFolderToClassicSln(string solutionPath, string folderName, string? targetFolder)
        {
            var lines = File.ReadAllLines(solutionPath).ToList();
            var folderGuid = "{" + Guid.NewGuid().ToString().ToUpper() + "}";
            
            int insertIndex = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].Trim() == "EndProject")
                {
                    insertIndex = i + 1;
                    break;
                }
            }
            if (insertIndex == -1) insertIndex = 0;

            var newProjectBlock = new List<string>
            {
                $"Project(\"{{2150E333-8FDC-42A3-9474-1A3956D46DE8}}\") = \"{folderName}\", \"{folderName}\", \"{folderGuid}\"",
                "EndProject"
            };
            lines.InsertRange(insertIndex, newProjectBlock);

            if (!string.IsNullOrEmpty(targetFolder))
            {
                string parentGuid = "";
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Project(", StringComparison.Ordinal))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"^Project\(""\{2150E333-8FDC-42A3-9474-1A3956D46DE8\}""\)\s*=\s*""([^""]+)""\s*,\s*""[^""]+""\s*,\s*""(\{([A-Fa-f0-9\-]+)\})""");
                        if (match.Success && match.Groups[1].Value == targetFolder)
                        {
                            parentGuid = match.Groups[2].Value;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(parentGuid))
                {
                    int nestedIdx = -1;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Contains("GlobalSection(NestedProjects)"))
                        {
                            nestedIdx = i + 1;
                            break;
                        }
                    }

                    if (nestedIdx != -1)
                    {
                        lines.Insert(nestedIdx, $"\t\t{folderGuid} = {parentGuid}");
                    }
                    else
                    {
                        int globalIdx = -1;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (lines[i].Trim() == "Global")
                            {
                                globalIdx = i + 1;
                                break;
                            }
                        }
                        if (globalIdx != -1)
                        {
                            var nestedSection = new List<string>
                            {
                                "\tGlobalSection(NestedProjects) = preSolution",
                                $"\t\t{folderGuid} = {parentGuid}",
                                "\tEndGlobalSection"
                            };
                            lines.InsertRange(globalIdx, nestedSection);
                        }
                    }
                }
            }

            File.WriteAllLines(solutionPath, lines);
        }

        internal static void AddProjectToClassicSln(string solutionPath, string projectPath, string? targetFolder)
        {
            string solutionDir = Path.GetDirectoryName(solutionPath) ?? "";
            string relativePath = Path.GetRelativePath(solutionDir, projectPath).Replace('/', '\\');
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            var projectGuid = "{" + Guid.NewGuid().ToString().ToUpper() + "}";
            
            var lines = File.ReadAllLines(solutionPath).ToList();

            int insertIndex = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].Trim() == "EndProject")
                {
                    insertIndex = i + 1;
                    break;
                }
            }
            if (insertIndex == -1) insertIndex = 0;

            var newProjectBlock = new List<string>
            {
                $"Project(\"{{9A19103F-16F7-4668-BE54-9A1E7A4F7556}}\") = \"{projectName}\", \"{relativePath}\", \"{projectGuid}\"",
                "EndProject"
            };
            lines.InsertRange(insertIndex, newProjectBlock);

            if (!string.IsNullOrEmpty(targetFolder))
            {
                string parentGuid = "";
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Project(", StringComparison.Ordinal))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(line, @"^Project\(""\{2150E333-8FDC-42A3-9474-1A3956D46DE8\}""\)\s*=\s*""([^""]+)""\s*,\s*""[^""]+""\s*,\s*""(\{([A-Fa-f0-9\-]+)\})""");
                        if (match.Success && match.Groups[1].Value == targetFolder)
                        {
                            parentGuid = match.Groups[2].Value;
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(parentGuid))
                {
                    int nestedIdx = -1;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (lines[i].Contains("GlobalSection(NestedProjects)"))
                        {
                            nestedIdx = i + 1;
                            break;
                        }
                    }

                    if (nestedIdx != -1)
                    {
                        lines.Insert(nestedIdx, $"\t\t{projectGuid} = {parentGuid}");
                    }
                    else
                    {
                        int globalIdx = -1;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            if (lines[i].Trim() == "Global")
                            {
                                globalIdx = i + 1;
                                break;
                            }
                        }
                        if (globalIdx != -1)
                        {
                            var nestedSection = new List<string>
                            {
                                "\tGlobalSection(NestedProjects) = preSolution",
                                $"\t\t{projectGuid} = {parentGuid}",
                                "\tEndGlobalSection"
                            };
                            lines.InsertRange(globalIdx, nestedSection);
                        }
                    }
                }
            }

            File.WriteAllLines(solutionPath, lines);
        }

        private void UpdateStatus(string message)
        {
            if (_ownerWindow is ShellWindow shell)
            {
                shell.UpdateStatus(message);
            }
        }

        private ContextMenu CreateProjectContextMenu(string projectPath)
        {
            var menu = new ContextMenu();

            var buildItem = new MenuItem { Header = "Build" };
            buildItem.Click += async (s, e) => await HandleBuildProject(projectPath, "");
            menu.Items.Add(buildItem);

            var rebuildItem = new MenuItem { Header = "Rebuild" };
            rebuildItem.Click += async (s, e) => await HandleBuildProject(projectPath, "--no-incremental");
            menu.Items.Add(rebuildItem);

            var cleanItem = new MenuItem { Header = "Clean" };
            cleanItem.Click += async (s, e) => await HandleCleanProject(projectPath);
            menu.Items.Add(cleanItem);

            menu.Items.Add(new Separator());

            var addMenu = new MenuItem { Header = "Add" };
            menu.Items.Add(addMenu);

            var newItem = new MenuItem { Header = "New Item..." };
            newItem.Click += async (s, e) => await HandleProjectAddNewItem(Path.GetDirectoryName(projectPath) ?? "");
            addMenu.Items.Add(newItem);

            var existingItem = new MenuItem { Header = "Existing Item..." };
            existingItem.Click += async (s, e) => await HandleProjectAddExistingItem(Path.GetDirectoryName(projectPath) ?? "");
            addMenu.Items.Add(existingItem);

            var newFolderItem = new MenuItem { Header = "New Folder..." };
            newFolderItem.Click += async (s, e) => await HandleProjectAddNewFolder(Path.GetDirectoryName(projectPath) ?? "");
            addMenu.Items.Add(newFolderItem);

            var classItem = new MenuItem { Header = "Class..." };
            classItem.Click += async (s, e) => await HandleProjectAddClass(projectPath, Path.GetDirectoryName(projectPath) ?? "");
            addMenu.Items.Add(classItem);

            menu.Items.Add(new Separator());

            var nugetItem = new MenuItem { Header = "Manage NuGet Packages..." };
            nugetItem.Click += async (s, e) => await HandleManageNuGet(projectPath);
            menu.Items.Add(nugetItem);

            return menu;
        }

        private ContextMenu CreateProjectFolderContextMenu(string projectPath, string folderPath)
        {
            var menu = new ContextMenu();

            var addMenu = new MenuItem { Header = "Add" };
            menu.Items.Add(addMenu);

            var newItem = new MenuItem { Header = "New Item..." };
            newItem.Click += async (s, e) => await HandleProjectAddNewItem(folderPath);
            addMenu.Items.Add(newItem);

            var existingItem = new MenuItem { Header = "Existing Item..." };
            existingItem.Click += async (s, e) => await HandleProjectAddExistingItem(folderPath);
            addMenu.Items.Add(existingItem);

            var newFolderItem = new MenuItem { Header = "New Folder..." };
            newFolderItem.Click += async (s, e) => await HandleProjectAddNewFolder(folderPath);
            addMenu.Items.Add(newFolderItem);

            var classItem = new MenuItem { Header = "Class..." };
            classItem.Click += async (s, e) => await HandleProjectAddClass(projectPath, folderPath);
            addMenu.Items.Add(classItem);

            menu.Items.Add(new Separator());

            var revealItem = new MenuItem { Header = "Reveal in Explorer" };
            revealItem.Click += (s, e) => HandleRevealInExplorer(folderPath);
            menu.Items.Add(revealItem);

            return menu;
        }

        private ContextMenu CreateProjectFileContextMenu(string filePath)
        {
            var menu = new ContextMenu();

            var openItem = new MenuItem { Header = "Open" };
            openItem.Click += (s, e) => FileSelected?.Invoke(filePath);
            menu.Items.Add(openItem);

            var diffItem = new MenuItem { Header = "Open Git Diff" };
            diffItem.Click += (s, e) => GitDiffSelected?.Invoke(filePath);
            menu.Items.Add(diffItem);

            var revealItem = new MenuItem { Header = "Reveal in Explorer" };
            revealItem.Click += (s, e) => HandleRevealInExplorer(filePath);
            menu.Items.Add(revealItem);

            menu.Items.Add(new Separator());

            var deleteItem = new MenuItem { Header = "Delete" };
            deleteItem.Click += async (s, e) => await HandleDeleteFile(filePath);
            menu.Items.Add(deleteItem);

            return menu;
        }

        private async Task HandleBuildProject(string projectPath, string extraArgs)
        {
            string projectDir = Path.GetDirectoryName(projectPath) ?? "";
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            string args = $"build \"{projectPath}\"";
            if (!string.IsNullOrEmpty(extraArgs))
            {
                args += " " + extraArgs;
            }
            string taskName = string.IsNullOrEmpty(extraArgs) ? "Build" : "Rebuild";
            await RunDotnetCommand(projectDir, args, $"{taskName} {projectName}");
        }

        private async Task HandleCleanProject(string projectPath)
        {
            string projectDir = Path.GetDirectoryName(projectPath) ?? "";
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            await RunDotnetCommand(projectDir, $"clean \"{projectPath}\"", $"Clean {projectName}");
        }

        private async Task HandleManageNuGet(string projectPath)
        {
            if (_ownerWindow == null) return;
            var managerWin = new NuGetManagerWindow(projectPath, _ownerWindow);
            await managerWin.ShowDialog(_ownerWindow);
        }

        private async Task RunDotnetCommand(string workingDir, string arguments, string taskName)
        {
            UpdateStatus($"{taskName} in progress...");

            var shellWin = _ownerWindow as ShellWindow;
            if (shellWin != null)
            {
                shellWin.FocusOutputTab("Build");
                shellWin.AppendToOutput("Build", $"\n--- Starting {taskName} ---\n");
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = new System.Diagnostics.Process { StartInfo = psi };
                var outputBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                        shellWin?.AppendToOutput("Build", e.Data + "\n");
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                        shellWin?.AppendToOutput("Build", e.Data + "\n");
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    UpdateStatus($"{taskName} succeeded.");
                    shellWin?.AppendToOutput("Build", $"\n[{taskName} Succeeded]\n");
                }
                else
                {
                    UpdateStatus($"{taskName} failed with exit code {process.ExitCode}.");
                    shellWin?.AppendToOutput("Build", $"\n[{taskName} Failed with exit code {process.ExitCode}]\n");
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"{taskName} error: {ex.Message}");
                shellWin?.AppendToOutput("Build", $"\n[Error executing {taskName}: {ex.Message}]\n");
            }
        }

        private async Task HandleProjectAddNewItem(string parentDir)
        {
            if (_ownerWindow == null) return;
            var fileName = await InputDialog.PromptAsync(_ownerWindow, "Add New Item", "Enter file name:");
            if (string.IsNullOrWhiteSpace(fileName)) return;

            try
            {
                string filePath = Path.Combine(parentDir, fileName);
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, "");
                }
                if (!string.IsNullOrEmpty(_rootPath))
                {
                    SetRootPath(_rootPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding new item: {ex}");
            }
        }

        private async Task HandleProjectAddExistingItem(string parentDir)
        {
            if (_ownerWindow == null) return;
            try
            {
                var files = await _ownerWindow.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Add Existing Item",
                    AllowMultiple = false
                });

                if (files != null && files.Count > 0)
                {
                    var sourcePath = files[0].Path.LocalPath;
                    string destPath = Path.Combine(parentDir, Path.GetFileName(sourcePath));
                    File.Copy(sourcePath, destPath, overwrite: true);
                    if (!string.IsNullOrEmpty(_rootPath))
                    {
                        SetRootPath(_rootPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding existing item: {ex}");
            }
        }

        private async Task HandleProjectAddNewFolder(string parentDir)
        {
            if (_ownerWindow == null) return;
            var folderName = await InputDialog.PromptAsync(_ownerWindow, "Add New Folder", "Enter folder name:");
            if (string.IsNullOrWhiteSpace(folderName)) return;

            try
            {
                string folderPath = Path.Combine(parentDir, folderName);
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                }
                if (!string.IsNullOrEmpty(_rootPath))
                {
                    SetRootPath(_rootPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding new folder: {ex}");
            }
        }

        private async Task HandleProjectAddClass(string projectPath, string parentDir)
        {
            if (_ownerWindow == null) return;
            var className = await InputDialog.PromptAsync(_ownerWindow, "Add Class", "Enter class name:");
            if (string.IsNullOrWhiteSpace(className)) return;

            if (className.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                className = className.Substring(0, className.Length - 3);
            }

            try
            {
                string filePath = Path.Combine(parentDir, className + ".cs");
                string ns = GetNamespaceForFolder(projectPath, parentDir);

                string content = $@"using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace {ns}
{{
    public class {className}
    {{
    }}
}}
";
                File.WriteAllText(filePath, content);
                if (!string.IsNullOrEmpty(_rootPath))
                {
                    SetRootPath(_rootPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error adding class: {ex}");
            }
        }

        private void HandleRevealInExplorer(string path)
        {
            try
            {
                string args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
                System.Diagnostics.Process.Start("explorer.exe", args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error revealing in explorer: {ex}");
            }
        }

        private async Task HandleDeleteFile(string filePath)
        {
            if (_ownerWindow == null) return;
            var confirm = await InputDialog.PromptAsync(_ownerWindow, "Confirm Delete", $"Type 'yes' to delete {Path.GetFileName(filePath)}:");
            if (confirm != null && confirm.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                    else if (Directory.Exists(filePath))
                    {
                        Directory.Delete(filePath, recursive: true);
                    }
                    if (!string.IsNullOrEmpty(_rootPath))
                    {
                        SetRootPath(_rootPath);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error deleting file: {ex}");
                }
            }
        }

        internal static string GetNamespaceForFolder(string projectPath, string parentDir)
        {
            string projectDir = Path.GetDirectoryName(projectPath) ?? "";
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            if (parentDir.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(projectDir, parentDir);
                if (relative == "." || string.IsNullOrEmpty(relative))
                {
                    return projectName;
                }
                var parts = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                return projectName + "." + string.Join(".", parts.Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"[^a-zA-Z0-9_]", "")));
            }
            return projectName;
        }

        private string? FindContainingProjectPath(string path)
        {
            try
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                while (dir != null)
                {
                    if (_rootPath != null)
                    {
                        string slnDir = Path.GetDirectoryName(_rootPath) ?? "";
                        if (dir.Length < slnDir.Length) break;
                    }

                    if (Directory.Exists(dir))
                    {
                        var projFiles = Directory.GetFiles(dir, "*.*proj");
                        var csproj = projFiles.FirstOrDefault(f => 
                            f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase));

                        if (csproj != null)
                        {
                            return csproj;
                        }
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                // Ignore filesystem access exceptions
            }
            return null;
        }

        private DockPanel CreateNodeHeader(string text, string iconPath, string iconColor, bool isBold = false)
        {
            var panel = new DockPanel
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2)
            };

            var icon = new Avalonia.Controls.Shapes.Path
            {
                Width = 14,
                Height = 14,
                Data = SafeParseGeometry(iconPath),
                Fill = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(iconColor)),
                Stretch = Avalonia.Media.Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            DockPanel.SetDock(icon, Dock.Left);
            panel.Children.Add(icon);

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = Avalonia.Media.Brushes.LightGray,
                FontSize = 12,
                FontWeight = isBold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
            };
            panel.Children.Add(textBlock);

            ToolTip.SetTip(panel, text);
            return panel;
        }
    }

    public class InputDialog : Window
    {
        private TextBox _textBox;
        private Button _okButton;
        private Button _cancelButton;
        
        public string ResultText { get; private set; } = "";

        public InputDialog(string title, string promptText, string defaultValue = "")
        {
            this.Title = title;
            this.Width = 350;
            this.Height = 150;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Background = Avalonia.Media.Brushes.Black;
            
            var panel = new StackPanel { Margin = new Thickness(15), Spacing = 10 };
            
            var prompt = new TextBlock { Text = promptText, Foreground = Avalonia.Media.Brushes.LightGray, FontSize = 12 };
            _textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 5) };
            
            var buttonsPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10 };
            _okButton = new Button { Content = "OK", Width = 60 };
            _cancelButton = new Button { Content = "Cancel", Width = 60 };
            
            _okButton.Click += (s, e) => { ResultText = _textBox.Text ?? ""; this.Close(true); };
            _cancelButton.Click += (s, e) => { this.Close(false); };
            
            buttonsPanel.Children.Add(_okButton);
            buttonsPanel.Children.Add(_cancelButton);
            
            panel.Children.Add(prompt);
            panel.Children.Add(_textBox);
            panel.Children.Add(buttonsPanel);
            
            this.Content = panel;
        }

        public static async Task<string?> PromptAsync(Window owner, string title, string promptText, string defaultValue = "")
        {
            var dialog = new InputDialog(title, promptText, defaultValue);
            var res = await dialog.ShowDialog<bool>(owner);
            return res ? dialog.ResultText : null;
        }
    }

    public class NewProjectDialog : Window
    {
        private TextBox _nameTextBox;
        private ComboBox _typeComboBox;
        private Button _okButton;
        private Button _cancelButton;
        
        public string ProjectName { get; private set; } = "";
        public string ProjectTemplate { get; private set; } = "console";

        public NewProjectDialog()
        {
            this.Title = "Add New Project";
            this.Width = 380;
            this.Height = 220;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Background = Avalonia.Media.Brushes.Black;
            
            var panel = new StackPanel { Margin = new Thickness(15), Spacing = 12 };
            
            var nameLabel = new TextBlock { Text = "Project Name:", Foreground = Avalonia.Media.Brushes.LightGray, FontSize = 12 };
            _nameTextBox = new TextBox { Watermark = "e.g., SpanCoder.Parser", Margin = new Thickness(0, 2) };
            
            var typeLabel = new TextBlock { Text = "Project Type:", Foreground = Avalonia.Media.Brushes.LightGray, FontSize = 12 };
            _typeComboBox = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            _typeComboBox.ItemsSource = new List<string> { "Console Application", "Class Library", "xUnit Test Project" };
            _typeComboBox.SelectedIndex = 0;
            
            var buttonsPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
            _okButton = new Button { Content = "OK", Width = 70 };
            _cancelButton = new Button { Content = "Cancel", Width = 70 };
            
            _okButton.Click += (s, e) =>
            {
                ProjectName = _nameTextBox.Text ?? "";
                if (string.IsNullOrWhiteSpace(ProjectName)) return;
                
                ProjectTemplate = _typeComboBox.SelectedIndex switch
                {
                    0 => "console",
                    1 => "classlib",
                    2 => "xunit",
                    _ => "console"
                };
                this.Close(true);
            };
            
            _cancelButton.Click += (s, e) => { this.Close(false); };
            
            buttonsPanel.Children.Add(_okButton);
            buttonsPanel.Children.Add(_cancelButton);
            
            panel.Children.Add(nameLabel);
            panel.Children.Add(_nameTextBox);
            panel.Children.Add(typeLabel);
            panel.Children.Add(_typeComboBox);
            panel.Children.Add(buttonsPanel);
            
            this.Content = panel;
        }

        public static async Task<(string Name, string Template)?> PromptAsync(Window owner)
        {
            var dialog = new NewProjectDialog();
            var res = await dialog.ShowDialog<bool>(owner);
            return res ? (dialog.ProjectName, dialog.ProjectTemplate) : null;
        }
    }

    public class OutputDialog : Window
    {
        public OutputDialog(string title, string contentText)
        {
            this.Title = title;
            this.Width = 600;
            this.Height = 400;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Background = Avalonia.Media.Brushes.Black;

            var panel = new DockPanel { Margin = new Thickness(10) };

            var titleBlock = new TextBlock
            {
                Text = title,
                Foreground = Avalonia.Media.Brushes.White,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(titleBlock);

            var closeButton = new Button
            {
                Content = "Close",
                Width = 80,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            closeButton.Click += (s, e) => this.Close();

            var textBox = new TextBox
            {
                Text = contentText,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1E1E1E")),
                Foreground = Avalonia.Media.Brushes.LightGray,
                FontFamily = new Avalonia.Media.FontFamily("Consolas"),
                FontSize = 12
            };

            var scrollViewer = new ScrollViewer { Content = textBox };

            panel.Children.Add(closeButton);
            panel.Children.Add(scrollViewer);

            DockPanel.SetDock(titleBlock, Dock.Top);
            DockPanel.SetDock(closeButton, Dock.Bottom);

            this.Content = panel;
        }

        public static void Show(Window owner, string title, string contentText)
        {
            var dialog = new OutputDialog(title, contentText);
            dialog.Show(owner);
        }
    }
}
