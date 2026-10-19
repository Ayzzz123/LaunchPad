using Launchpad.Core;

var checks = new List<(string Name, Action Run)>
{
    ("search finds Chinese and English names without case sensitivity", TestSearch),
    ("removing a manual app cleans up duplicate and undersized folder entries", TestManualAppsCleanup),
    ("applications can be grouped into and moved out of folders", TestFolderMoves),
    ("reordering an application inside a folder preserves the other members", TestFolderReorder),
    ("folders can be reordered alongside root applications", TestFolderRootReorder),
    ("moving an application back out of a folder restores a root tile", TestFolderMoveOut),
    ("layout storage restores its last good backup after a corrupt write", TestLayoutBackup),
    ("manual apps sync preserves user order and adds new apps to end", TestManualAppsSync),
    ("manual apps sync removes apps not in the manual list", TestManualAppsRemoval),
    ("manual apps sync deduplicates by id", TestManualAppsDedupe),
    ("manual catalog is deduplicated as well as tiles", TestManualCatalogDedupe),
    ("escape unwinds overlays, folders, editing, search and settings in order", TestBackPriority),
    ("editing is disabled in search and settings", TestEditingState),
    ("window centers in physical pixels at 100 through 200 percent DPI", TestWindowGeometry),
    ("small screens reduce grid capacity without dropping below one slot", TestAdaptiveGrid),
    ("renaming persists inside a folder and keeps app identity", TestRenamePersistence),
    ("launch contexts with different AppData views use the same user layout", TestUserStoreContexts),
    ("user storage migrates a valid legacy backup and retains a recovery copy", TestUserStoreMigrationBackup),
    ("an existing user backup is not replaced by a different legacy catalog", TestUserStoreExistingBackup),
};

var failures = 0;
foreach (var (name, run) in checks)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{checks.Count - failures}/{checks.Count} checks passed");
return failures == 0 ? 0 : 1;

static void TestManualCatalogDedupe()
{
    var layout = new LayoutDocument();
    var app = NewApp("one", "One");
    LayoutEditor.SyncManualApps(layout, [app, app]);
    Equal(1, layout.ManualApps.Count);
}

static void TestBackPriority()
{
    var state = new LauncherState { FolderId = "folder" };
    state.SetEditing(true);
    state.SearchText = "query";
    state.Page = LauncherPage.Apps;
    state.HasOverlay = true;
    Equal(BackAction.Overlay, state.Back());
    Equal(BackAction.Folder, state.Back());
    Equal(BackAction.Editing, state.Back());
    Equal(BackAction.Search, state.Back());
    Equal(BackAction.Settings, state.Back());
    Equal(BackAction.Hide, state.Back());
}

static void TestEditingState()
{
    var state = new LauncherState { SearchText = "中文" };
    state.SetEditing(true);
    True(!state.IsEditing);
    state.SearchText = "";
    state.Page = LauncherPage.Hotkey;
    state.SetEditing(true);
    True(!state.IsEditing);
    state.Page = LauncherPage.Home;
    state.SetEditing(true);
    True(state.IsEditing);
}

static void TestWindowGeometry()
{
    foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2 })
    {
        var rect = LauncherGeometry.Center(-2560, -300, 2560, 1400, scale, scale);
        True(rect.X >= -2560 && rect.Y >= -300);
        True(rect.X + rect.Width <= 0 && rect.Y + rect.Height <= 1100);
        True(Math.Abs((rect.X + rect.Width / 2d) - (-1280)) <= .5);
        True(Math.Abs((rect.Y + rect.Height / 2d) - 400) <= .5);
    }
}

static void TestAdaptiveGrid()
{
    Equal((7, 4), LauncherGeometry.Grid(1012, 534));
    Equal((4, 2), LauncherGeometry.Grid(540, 280));
    Equal((1, 1), LauncherGeometry.Grid(80, 80));
}

