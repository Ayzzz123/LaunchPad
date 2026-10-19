namespace Launchpad.Core;

public static class AppSearch
{
    public static IReadOnlyList<LaunchableApp> Filter(IEnumerable<LaunchableApp> apps, string query)
    {
        var search = query.Trim();
        return string.IsNullOrEmpty(search)
            ? apps.ToArray()
            : apps.Where(app => app.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
