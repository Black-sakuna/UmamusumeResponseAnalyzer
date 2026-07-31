using System.Diagnostics;
using System.Text;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Commands;

internal static class HostCommands
{
    const string WorkspaceUsage =
        "用法: /workspace | /workspace switch [<title>|\"<title>\"] | /workspace list";
    const string PluginUsage =
        "用法: /plugin [list] | /plugin load <InternalName> | " +
        "/plugin unload <InternalName> | /plugin reload <InternalName>";

    internal sealed record WorkspaceItem(
        Workspace Handle,
        string? ShortcutText = null);

    internal sealed record Snapshot(
        IReadOnlyList<WorkspaceItem> Workspaces,
        Workspace? CurrentWorkspace,
        IReadOnlyList<PluginRuntimeStatus> Plugins)
    {
        internal static Snapshot Empty { get; } = new([], null, []);
    }

    internal sealed record DisplayItem(
        string Text,
        Workspace? Workspace = null);

    internal sealed record Display(
        string Title,
        IReadOnlyList<DisplayItem> Items,
        int? SelectedIndex = null);

    internal sealed record Result(
        string? Message = null,
        UiSeverity Severity = UiSeverity.Info,
        Display? Display = null,
        Workspace? SwitchWorkspace = null,
        bool RefreshSnapshot = false);

    internal static IReadOnlyList<PluginRuntimeStatus> SnapshotPlugins()
        => PluginManager.SnapshotPluginStatuses();

    internal static async Task<Result?> ExecuteAsync(
        string command,
        Snapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!command.StartsWith('/'))
            return null;

        var (name, arguments) = Tokenize(command[1..]);
        if (name.Length == 0)
            return Warning("命令为空。");

        if (name.Equals("workspace", StringComparison.OrdinalIgnoreCase))
            return RunWorkspaceCommand(arguments, snapshot);
        if (name.Equals("plugin", StringComparison.OrdinalIgnoreCase))
            return await RunPluginCommandAsync(arguments, snapshot, cancellationToken);

