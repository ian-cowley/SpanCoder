using System;

namespace SpanCoder.Contracts
{
    public readonly record struct CommandDescriptor(
        string Id,
        string DisplayName,
        string Category,
        string DefaultShortcut
    );

    public readonly record struct MenuItemDescriptor(
        string CommandId,
        string MenuPath, // e.g., "File/File System/New"
        int OrderPriority
    );

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
    public sealed class CommandAttribute : Attribute
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public string DefaultShortcut { get; }

        public CommandAttribute(string id, string displayName, string category = "", string defaultShortcut = "")
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            DefaultShortcut = defaultShortcut;
        }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class MenuItemAttribute : Attribute
    {
        public string CommandId { get; }
        public string MenuPath { get; }
        public int OrderPriority { get; }

        public MenuItemAttribute(string commandId, string menuPath, int orderPriority = 100)
        {
            CommandId = commandId;
            MenuPath = menuPath;
            OrderPriority = orderPriority;
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SpanCoderPluginAttribute : Attribute
    {
        public string Id { get; }
        public SpanCoderPluginAttribute(string id)
        {
            Id = id;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class CustomPanelAttribute : Attribute
    {
        public string Id { get; }
        public string Title { get; }
        public CustomPanelAttribute(string id, string title)
        {
            Id = id;
            Title = title;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class SettingAttribute : Attribute
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Type { get; }
        public string DefaultValue { get; }

        public SettingAttribute(string id, string displayName, string type = "string", string defaultValue = "")
        {
            Id = id;
            DisplayName = displayName;
            Type = type;
            DefaultValue = defaultValue;
        }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class StatusBarItemAttribute : Attribute
    {
        public string Id { get; }
        public string Text { get; }
        public string Tooltip { get; }
        public string CommandId { get; }
        public int Alignment { get; } // 0 = Left, 1 = Right
        public int OrderPriority { get; }

        public StatusBarItemAttribute(string id, string text, string tooltip = "", string commandId = "", int alignment = 1, int orderPriority = 100)
        {
            Id = id;
            Text = text;
            Tooltip = tooltip;
            CommandId = commandId;
            Alignment = alignment;
            OrderPriority = orderPriority;
        }
    }

    public interface IExtensionManager
    {
        int Port { get; }
        event Action<string, ExtensionManifest> ExtensionRegistered;
        event Action<string, string> PanelContentUpdated;
        event Action<string> ExtensionUnregistered;
        event Action<string, string, string, string, string>? StatusBarItemUpdated;
        void ExecuteCommand(string extensionId, string commandId);
        void ExecuteCommandWithContext(string extensionId, string commandId, string activeFilePath, string activeContent);
        void AddPendingToken(string token, string extensionId);
        System.Threading.Tasks.Task<string?> FormatDocumentAsync(string extensionId, int documentId, string filePath, string content);
        void UninstallPlugin(string extensionId);
        void InstallAndLaunchPlugin(string pluginDir);
    }


    public readonly record struct LanguageConfigDescriptor(
        string Extension,
        string? LineComment,
        string? BlockCommentStart,
        string? BlockCommentEnd,
        System.Collections.Generic.List<string>? Keywords = null,
        System.Collections.Generic.List<string>? Types = null
    );

    public readonly record struct ToolbarItemDescriptor(
        string CommandId,
        string DisplayName,
        string? IconPath,
        int OrderPriority
    );

    public readonly record struct SettingDescriptor(
        string Id,
        string DisplayName,
        string Type, // "string", "boolean", "integer"
        string DefaultValue
    );

    public readonly record struct StatusBarItemDescriptor(
        string Id,
        string Text,
        string? Tooltip,
        string? CommandId,
        int Alignment, // 0 = Left, 1 = Right
        int OrderPriority
    );

    public struct ExtensionManifest
    {
        public string Id { get; }
        public System.Collections.Generic.List<CommandDescriptor> Commands { get; }
        public System.Collections.Generic.List<MenuItemDescriptor> MenuItems { get; }
        public System.Collections.Generic.List<PanelDescriptor> Panels { get; }
        public System.Collections.Generic.List<LanguageConfigDescriptor> Languages { get; }
        public System.Collections.Generic.List<ToolbarItemDescriptor> ToolbarItems { get; }
        public System.Collections.Generic.List<SettingDescriptor> Settings { get; }
        public System.Collections.Generic.List<StatusBarItemDescriptor> StatusBarItems { get; }
        public System.Collections.Generic.List<string> Formatters { get; }

        public ExtensionManifest(string id, System.Collections.Generic.List<CommandDescriptor> commands, System.Collections.Generic.List<MenuItemDescriptor> menuItems, System.Collections.Generic.List<PanelDescriptor> panels)
            : this(id, commands, menuItems, panels, new(), new(), new(), new(), new())
        {
        }

        public ExtensionManifest(
            string id, 
            System.Collections.Generic.List<CommandDescriptor> commands, 
            System.Collections.Generic.List<MenuItemDescriptor> menuItems, 
            System.Collections.Generic.List<PanelDescriptor> panels,
            System.Collections.Generic.List<LanguageConfigDescriptor> languages,
            System.Collections.Generic.List<ToolbarItemDescriptor> toolbarItems,
            System.Collections.Generic.List<SettingDescriptor> settings,
            System.Collections.Generic.List<StatusBarItemDescriptor> statusBarItems)
            : this(id, commands, menuItems, panels, languages, toolbarItems, settings, statusBarItems, new())
        {
        }

        public ExtensionManifest(
            string id, 
            System.Collections.Generic.List<CommandDescriptor> commands, 
            System.Collections.Generic.List<MenuItemDescriptor> menuItems, 
            System.Collections.Generic.List<PanelDescriptor> panels,
            System.Collections.Generic.List<LanguageConfigDescriptor> languages,
            System.Collections.Generic.List<ToolbarItemDescriptor> toolbarItems,
            System.Collections.Generic.List<SettingDescriptor> settings,
            System.Collections.Generic.List<StatusBarItemDescriptor> statusBarItems,
            System.Collections.Generic.List<string>? formatters)
        {
            Id = id;
            Commands = commands;
            MenuItems = menuItems;
            Panels = panels;
            Languages = languages;
            ToolbarItems = toolbarItems;
            Settings = settings;
            StatusBarItems = statusBarItems;
            Formatters = formatters ?? new();
        }
    }

    public struct PanelDescriptor
    {
        public string Id { get; }
        public string Title { get; }

        public PanelDescriptor(string id, string title)
        {
            Id = id;
            Title = title;
        }
    }
}
