namespace Launchpad.Core;

public enum LayoutItemKind
{
    App,
    Folder,
}

public sealed class LayoutItem
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public LayoutItemKind Kind { get; set; }

    public List<LayoutItem> Children { get; set; } = [];

    public static LayoutItem ForApp(string appId, string title) => new()
    {
        Id = appId,
        Title = title,
        Kind = LayoutItemKind.App,
    };

    public static LayoutItem ForFolder(string title, IEnumerable<LayoutItem> children) => new()
    {
        Id = $"folder:{Guid.NewGuid():N}",
        Title = title,
        Kind = LayoutItemKind.Folder,
        Children = children.ToList(),
    };
}
