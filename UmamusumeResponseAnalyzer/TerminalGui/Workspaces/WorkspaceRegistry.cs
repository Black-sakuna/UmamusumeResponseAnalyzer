namespace UmamusumeResponseAnalyzer.TerminalGui;

internal sealed class WorkspaceRegistry
{
    readonly Dictionary<string, Workspace> registrations = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Workspace> registrationOrder = [];

    internal Workspace? Current { get; private set; }

    internal Workspace[] SnapshotRegistrationOrder()
        => [.. registrationOrder];

    internal (Workspace Workspace, bool Created) Create(string title)
    {
        if (registrations.TryGetValue(title, out var existing))
            return (existing, false);

        var workspace = new Workspace(title);
        registrations.Add(workspace.Title, workspace);
        registrationOrder.Add(workspace);
        Current ??= workspace;
        return (workspace, true);
    }

    internal void EnsureLive(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.IsRemoved)
            throw RemovedException(workspace);
        if (!registrations.TryGetValue(workspace.Title, out var registered) ||
            !ReferenceEquals(registered, workspace))
        {
            throw new InvalidOperationException(
                $"Workspace '{workspace.Title}' is not the active registered generation.");
        }
    }

    internal bool Remove(Workspace workspace, out Workspace? replacement)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.IsRemoved)
        {
            replacement = Current;
            return false;
        }

        EnsureLive(workspace);
        registrations.Remove(workspace.Title);
        registrationOrder.Remove(workspace);
        workspace.IsRemoved = true;
        if (ReferenceEquals(Current, workspace))
            Current = registrationOrder.FirstOrDefault();
        replacement = Current;
        return true;
    }

    internal void SwitchTo(Workspace workspace)
    {
        EnsureLive(workspace);
        Current = workspace;
    }

    internal static InvalidOperationException RemovedException(Workspace workspace)
        => new($"Workspace '{workspace.Title}' was removed and this handle cannot be used.");
}
