using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public static class SettingsManager
    {
        private static readonly string SettingsFilePath = GetDefaultSettingsFilePath();

        private static string GetDefaultSettingsFilePath()
        {
            if (IsRunningInUnitTest())
            {
                return Path.Combine(Path.GetTempPath(), "spancoder_test_settings.json");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpanCoder",
                "settings.json"
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

        private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SettingDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

        public static event Action<string>? SettingChanged;

        static SettingsManager()
        {
            if (IsRunningInUnitTest())
            {
                try
                {
                    string path = GetDefaultSettingsFilePath();
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch { }
            }

            // Register built-in settings
            RegisterBuiltIn("editor.fontSize", "Editor Font Size", "integer", "14");
            RegisterBuiltIn("editor.fontFamily", "Editor Font Family", "string", "Consolas");
            RegisterBuiltIn("editor.tabSize", "Editor Tab Size", "integer", "4");
            RegisterBuiltIn("workbench.theme", "Theme (Dark/Light)", "string", "Dark");
            RegisterBuiltIn("workbench.statusBarVisible", "Show Status Bar", "boolean", "true");
            RegisterBuiltIn("liveUnitTesting.enabled", "Enable Live Unit Testing", "boolean", "true");
            RegisterBuiltIn("ai.provider", "AI Provider (OpenAI/Gemini/Ollama)", "string", "OpenAI");
            RegisterBuiltIn("ai.openai.apikey", "OpenAI API Key", "string", "");
            RegisterBuiltIn("ai.openai.model", "OpenAI Model", "string", "gpt-4o");
            RegisterBuiltIn("ai.gemini.apikey", "Gemini API Key", "string", "");
            RegisterBuiltIn("ai.gemini.model", "Gemini Model", "string", "gemini-1.5-pro");
            RegisterBuiltIn("ai.ollama.apikey", "Ollama API Key (Optional)", "string", "");
            RegisterBuiltIn("ai.ollama.model", "Ollama Model", "string", "qwen2.5-coder:7b");
            RegisterBuiltIn("editor.vimEnabled", "Enable Vim Emulation", "boolean", "false");
            RegisterBuiltIn("editor.formatOnSave", "Format on Save", "boolean", "false");

            Load();
        }

        private static void RegisterBuiltIn(string id, string displayName, string type, string defaultValue)
        {
            var desc = new SettingDescriptor(id, displayName, type, defaultValue);
            _descriptors[id] = desc;
            _values[id] = defaultValue;
        }

        public static void RegisterExtensionSetting(SettingDescriptor desc)
        {
            _descriptors[desc.Id] = desc;
            if (!_values.ContainsKey(desc.Id))
            {
                _values[desc.Id] = desc.DefaultValue;
            }
        }

        public static void UnregisterExtensionSettings(string extensionId)
        {
            var descriptorsToRemove = new List<string>();
            foreach (var key in _descriptors.Keys)
            {
                if (key.StartsWith(extensionId + ".", StringComparison.OrdinalIgnoreCase))
                {
                    descriptorsToRemove.Add(key);
                }
            }
            foreach (var key in descriptorsToRemove)
            {
                _descriptors.Remove(key);
            }

            var valuesToRemove = new List<string>();
            foreach (var key in _values.Keys)
            {
                if (key.StartsWith(extensionId + ".", StringComparison.OrdinalIgnoreCase))
                {
                    valuesToRemove.Add(key);
                }
            }
            foreach (var key in valuesToRemove)
            {
                _values.Remove(key);
            }

            Save();
        }

        public static IEnumerable<SettingDescriptor> GetDescriptors() => _descriptors.Values;

        public static string Get(string id)
        {
            string val = "";
            if (_values.TryGetValue(id, out var v)) val = v;
            else if (_descriptors.TryGetValue(id, out var desc)) val = desc.DefaultValue;

            if (id.EndsWith(".apikey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(val))
            {
                return DpapiHelper.Decrypt(val);
            }
            return val;
        }

        public static T Get<T>(string id, T fallback)
        {
            string val = Get(id);
            if (string.IsNullOrEmpty(val)) return fallback;
            try
            {
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                return fallback;
            }
        }

        public static void Set(string id, string value)
        {
            if (id.EndsWith(".apikey", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                _values[id] = DpapiHelper.Encrypt(value);
            }
            else
            {
                _values[id] = value;
            }
            Save();
            SettingChanged?.Invoke(id);
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return;
                string json = File.ReadAllText(SettingsFilePath);
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    _values[prop.Name] = prop.Value.ToString();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Failed to load settings: {ex.Message}");
            }
        }

        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var kvp in _values)
                    {
                        writer.WriteString(kvp.Key, kvp.Value);
                    }
                    writer.WriteEndObject();
                }
                
                File.WriteAllText(SettingsFilePath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SettingsManager] Failed to save settings: {ex.Message}");
            }
        }
    }
}
