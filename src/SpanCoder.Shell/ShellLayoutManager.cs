using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public static class ShellLayoutManager
    {
        public static Menu BuildMenu(Action<string> onCommandInvoked)
        {
            var mainMenu = new Menu();

            // Fetch compiled menu items from the Source-Generated registry
            var items = GeneratedCommandRegistry.MenuItems.OrderBy(x => x.OrderPriority).ToList();
            var roots = new Dictionary<string, MenuItem>();

            foreach (var item in items)
            {
                var parts = item.MenuPath.Split('/');
                if (parts.Length == 0) continue;

                MenuItem? current = null;
                for (int i = 0; i < parts.Length; i++)
                {
                    var partName = parts[i];
                    string key = string.Join("/", parts.Take(i + 1));

                    if (i == 0)
                    {
                        if (!roots.TryGetValue(key, out current))
                        {
                            current = new MenuItem { Header = partName };
                            roots[key] = current;
                            mainMenu.Items.Add(current);
                        }
                    }
                    else
                    {
                        MenuItem parent = current!;
                        var existing = parent.Items.OfType<MenuItem>().FirstOrDefault(x => (x.Header as string) == partName);
                        if (existing == null)
                        {
                            current = new MenuItem { Header = partName };
                            parent.Items.Add(current);
                        }
                        else
                        {
                            current = existing;
                        }
                    }
                }

                if (current != null)
                {
                    var commandId = item.CommandId;
                    
                    // Bind the click event to fire command request
                    current.Click += (s, e) =>
                    {
                        onCommandInvoked(commandId);
                    };

                    // Look up and attach Key Gestures
                    var cmdDesc = GeneratedCommandRegistry.Commands.FirstOrDefault(x => x.Id == commandId);
                    if (!string.IsNullOrEmpty(cmdDesc.DefaultShortcut))
                    {
                        try
                        {
                            string shortcut = cmdDesc.DefaultShortcut;
                            if (shortcut.EndsWith("+/"))
                            {
                                shortcut = shortcut.Substring(0, shortcut.Length - 1) + "Oem2";
                            }
                            else if (shortcut == "/")
                            {
                                shortcut = "Oem2";
                            }
                            current.InputGesture = KeyGesture.Parse(shortcut);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ShellLayoutManager] Failed to parse shortcut '{cmdDesc.DefaultShortcut}' for command '{commandId}': {ex.Message}");
                        }
                    }
                }
            }

            return mainMenu;
        }
    }
}
