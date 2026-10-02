using XcpNgCenter.Shell.ViewModels;

namespace XcpNgCenter.Shell.Services;

/// <summary>Reuses cached tree nodes and their UI containers while applying a fresh inventory.</summary>
public static class InfrastructureTreeUpdater
{
    public static InfraTreeNode Apply(InfraTreeNode existing, InfraTreeNode incoming)
    {
        if (!ReferenceEquals(existing.Server, incoming.Server) || Key(existing, "") != Key(incoming, ""))
            return incoming;

        var nodes = new Dictionary<string, InfraTreeNode>(StringComparer.Ordinal);
        var parents = new Dictionary<InfraTreeNode, InfraTreeNode>();
        Index(existing, "", nodes, parents);
        // Detach moved objects before attaching them elsewhere. A node must not
        // occupy two UI parents while collection notifications are delivered.
        DetachMoved(incoming, "", null, nodes, parents);
        return Merge(incoming, "", nodes);
    }

    private static string Key(InfraTreeNode node, string parent)
        => node.OpaqueRef is { } reference
            ? $"{node.Kind}:{reference}"
            : $"{parent}/{node.Kind}:{node.Title}";

    private static void Index(InfraTreeNode node, string parentKey, Dictionary<string, InfraTreeNode> nodes,
        Dictionary<InfraTreeNode, InfraTreeNode> parents)
    {
        var key = Key(node, parentKey);
        nodes.TryAdd(key, node);
        foreach (var child in node.Children)
        {
            parents[child] = node;
            Index(child, key, nodes, parents);
        }
    }

    private static void DetachMoved(InfraTreeNode incoming, string parentKey, InfraTreeNode? desiredParent,
        Dictionary<string, InfraTreeNode> nodes, Dictionary<InfraTreeNode, InfraTreeNode> parents)
    {
        var key = Key(incoming, parentKey);
        nodes.TryGetValue(key, out var existing);
        if (existing != null && parents.TryGetValue(existing, out var oldParent)
            && !ReferenceEquals(oldParent, desiredParent))
            oldParent.Children.Remove(existing);
        foreach (var child in incoming.Children) DetachMoved(child, key, existing, nodes, parents);
    }

    private static InfraTreeNode Merge(InfraTreeNode incoming, string parentKey, Dictionary<string, InfraTreeNode> nodes)
    {
        var key = Key(incoming, parentKey);
        if (!nodes.TryGetValue(key, out var existing))
        {
            // Even a new group can contain a VM moved from an existing host.
            var children = incoming.Children.Select(child => Merge(child, key, nodes)).ToArray();
            incoming.Children.Clear();
            foreach (var child in children) incoming.Children.Add(child);
            return incoming;
        }
        existing.Title = incoming.Title;
        existing.Subtitle = incoming.Subtitle;
        existing.Detail = incoming.Detail;
        existing.ShowStatusIcon = incoming.ShowStatusIcon;
        existing.StatusIcon = incoming.StatusIcon;
        existing.StatusTooltip = incoming.StatusTooltip;
        var desired = incoming.Children.Select(child => Merge(child, key, nodes)).ToArray();
        var retained = new HashSet<InfraTreeNode>(desired);
        for (var i = existing.Children.Count - 1; i >= 0; i--)
            if (!retained.Contains(existing.Children[i])) existing.Children.RemoveAt(i);
        for (var i = 0; i < desired.Length; i++)
        {
            if (i < existing.Children.Count && ReferenceEquals(existing.Children[i], desired[i])) continue;
            var oldIndex = existing.Children.IndexOf(desired[i]);
            if (oldIndex >= 0) existing.Children.Move(oldIndex, i);
            else existing.Children.Insert(i, desired[i]);
        }
        return existing;
    }
}
