using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class RegisteredLanguageConfig
    {
        public string Extension { get; }
        public string? LineComment { get; }
        public string? BlockCommentStart { get; }
        public string? BlockCommentEnd { get; }
        public HashSet<string> Keywords { get; }
        public HashSet<string> Types { get; }

        public RegisteredLanguageConfig(LanguageConfigDescriptor desc)
        {
            Extension = desc.Extension;
            LineComment = desc.LineComment;
            BlockCommentStart = desc.BlockCommentStart;
            BlockCommentEnd = desc.BlockCommentEnd;
            Keywords = desc.Keywords != null ? new HashSet<string>(desc.Keywords, StringComparer.Ordinal) : new HashSet<string>();
            Types = desc.Types != null ? new HashSet<string>(desc.Types, StringComparer.Ordinal) : new HashSet<string>();
        }

        public RegisteredLanguageConfig(string extension, string? lineComment, string? blockCommentStart, string? blockCommentEnd)
        {
            Extension = extension;
            LineComment = lineComment;
            BlockCommentStart = blockCommentStart;
            BlockCommentEnd = blockCommentEnd;
            Keywords = new HashSet<string>();
            Types = new HashSet<string>();
        }
    }

    public static class LanguageConfigurationRegistry
    {
        private static readonly ConcurrentDictionary<string, RegisteredLanguageConfig> _registry = new(StringComparer.OrdinalIgnoreCase);

        static LanguageConfigurationRegistry()
        {
            // Register built-in core languages
            Register(new LanguageConfigDescriptor(".cs", "//", "/*", "*/"));
            Register(new LanguageConfigDescriptor(".js", "//", "/*", "*/"));
            Register(new LanguageConfigDescriptor(".html", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".css", null, "/*", "*/"));
            Register(new LanguageConfigDescriptor(".md", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".json", null, null, null));
            Register(new LanguageConfigDescriptor(".sln", "#", null, null,
                new List<string> { "Project", "EndProject", "Global", "EndGlobal", "GlobalSection", "EndGlobalSection" },
                new List<string> { "preSolution", "postSolution", "ActiveCfg", "Build.0" }
            ));
            Register(new LanguageConfigDescriptor(".csproj", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".slnx", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".xml", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".props", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".targets", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".config", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".settings", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".resx", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".pubxml", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".user", null, "<!--", "-->"));
            Register(new LanguageConfigDescriptor(".nuspec", null, "<!--", "-->"));
        }

        public static void Register(LanguageConfigDescriptor descriptor)
        {
            if (string.IsNullOrEmpty(descriptor.Extension)) return;
            
            string ext = descriptor.Extension;
            if (!ext.StartsWith("."))
            {
                ext = "." + ext;
            }
            
            var descWithExt = descriptor with { Extension = ext };
            _registry[ext] = new RegisteredLanguageConfig(descWithExt);
        }

        public static RegisteredLanguageConfig Get(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return new RegisteredLanguageConfig("", null, null, null);
            }

            string ext = extension;
            if (!ext.StartsWith("."))
            {
                ext = "." + ext;
            }

            if (_registry.TryGetValue(ext, out var config))
            {
                return config;
            }

            return new RegisteredLanguageConfig(ext, null, null, null);
        }
        public static void Unregister(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return;
            string ext = extension;
            if (!ext.StartsWith("."))
            {
                ext = "." + ext;
            }
            _registry.TryRemove(ext, out _);
        }
    }
}
