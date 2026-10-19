namespace Launchpad.Core;

public static class LayoutEditor
{
    public static void SyncManualApps(LayoutDocument layout, List<LaunchableApp> manualApps)
    {
        layout.Items ??= [];

        var catalog = manualApps
            .Where(app => !string.IsNullOrWhiteSpace(app.Id) && !string.IsNullOrWhiteSpace(app.Name))
            .GroupBy(app => app.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(app => app.Id, StringComparer.Ordinal);
        layout.ManualApps = catalog.Values.ToList();

        var placed = new HashSet<string>(StringComparer.Ordinal);
        var safeItems = new List<LayoutItem>();

        foreach (var item in layout.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id))
            {
                continue;
            }

            if (item.Kind == LayoutItemKind.App)
            {
                if (catalog.TryGetValue(item.Id, out var app) && placed.Add(app.Id))
                {
                    safeItems.Add(LayoutItem.ForApp(app.Id, app.Name));
                }
                continue;
            }

            if (item.Kind != LayoutItemKind.Folder || item.Children is null)
            {
                continue;
            }

            var members = new List<LayoutItem>();
            foreach (var child in item.Children)
            {
                if (child is null || child.Kind != LayoutItemKind.App || !catalog.TryGetValue(child.Id, out var app) || !placed.Add(app.Id))
                {
                    continue;
                }
                members.Add(LayoutItem.ForApp(app.Id, app.Name));
            }

            if (members.Count == 1)
            {
                safeItems.Add(members[0]);
            }
            else if (members.Count > 1)
            {
                safeItems.Add(new LayoutItem
                {
                    Id = item.Id,
                    Title = string.IsNullOrWhiteSpace(item.Title) ? "文件夹" : item.Title,
                    Kind = LayoutItemKind.Folder,
                    Children = members,
                });
            }
        }

        // 添加新应用到末尾
        var additions = catalog.Values.Where(app => placed.Add(app.Id));
        foreach (var app in additions)
        {
            safeItems.Add(LayoutItem.ForApp(app.Id, app.Name));
        }