static void TestRenamePersistence()
{
    var root = Path.Combine(Path.GetTempPath(), "Launchpad.Core.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var layout = new LayoutDocument();
        LayoutEditor.SyncManualApps(layout, [NewApp("one", "旧名称"), NewApp("two", "第二个")]);
        LayoutEditor.MoveApp(layout, "one", "two", intoFolder: true);
        var folderId = layout.Items.Single().Id;
        LayoutEditor.SyncManualApps(layout, [NewApp("one", "新名称"), NewApp("two", "第二个")]);
        new LayoutStore(root).Save(layout);
        var restored = new LayoutStore(root).Load();
        Equal(folderId, restored.Items.Single().Id);
        Equal("新名称", restored.Items.Single().Children.Single(child => child.Id == "one").Title);
        Equal("one", AppSearch.Filter(restored.ManualApps, "新名").Single().Id);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void TestSearch()
{
    var apps = new[]
    {
        NewApp("browser", "Microsoft Edge"),
        NewApp("editor", "记事本"),
    };

    Equal("browser", AppSearch.Filter(apps, "EDGE").Single().Id);
    Equal("editor", AppSearch.Filter(apps, "记事").Single().Id);
    Equal(0, AppSearch.Filter(apps, "missing").Count);
}

static void TestFolderMoves()
{
    var layout = NewLayout("first", "second", "third");

    True(LayoutEditor.MoveApp(layout, "first", "second", intoFolder: true));
    Equal(LayoutItemKind.Folder, layout.Items.Single(item => item.Kind == LayoutItemKind.Folder).Kind);
    True(LayoutEditor.MoveApp(layout, "first", "third", intoFolder: true));
    Equal(new[] { "third", "first" }, layout.Items.Single(item => item.Kind == LayoutItemKind.Folder).Children.Select(child => child.Id).ToArray());
    Equal("second", layout.Items.Single(item => item.Kind == LayoutItemKind.App).Id);
}

static void TestManualAppsCleanup()
{
    var layout = new LayoutDocument();
    layout.Items.Add(LayoutItem.ForApp("kept", "Old title"));
    layout.Items.Add(LayoutItem.ForApp("kept", "Duplicate"));
    layout.Items.Add(LayoutItem.ForFolder("Small folder", [
        LayoutItem.ForApp("missing", "Removed"),
        LayoutItem.ForApp("nested", "Nested"),
    ]));

    LayoutEditor.SyncManualApps(layout, new List<LaunchableApp>
    {
        NewApp("kept", "Kept"),
        NewApp("nested", "Nested"),
    });

    Equal(new[] { "kept", "nested" }, LayoutEditor.AppIds(layout).ToArray());
    True(layout.Items.All(item => item.Kind == LayoutItemKind.App));
}

static void TestFolderReorder()
{
    var layout = NewLayout("source", "existing");
    LayoutEditor.MoveApp(layout, "source", "existing", intoFolder: true);
    True(LayoutEditor.MoveApp(layout, "source", "existing"));

    Equal(new[] { "source", "existing" }, layout.Items.Single(item => item.Kind == LayoutItemKind.Folder).Children.Select(child => child.Id).ToArray());
}

static void TestFolderMoveOut()
{
    var layout = NewLayout("first", "second", "third");
    LayoutEditor.MoveApp(layout, "first", "second", intoFolder: true);

    True(LayoutEditor.MoveOutOfFolder(layout, "first"));

    Equal(new[] { "second", "third", "first" }, layout.Items.Select(item => item.Id).ToArray());
}

static void TestFolderRootReorder()
{
    var layout = NewLayout("first", "second", "third");
    LayoutEditor.MoveApp(layout, "first", "second", intoFolder: true);
    var folder = layout.Items.Single(item => item.Kind == LayoutItemKind.Folder);

    True(LayoutEditor.MoveFolder(layout, folder.Id, "third", insertAfter: true));

    Equal(new[] { "third", folder.Id }, layout.Items.Select(item => item.Id).ToArray());
}

static void TestLayoutBackup()
{
    var root = Path.Combine(Path.GetTempPath(), "Launchpad.Core.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var store = new LayoutStore(root);
        store.Save(NewLayout("kept"));
        store.Save(NewLayout("current"));
        File.WriteAllText(Path.Combine(root, "layout.json"), "{");

        var restored = new LayoutStore(root).Load();

        Equal("kept", restored.Items.Single().Id);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void TestManualAppsSync()
{
    var layout = new LayoutDocument();
    layout.Items.Add(LayoutItem.ForApp("second", "Second"));
    layout.Items.Add(LayoutItem.ForApp("first", "First"));

    var manualApps = new List<LaunchableApp>
    {
        NewApp("first", "First"),
        NewApp("second", "Second"),
        NewApp("third", "Third"),
    };

    LayoutEditor.SyncManualApps(layout, manualApps);

    Equal(new[] { "second", "first", "third" }, layout.Items.Select(item => item.Id).ToArray());
    Equal(3, layout.ManualApps.Count);
}

static void TestManualAppsRemoval()
{
    var layout = new LayoutDocument();
    layout.Items.Add(LayoutItem.ForApp("first", "First"));
    layout.Items.Add(LayoutItem.ForApp("second", "Second"));
    layout.Items.Add(LayoutItem.ForApp("removed", "Removed"));

    var manualApps = new List<LaunchableApp>
    {
        NewApp("first", "First"),
        NewApp("second", "Second"),
    };

    LayoutEditor.SyncManualApps(layout, manualApps);

    Equal(new[] { "first", "second" }, layout.Items.Select(item => item.Id).ToArray());
    Equal(2, layout.ManualApps.Count);
}

static void TestManualAppsDedupe()
{
    var layout = new LayoutDocument();

    var manualApps = new List<LaunchableApp>
    {
        NewApp("app1", "App 1"),
        NewApp("app1", "App 1 Duplicate"),
        NewApp("app2", "App 2"),
    };

    LayoutEditor.SyncManualApps(layout, manualApps);

    Equal(2, layout.Items.Count);
    Equal(new[] { "app1", "app2" }, layout.Items.Select(item => item.Id).ToArray());
}

static LaunchableApp NewApp(string id, string name) => new(id, name, "", "", "");

static void TestUserStoreContexts()
{
    var root = Path.Combine(Path.GetTempPath(), "Launchpad.Core.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var profile = Path.Combine(root, "profile");
        var developmentAppData = Path.Combine(root, "development-appdata");
        var loginAppData = Path.Combine(root, "login-appdata");
        var original = NewLayout("second", "first", "third");
        original.ManualApps = [NewApp("first", "中文应用"), NewApp("second", "Second"), NewApp("third", "Third")];
        original.Hotkey = "Ctrl+Space";
        LayoutEditor.MoveApp(original, "first", "second", intoFolder: true);
        original.Items[0].Title = "我的文件夹";
        new LayoutStore(Path.Combine(developmentAppData, "Launchpad")).Save(original);
        new LayoutStore(Path.Combine(loginAppData, "Launchpad")).Save(NewLayout("unrelated"));

        var firstSession = LayoutStore.ForUser(profile, developmentAppData);
        Equal(Path.Combine(profile, ".launchpad"), firstSession.DirectoryPath);
        var afterLogin = LayoutStore.ForUser(profile, loginAppData).Load();
        Equal(3, afterLogin.ManualApps.Count);
        Equal("Ctrl+Space", afterLogin.Hotkey);
        Equal("我的文件夹", afterLogin.Items[0].Title);
        Equal(new[] { "second", "first", "third" }, LayoutEditor.AppIds(afterLogin).ToArray());
        LayoutEditor.MoveOutOfFolder(afterLogin, "first");
        LayoutStore.ForUser(profile, loginAppData).Save(afterLogin);
        Equal(LayoutEditor.AppIds(afterLogin).ToArray(), LayoutEditor.AppIds(LayoutStore.ForUser(profile, developmentAppData).Load()).ToArray());
        Equal(3, firstSession.Load().ManualApps.Count);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void TestUserStoreMigrationBackup()
{
    var root = Path.Combine(Path.GetTempPath(), "Launchpad.Core.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var legacyAppData = Path.Combine(root, "legacy");
        var legacy = new LayoutStore(Path.Combine(legacyAppData, "Launchpad"));
        legacy.Save(NewLayout("saved"));
        legacy.Save(NewLayout("current"));
        File.WriteAllText(Path.Combine(legacy.DirectoryPath, "layout.json"), "{");
        var migrated = LayoutStore.ForUser(Path.Combine(root, "profile"), legacyAppData);
        Equal("saved", migrated.Load().Items.Single().Id);
        File.WriteAllText(Path.Combine(migrated.DirectoryPath, "layout.json"), "{");
        Equal("saved", migrated.Load().Items.Single().Id);
        Equal("{", File.ReadAllText(Path.Combine(legacy.DirectoryPath, "layout.json")));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void TestUserStoreExistingBackup()
{
    var root = Path.Combine(Path.GetTempPath(), "Launchpad.Core.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        var profile = Path.Combine(root, "profile");
        var canonical = new LayoutStore(Path.Combine(profile, ".launchpad"));
        canonical.Save(NewLayout("kept"));
        canonical.Save(NewLayout("latest"));
        File.Delete(Path.Combine(canonical.DirectoryPath, "layout.json"));
        var legacyAppData = Path.Combine(root, "legacy");
        new LayoutStore(Path.Combine(legacyAppData, "Launchpad")).Save(NewLayout("other"));
        Equal("kept", LayoutStore.ForUser(profile, legacyAppData).Load().Items.Single().Id);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static LayoutDocument NewLayout(params string[] ids)
{
    var layout = new LayoutDocument();
    foreach (var id in ids)
    {
        layout.Items.Add(LayoutItem.ForApp(id, id));
    }
    return layout;
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (expected is System.Collections.IEnumerable expectedItems and not string
        && actual is System.Collections.IEnumerable actualItems and not string)
    {
        var expectedList = expectedItems.Cast<object>().ToArray();
        var actualList = actualItems.Cast<object>().ToArray();
        if (!expectedList.SequenceEqual(actualList))
        {
            throw new InvalidOperationException($"Expected [{string.Join(", ", expectedList)}], got [{string.Join(", ", actualList)}].");
        }
        return;
    }

    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