        return Warning($"未知命令: /{name}");
    }

    internal static IReadOnlyList<string> Complete(string input, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!input.StartsWith('/'))
            return [];

        var body = input[1..];
        var spaceIndex = body.IndexOf(' ');
        if (spaceIndex < 0)
            return CompleteByPrefix(input, ["/plugin", "/workspace"]);

        var name = body[..spaceIndex];
        var rest = body[(spaceIndex + 1)..];
        return name.ToLowerInvariant() switch
        {
            "workspace" => CompleteWorkspaceCommand(rest, snapshot.Workspaces),
            "plugin" => CompletePluginCommand(rest, snapshot.Plugins),
            _ => []
        };
    }

    internal static (string Token, string Remainder) Tokenize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0)
            return (string.Empty, string.Empty);

        var index = value.IndexOf(' ');
        return index < 0
            ? (value, string.Empty)
            : (value[..index], value[(index + 1)..].Trim());
    }

    internal static string ParseWorkspaceTitle(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StartsWith('"'))
            return value;

        var title = new StringBuilder(value.Length);
        for (var i = 1; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '"':
                    if (i != value.Length - 1)
                    {
                        throw new FormatException(
                            "Quoted workspace title 的结束双引号后不能有其它内容。");
                    }
                    if (string.IsNullOrWhiteSpace(title.ToString()))
                    {
                        throw new FormatException(
                            "Quoted workspace title 不能为空或仅包含空白。");
                    }
                    return title.ToString();
                case '\\':
                    if (++i >= value.Length)
                        throw new FormatException("Quoted workspace title 不能以反斜杠结尾。");
                    if (value[i] is not ('"' or '\\'))
                    {
                        throw new FormatException(
                            $"Quoted workspace title 不支持转义 \\{value[i]}；仅支持 \\\" 与 \\\\。");
                    }
                    title.Append(value[i]);
                    break;
                default:
                    title.Append(value[i]);
                    break;
            }
        }

        throw new FormatException("Quoted workspace title 缺少结束双引号。");
    }

    static Result RunWorkspaceCommand(string arguments, Snapshot snapshot)
    {
        var (subcommand, remainder) = Tokenize(arguments);
        if (subcommand.Length == 0)
            return ShowWorkspaces(snapshot, selectable: true);

        if (subcommand.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return remainder.Length == 0
                ? ShowWorkspaces(snapshot, selectable: false)
                : Warning(WorkspaceUsage);
        }

        if (subcommand.Equals("switch", StringComparison.OrdinalIgnoreCase))
        {
            if (remainder.Length == 0)
                return ShowWorkspaces(snapshot, selectable: true);

            try
            {
                var title = ParseWorkspaceTitle(remainder);
                var workspace = snapshot.Workspaces
                    .Select(item => item.Handle)
                    .FirstOrDefault(candidate =>
                        candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
                return workspace is null
                    ? Warning($"workspace 不存在: {title}")
                    : new(SwitchWorkspace: workspace, RefreshSnapshot: true);
            }
            catch (FormatException ex)
            {
                return new(ex.Message, UiSeverity.Error);
            }
        }

        return Warning(WorkspaceUsage);
    }

    static async Task<Result> RunPluginCommandAsync(
        string arguments,
        Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var (subcommand, remainder) = Tokenize(arguments);
        if (subcommand.Length == 0)
            return ShowPlugins(snapshot.Plugins);

        if (subcommand.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return remainder.Length == 0
                ? ShowPlugins(snapshot.Plugins)
                : Warning(PluginUsage);
        }

        var action = subcommand.ToLowerInvariant();
        if (action is not ("load" or "unload" or "reload"))
            return Warning(PluginUsage);

        var (pluginName, extra) = Tokenize(remainder);
        if (pluginName.Length == 0 || extra.Length != 0)
            return Warning(PluginUsage);

        var plugin = snapshot.Plugins.FirstOrDefault(status =>
            status.InternalName.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
            return Warning($"插件不存在: {pluginName}");

        cancellationToken.ThrowIfCancellationRequested();
        var result = (action switch
        {
            "load" => await PluginManager.LoadPluginsAsync(plugin.InternalName),
            "unload" => await PluginManager.UnloadPluginsAsync(plugin.InternalName),
            "reload" => await PluginManager.ReloadPluginsAsync(plugin.InternalName),
            _ => throw new UnreachableException()
        }).Single();

        var localizedAction = action switch
        {
            "load" => "加载",
            "unload" => "卸载",
            "reload" => "重载",
            _ => throw new UnreachableException()
        };
        if (result.Outcome == PluginManager.PluginLifecycleOutcome.RestartRequired)
        {
            return Warning(
                $"插件 {plugin.InternalName} 需要重启才能{localizedAction}。",
                refreshSnapshot: true);
        }
        if (result.Outcome == PluginManager.PluginLifecycleOutcome.Failed)
        {
            return new(
                $"插件 {plugin.InternalName} {localizedAction}失败。",
                UiSeverity.Error,
                RefreshSnapshot: true);
        }

        var message = $"插件 {plugin.InternalName} 已{localizedAction}。";
        return new(
            message,
            UiSeverity.Success,
            new("Plugin command", [new($"{plugin.InternalName} 已{localizedAction}。")]),
            RefreshSnapshot: true);
    }

    static Result ShowWorkspaces(Snapshot snapshot, bool selectable)
    {
        if (snapshot.Workspaces.Count == 0)
        {
            return new(Display: new(
                "Workspaces",
                [new("（没有已注册 workspace）")]));
        }

        var items = snapshot.Workspaces.Select(item =>
        {
            var marker = ReferenceEquals(item.Handle, snapshot.CurrentWorkspace) ? "*" : " ";
            var shortcut = string.IsNullOrEmpty(item.ShortcutText)
                ? string.Empty
                : $" [{item.ShortcutText}]";
            return new DisplayItem(
                $"{marker} {item.Handle.Title}{shortcut}",
                item.Handle);
        }).ToArray();
        int? selectedIndex = selectable
            ? Math.Max(0, Array.FindIndex(
                items,
                item => ReferenceEquals(item.Workspace, snapshot.CurrentWorkspace)))
            : null;
        return new(Display: new("Workspaces", items, selectedIndex));
    }

    static Result ShowPlugins(IReadOnlyList<PluginRuntimeStatus> plugins)
    {
        if (plugins.Count == 0)
            return new(Display: new("Plugins", [new("（没有已知插件）")]));

        return new(Display: new(
            "Plugins",
            plugins.Select(plugin =>
            {
                var state = plugin.IsLoaded
                    ? "loaded"
                    : plugin.IsAvailable ? "unloaded" : "failed";
                var displayName = plugin.DisplayName == plugin.InternalName
                    ? string.Empty
                    : $" ({plugin.DisplayName})";
                var version = plugin.Version is null ? string.Empty : $" v{plugin.Version}";
                var author = string.IsNullOrWhiteSpace(plugin.Author)
                    ? string.Empty
                    : $" by {plugin.Author}";
                var host = plugin.LoadInHost ? " host" : string.Empty;
                return new DisplayItem(
                    $"{state} {plugin.InternalName}{displayName}{version}{author}{host}");
            }).ToArray()));
    }

    static IReadOnlyList<string> CompleteWorkspaceCommand(
        string rest,
        IReadOnlyList<WorkspaceItem> workspaces)
    {
        var subcommandSpaceIndex = rest.IndexOf(' ');
        if (subcommandSpaceIndex < 0)
        {
            return CompleteByPrefix($"/workspace {rest}",
            [
                "/workspace list",
                "/workspace switch"
            ]);
        }

        var subcommand = rest[..subcommandSpaceIndex];
        var arguments = rest[(subcommandSpaceIndex + 1)..];
        return subcommand.Equals("switch", StringComparison.OrdinalIgnoreCase)
            ? CompleteWorkspaceTitles(arguments, workspaces)
            : [];
    }

    static IReadOnlyList<string> CompleteWorkspaceTitles(
        string arguments,
        IReadOnlyList<WorkspaceItem> workspaces)
    {
        var prefix = WorkspaceCompletionPrefix(arguments);
        return workspaces
            .Select(item => item.Handle.Title)
            .Where(title => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(title => $"/workspace switch {QuoteWorkspaceTitleIfNeeded(title)}")
            .OrderBy(command => command, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string WorkspaceCompletionPrefix(string value)
    {
        if (!value.StartsWith('"'))
            return value;

        var prefix = new StringBuilder(value.Length);
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '"')
                break;
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '"' or '\\')
                i++;
            prefix.Append(value[i]);
        }
        return prefix.ToString();
    }

    static string QuoteWorkspaceTitleIfNeeded(string title)
    {
        if (!title.Any(char.IsWhiteSpace) && !title.Contains('"') && !title.Contains('\\'))
            return title;

        return $"\"{title.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    static IReadOnlyList<string> CompletePluginCommand(
        string rest,
        IReadOnlyList<PluginRuntimeStatus> plugins)
    {
        var subcommandSpaceIndex = rest.IndexOf(' ');
        if (subcommandSpaceIndex < 0)
        {
            return CompleteByPrefix($"/plugin {rest}",
            [
                "/plugin list",
                "/plugin load",
                "/plugin unload",
                "/plugin reload"
            ]);
        }

        var subcommand = rest[..subcommandSpaceIndex];
        if (!subcommand.Equals("load", StringComparison.OrdinalIgnoreCase) &&
            !subcommand.Equals("unload", StringComparison.OrdinalIgnoreCase) &&
            !subcommand.Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var arguments = rest[(subcommandSpaceIndex + 1)..];
        return CompleteByPrefix(
            $"/plugin {subcommand} {arguments}",
            plugins.Select(plugin => $"/plugin {subcommand} {plugin.InternalName}"));
    }

    static IReadOnlyList<string> CompleteByPrefix(
        string prefix,
        IEnumerable<string> candidates)
        => candidates
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    static Result Warning(string message, bool refreshSnapshot = false)
        => new(message, UiSeverity.Warning, RefreshSnapshot: refreshSnapshot);

}