        layout.Items = safeItems;
    }

    public static bool MoveApp(
        LayoutDocument layout,
        string sourceAppId,
        string destinationItemId,
        bool intoFolder = false,
        bool insertAfter = false)
    {
        if (string.Equals(sourceAppId, destinationItemId, StringComparison.Ordinal))
        {
            return false;
        }

        var sourceLocation = FindItem(layout, sourceAppId);
        var destinationLocation = FindItem(layout, destinationItemId);
        if (sourceLocation is null || destinationLocation is null || sourceLocation.Value.Item.Kind != LayoutItemKind.App)
        {
            return false;
        }

        if (ReferenceEquals(sourceLocation.Value.Container, destinationLocation.Value.Container))
        {
            if (intoFolder && destinationLocation.Value.Item.Kind == LayoutItemKind.Folder)
            {
                sourceLocation.Value.Container.Remove(sourceLocation.Value.Item);
                destinationLocation.Value.Item.Children.Add(sourceLocation.Value.Item);
                return true;
            }

            if (intoFolder && ReferenceEquals(sourceLocation.Value.Container, layout.Items)
                && sourceLocation.Value.Item.Kind == LayoutItemKind.App
                && destinationLocation.Value.Item.Kind == LayoutItemKind.App)
            {
                var groupedApp = sourceLocation.Value.Item;
                sourceLocation.Value.Container.Remove(groupedApp);
                var rootTarget = FindItem(layout, destinationItemId);
                if (rootTarget is null)
                {
                    return false;
                }

                var index = rootTarget.Value.Container.IndexOf(rootTarget.Value.Item);
                rootTarget.Value.Container[index] = LayoutItem.ForFolder("文件夹", [rootTarget.Value.Item, groupedApp]);
                return true;
            }

            return Reorder(sourceLocation.Value.Container, sourceLocation.Value.Item.Id, destinationLocation.Value.Item.Id, insertAfter);
        }

        if (intoFolder && destinationLocation.Value.Item.Kind == LayoutItemKind.Folder
            && ReferenceEquals(destinationLocation.Value.Item.Children, sourceLocation.Value.Container))
        {
            return false;
        }

        var source = sourceLocation.Value.Item;
        sourceLocation.Value.Container.Remove(source);
        CollapseFolder(layout, sourceLocation.Value.Container);

        var target = FindItem(layout, destinationItemId);
        if (target is null)
        {
            return false;
        }

        if (intoFolder && target.Value.Item.Kind == LayoutItemKind.Folder)
        {
            target.Value.Item.Children.Add(source);
            return true;
        }

        if (intoFolder && target.Value.Item.Kind == LayoutItemKind.App && !ReferenceEquals(target.Value.Container, layout.Items))
        {
            target.Value.Container.Add(source);
            return true;
        }

        if (intoFolder && target.Value.Item.Kind == LayoutItemKind.App && ReferenceEquals(target.Value.Container, layout.Items))
        {
            var index = target.Value.Container.IndexOf(target.Value.Item);
            target.Value.Container[index] = LayoutItem.ForFolder("文件夹", [target.Value.Item, source]);
            return true;
        }

        var insertionIndex = target.Value.Container.IndexOf(target.Value.Item) + (insertAfter ? 1 : 0);
        target.Value.Container.Insert(insertionIndex, source);
        return true;
    }

    public static IEnumerable<string> AppIds(LayoutDocument layout) => layout.Items
        .SelectMany(item => item.Kind == LayoutItemKind.App ? new[] { item.Id } : item.Children.Select(child => child.Id));

    public static bool MoveOutOfFolder(LayoutDocument layout, string sourceAppId)
    {
        var source = FindItem(layout, sourceAppId);
        if (source is null || ReferenceEquals(source.Value.Container, layout.Items) || source.Value.Item.Kind != LayoutItemKind.App)
        {
            return false;
        }

        var app = source.Value.Item;
        source.Value.Container.Remove(app);
        CollapseFolder(layout, source.Value.Container);
        layout.Items.Add(app);
        return true;
    }

    public static bool MoveFolder(LayoutDocument layout, string folderId, string destinationItemId, bool insertAfter = false)
    {
        var source = layout.Items.FirstOrDefault(item =>
            item.Kind == LayoutItemKind.Folder && string.Equals(item.Id, folderId, StringComparison.Ordinal));
        var target = layout.Items.FirstOrDefault(item => string.Equals(item.Id, destinationItemId, StringComparison.Ordinal));
        return source is not null && target is not null && Reorder(layout.Items, source.Id, target.Id, insertAfter);
    }

    private static bool Reorder(List<LayoutItem> items, string sourceId, string targetId, bool insertAfter)
    {
        var source = items.FirstOrDefault(item => string.Equals(item.Id, sourceId, StringComparison.Ordinal));
        var target = items.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
        if (source is null || target is null || ReferenceEquals(source, target))
        {
            return false;
        }

        items.Remove(source);
        var insertionIndex = items.IndexOf(target) + (insertAfter ? 1 : 0);
        items.Insert(insertionIndex, source);
        return true;
    }

    private static void CollapseFolder(LayoutDocument layout, List<LayoutItem> container)
    {
        var folder = layout.Items.FirstOrDefault(item => item.Kind == LayoutItemKind.Folder && ReferenceEquals(item.Children, container));
        if (folder is null || folder.Children.Count > 1)
        {
            return;
        }

        var index = layout.Items.IndexOf(folder);
        if (folder.Children.Count == 0)
        {
            layout.Items.RemoveAt(index);
        }
        else
        {
            layout.Items[index] = folder.Children[0];
        }
    }

    private static (LayoutItem Item, List<LayoutItem> Container)? FindItem(LayoutDocument layout, string id)
    {
        var root = layout.Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (root is not null)
        {
            return (root, layout.Items);
        }

        foreach (var folder in layout.Items.Where(item => item.Kind == LayoutItemKind.Folder))
        {
            var child = folder.Children.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (child is not null)
            {
                return (child, folder.Children);
            }
        }

        return null;
    }
}
