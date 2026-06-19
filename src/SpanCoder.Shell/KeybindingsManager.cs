using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SpanCoder.Shell
{
    public static class KeybindingsManager
    {
        private static readonly string KeybindingsFilePath = GetDefaultKeybindingsFilePath();
        private static readonly Dictionary<string, string> _customBindings = new(StringComparer.OrdinalIgnoreCase);

        public static event Action? KeybindingsChanged;

        static KeybindingsManager()
        {
            if (IsRunningInUnitTest())
            {
                try
                {
                    string path = GetDefaultKeybindingsFilePath();
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch { }
            }
            Load();
        }

        private static string GetDefaultKeybindingsFilePath()
        {
            if (IsRunningInUnitTest())
            {
                return Path.Combine(Path.GetTempPath(), "spancoder_test_keybindings.json");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpanCoder",
                "keybindings.json"
            );
        }

        private static bool IsRunningInUnitTest()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.FullName ?? "";
                if (name.Contains("test", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("xunit", StringComparison.OrdinalIgnoreCase) || 
                    name.Contains("nunit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static string GetShortcut(string commandId, string defaultShortcut)
        {
            if (_customBindings.TryGetValue(commandId, out var custom))
            {
                return custom;
            }
            return defaultShortcut;
        }

        public static void SetShortcut(string commandId, string gesture)
        {
            if (string.IsNullOrEmpty(gesture))
            {
                _customBindings.Remove(commandId);
            }
            else
            {
                _customBindings[commandId] = gesture;
            }
            Save();
            KeybindingsChanged?.Invoke();
        }

        public static void ResetShortcut(string commandId)
        {
            if (_customBindings.Remove(commandId))
            {
                Save();
                KeybindingsChanged?.Invoke();
            }
        }

        public static bool HasCustomShortcut(string commandId)
        {
            return _customBindings.ContainsKey(commandId);
        }

        public static void Load()
        {
            try
            {
                _customBindings.Clear();
                if (!File.Exists(KeybindingsFilePath)) return;
                string json = File.ReadAllText(KeybindingsFilePath);
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    _customBindings[prop.Name] = prop.Value.ToString();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeybindingsManager] Failed to load keybindings: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(KeybindingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var kvp in _customBindings)
                    {
                        writer.WriteString(kvp.Key, kvp.Value);
                    }
                    writer.WriteEndObject();
                }

                File.WriteAllText(KeybindingsFilePath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[KeybindingsManager] Failed to save keybindings: {ex.Message}");
            }
        }
        public static string NormalizeShortcut(string shortcut)
        {
            if (string.IsNullOrEmpty(shortcut)) return shortcut;

            if (shortcut.EndsWith("+/"))
            {
                return shortcut.Substring(0, shortcut.Length - 1) + "Oem2";
            }
            if (shortcut == "/")
            {
                return "Oem2";
            }
            if (shortcut.Length > 2 && shortcut[^1] == '/' && shortcut[^2] == '+')
            {
                return shortcut.Substring(0, shortcut.Length - 1) + "Oem2";
            }
            return shortcut;
        }
    }
}
