using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Launchpad.UiTests;

internal static class Program
{
    private static readonly List<string> Results = [];
    private static Window _window = null!;
    private static Assembly _product = null!;
    private static Assembly _core = null!;
    private static string _output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--storage-probe")
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetFullPath(args[1]), "Launchpad.Core.dll"));
            var storeType = assembly.GetType("Launchpad.Core.LayoutStore")!;
            var store = storeType.GetMethod("ForUser")!.Invoke(null,
                [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)])!;
            var layout = storeType.GetMethod("Load")!.Invoke(store, null)!;
            var layoutType = layout.GetType();
            var catalog = (IList)layoutType.GetProperty("ManualApps")!.GetValue(layout)!;
            File.WriteAllText(args[2], JsonSerializer.Serialize(new
            {
                Directory = storeType.GetProperty("DirectoryPath")!.GetValue(store),
                Apps = catalog.Count,
                Ids = catalog.Cast<object>().Select(app => (string)app.GetType().GetProperty("Id")!.GetValue(app)!).ToArray(),
                Layout = JsonSerializer.Serialize(layout, layoutType),
            }));
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--probe")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { Args = args, Directory = Environment.CurrentDirectory }));
            return 0;
        }
        if (args.Length != 2) { Console.Error.WriteLine("Usage: Launchpad.UiTests <product-directory> <output-directory>"); return 2; }
        var productPath = Path.GetFullPath(args[0]);
        _output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(_output);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var file = Path.Combine(productPath, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        _core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(productPath, "Launchpad.Core.dll"));
        _product = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(productPath, "Launchpad.dll"));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = 1;
        application.Startup += async (_, _) =>
        {
            try { await Run(); exit = 0; }
            catch (Exception ex) { Results.Add("FAIL " + ex); Console.Error.WriteLine(ex); }
            finally
            {
                File.WriteAllLines(Path.Combine(_output, "results.txt"), Results);
                if (_window is not null)
                {
                    ((IDisposable)_window).Dispose();
                    _window.Close();
                }
                application.Shutdown();
            }
        };
        application.Run();
        return exit;
    }

    private static async Task Run()
    {
        var data = Path.Combine(_output, "isolated-data", Guid.NewGuid().ToString("N"));
        var store = Activator.CreateInstance(_core.GetType("Launchpad.Core.LayoutStore")!, data)!;
        _window = (Window)Activator.CreateInstance(_product.GetType("Launchpad.MainWindow")!, store, data)!;
        // Keep an automated fixture alive while other application windows own focus.
        _window.Deactivated -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), _window,
            _window.GetType().GetMethod("Window_Deactivated", BindingFlags.NonPublic | BindingFlags.Instance)!);
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        Call("Attach", handle);
        Call("ShowLauncher");
        await Settle();
        Check(!_window.AllowsTransparency && _window.IsVisible, "published WPF window opens without layered transparency");
        Check((int)Get("_columns")! == 7 && (int)Get("_rows")! == 4, "default window fits 7 by 4 icons");
        Check(Element<Border>("GlassSurface").Background is SolidColorBrush tint
            && tint.Color.R < 16 && tint.Color.G < 16 && tint.Color.B < 16 && tint.Color.A < 64,
            "dark glass keeps a thin translucent black tint over the native backdrop" );
        await Shot("01-empty");

        var importer = _product.GetType("Launchpad.Services.AppImportService")!;
        object Import(string path) => importer.GetMethod("CreateAppFromPath")!.Invoke(null, [path])!;
        var appType = _core.GetType("Launchpad.Core.LaunchableApp")!;
        var sampleDirectory = Path.Combine(data, "示例文件夹");
        Directory.CreateDirectory(sampleDirectory);
        var textFile = Path.Combine(sampleDirectory, "测试文档.txt");
        var otherFile = Path.Combine(sampleDirectory, "自定义格式.launchpad-test");
        File.WriteAllText(textFile, "Launchpad 文档测试");
        File.WriteAllText(otherFile, "unknown extension");
        var diskFolder = Import(sampleDirectory);
        Check(diskFolder is not null && (string)appType.GetProperty("ParsingName")!.GetValue(diskFolder)! == sampleDirectory,
            "manual import accepts a disk folder without scanning its contents");
        var textEntry = Import(textFile);
        var otherEntry = Import(otherFile);
        Check(textEntry is not null && otherEntry is not null
            && (string)appType.GetProperty("Name")!.GetValue(textEntry)! == "测试文档.txt",
            "manual import accepts documents and unknown file types with their extensions");
        var listType = typeof(List<>).MakeGenericType(appType);
        var apps = (IList)Activator.CreateInstance(listType)!;
        var fixtures = new (string File, string Name)[]
        {
            (@"C:\Windows\explorer.exe", "文件资源管理器"),
            (@"C:\Windows\System32\notepad.exe", "记事本"),
            (@"C:\Windows\System32\calc.exe", "计算器"),
            (@"C:\Windows\System32\mspaint.exe", "画图"),
            (@"C:\Windows\System32\SnippingTool.exe", "截图工具"),
            (@"C:\Windows\System32\control.exe", "控制面板"),
            (@"C:\Windows\System32\Taskmgr.exe", "任务管理器"),
            (@"C:\Windows\System32\mstsc.exe", "远程桌面"),
            (@"C:\Windows\System32\msinfo32.exe", "系统信息"),
            (@"C:\Windows\System32\charmap.exe", "字符映射表"),
            (@"C:\Windows\System32\mmsys.cpl", "声音"),
            (@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "Microsoft Edge"),
        };
        foreach (var fixture in fixtures.Where(f => File.Exists(f.File) && Path.GetExtension(f.File) == ".exe"))
        {
            var app = Import(fixture.File);
            appType.GetProperty("Name")!.SetValue(app, fixture.Name);
            apps.Add(app);
        }
        Check(apps.Count >= 5, "fixture contains real Windows application targets");
        apps.Add(apps[0]);
        Call("CommitApps", apps);
        var layout = Get("_layout")!;
        var manualApps = (IList)layout.GetType().GetProperty("ManualApps")!.GetValue(layout)!;
        Check(manualApps.Count == apps.Count - 1, "manual add deduplicates the saved catalog");
        var iconCache = Get("_icons")!;
        var iconLoads = manualApps.Cast<object>().Select(app => (Task)iconCache.GetType().GetMethod("GetAsync")!.Invoke(iconCache, [app])!).ToArray();
        await Task.WhenAll(iconLoads).WaitAsync(TimeSpan.FromSeconds(20));
        var iconCount = iconLoads.Count(task => task.GetType().GetProperty("Result")!.GetValue(task) is not null);
        for (var i = 0; i < iconLoads.Length; i++)
            if (iconLoads[i].GetType().GetProperty("Result")!.GetValue(iconLoads[i]) is null)
                Console.WriteLine("Missing icon: " + appType.GetProperty("ParsingName")!.GetValue(manualApps[i]));
        Check(iconCount == manualApps.Count, $"all {manualApps.Count} real application icons finish loading ({iconCount} available)");
        var hoverTile = Element<UniformGrid>("TilesGrid").Children.OfType<Button>().First();
        hoverTile.ApplyTemplate();
        var hoverSurface = (Border)hoverTile.Template.FindName("TileHoverSurface", hoverTile);
        void TilePointerEvent(RoutedEvent pointerEvent) => hoverTile.RaiseEvent(
            new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = pointerEvent });
        TilePointerEvent(UIElement.MouseEnterEvent);
        await Settle();
        Check(hoverSurface.Opacity > .99 && hoverTile.Cursor == System.Windows.Input.Cursors.Hand,
            "tile hover fades in while preserving the hand cursor");
        Check(hoverSurface.ActualHeight < hoverTile.ActualHeight && hoverSurface.CornerRadius.TopLeft == 14,
            "hover card hugs the icon and label instead of filling the grid cell");
        Check(hoverSurface.Background is SolidColorBrush tileTint && tileTint.Color.A < 32,
            "dark hover surface uses a restrained translucent gray fill");
        TilePointerEvent(UIElement.MouseLeaveEvent);
        await Settle();
        Check(hoverSurface.Opacity < .01, "tile hover fades out completely after pointer leave");
        Check((double)Get("_iconSize")! == 48, "icons match the current 48-pixel desktop setting");
        Check(DwmGetWindowAttribute(handle, 38, out var backdrop, sizeof(int)) == 0 && backdrop == 1,
            "default opaque Desktop Acrylic is disabled before applying custom blur");
        Check((bool)_product.GetType("Launchpad.Services.WindowBackdrop")!.GetMethod("Apply")!.Invoke(null, [_window, true])!,
            "native acrylic composition request succeeds on this Windows installation");
        Check((bool)Get("_isDark")! && DwmGetWindowAttribute(handle, 20, out var darkMode, sizeof(int)) == 0 && darkMode == 1, "current Windows dark theme applies to content and native Acrylic");
        Check(((long)GetWindowLongPtr(handle, -20) & 0x80000) == 0, "native window does not use WS_EX_LAYERED");
        await Shot("02-home");

        Element<Button>("EditButton").Focus();
        Element<Border>("SearchPanel").RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Check(Element<TextBox>("SearchBox").IsKeyboardFocused, "clicking search padding focuses the input");
        Element<TextBox>("SearchBox").Text = "记事";
        var blankClick = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = Element<Grid>("GridArea") };
        Call("Root_MouseLeftButtonDown", Element<Grid>("Root"), blankClick);
        Check(_window.IsVisible && !Element<TextBox>("SearchBox").IsKeyboardFocused,
            "clicking blank space removes the search caret without hiding the launcher");
        Check(Element<TextBox>("SearchBox").Text == "记事", "defocusing search preserves the query");
        Element<TextBox>("SearchBox").Clear();
        Element<TextBox>("SearchBox").Focus();
        _window.GetType().GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, 4);
        _window.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, 1);
        Call("RenderTiles", false);
        var pageDots = Element<StackPanel>("PageDots");
        Check(!pageDots.IsHitTestVisible && pageDots.Children.Count == 3 && !pageDots.Children.OfType<Button>().Any(),
            "page dots display status without mouse or button interaction");
        void ScrollPage(int delta)
        {
            var wheelEvent = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, delta)
                { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            Call("Root_PreviewMouseWheel", _window, wheelEvent);
            Check(wheelEvent.Handled, "mouse wheel is handled inside the launcher");
        }
        ScrollPage(-120);
        Check((int)Get("_page")! == 1, "mouse wheel advances while search input is focused");
        ScrollPage(120);
        Check((int)Get("_page")! == 0, "mouse wheel returns to the previous page");
        ScrollPage(120);
        Check((int)Get("_page")! == 0, "mouse wheel stops at the first page");
        var arrow = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(_window)!, 0, System.Windows.Input.Key.Left);
        Call("Window_PreviewKeyDown", _window, arrow);
        Check(!arrow.Handled, "search retains arrow keys for positioning the text caret");
        Call("UpdateGridSize");

        Element<TextBox>("SearchBox").Text = "记事";
        await Settle();
        Check(Element<UniformGrid>("TilesGrid").Children.Count == 1, "Chinese search filters real application tiles");
        await Shot("03-search");
        Element<TextBox>("SearchBox").Text = "__no_result__";
        await Settle();
        Check(Element<StackPanel>("EmptyStatePanel").Visibility == Visibility.Visible, "no results displays an empty state");
        Element<TextBox>("SearchBox").Clear();

        // Exercise actual WPF edit controls and folder model together.
        Call("Edit_Click", _window, new RoutedEventArgs());
        var state = Get("_state")!;
        Check((bool)state.GetType().GetProperty("IsEditing")!.GetValue(state)!, "edit action enables organization");
        await Shot("04-edit");
        var tiles = Element<UniformGrid>("TilesGrid");
        var first = (Button)tiles.Children[0];
        var removeButton = Descendants<Button>(first).Single();
        Check(removeButton.Content is Border { Width: 10, Height: 2 }, "remove badge uses a vector minus instead of a font glyph");
        var minus = (Border)removeButton.Content;
        var minusOrigin = minus.TranslatePoint(new Point(), removeButton);
        Check(Math.Abs(minusOrigin.X + minus.ActualWidth / 2 - removeButton.ActualWidth / 2) < .5
            && Math.Abs(minusOrigin.Y + minus.ActualHeight / 2 - removeButton.ActualHeight / 2) < .5,
            "minus is centered in the circular remove badge");
        var beforeBadgeClick = manualApps.Count;
        removeButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Check(Element<Grid>("ActionOverlay").Visibility == Visibility.Visible && Element<TextBlock>("ActionTitle").Text == "从启动台移除？",
            "remove badge opens the confirmation panel");
        Call("CancelAction_Click", _window, new RoutedEventArgs());
        Check(manualApps.Count == beforeBadgeClick, "cancelling badge removal preserves the application");
        _window.GetType().GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, 4);
        _window.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, 1);
        Call("ChangePage", 1);
        Check((int)Get("_page")! == 1, "drag fixture starts on the second page");
        Check(((DispatcherTimer)Get("_edgeHoverTimer")!).Interval <= TimeSpan.FromMilliseconds(450),
            "cross-page drag uses a shorter edge dwell");
        var draggedTile = ((IList)Get("_visibleTiles")!)[0]!;
        var draggedItem = draggedTile.GetType().GetProperty("Item")!.GetValue(draggedTile)!;
        var draggedId = (string)draggedItem.GetType().GetProperty("Id")!.GetValue(draggedItem)!;
        _window.GetType().GetField("_activeDragItemId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, draggedId);
        Call("ShowDragPreview", draggedTile, new Point(70, 300));
        Call("UpdateDragEdge", new Point(70, 300));
        Check(Element<Border>("DragPreviousPage").Visibility == Visibility.Visible, "left edge shows the previous-page drag target");
        await Shot("13-drag-edge");
        Call("ResetDragEdgeIfOutside", new Point(72, 310));
        Check(((DispatcherTimer)Get("_edgeHoverTimer")!).IsEnabled, "moving between child elements does not cancel edge dwell");
        await Task.Delay(550);
        await Settle();
        Check((int)Get("_page")! == 0, "edge dwell changes from the second page to the first");
        Check(Element<Border>("DragPreview").Visibility == Visibility.Visible, "drag preview remains visible after changing page");
        var root = Element<Grid>("Root");
        var grid = Element<UniformGrid>("TilesGrid");
        var dropPoint = grid.TranslatePoint(new Point(2, 2), root);
        var payloadType = _window.GetType().GetNestedType("DragPayload", BindingFlags.NonPublic)!;
        var payload = Activator.CreateInstance(payloadType, draggedId, Enum.Parse(_core.GetType("Launchpad.Core.LayoutItemKind")!, "App"))!;
        var dataObject = new DataObject(payloadType, payload);
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [dataObject, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, root, dropPoint], null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        drop.Source = root;
        Call("Root_Drop", root, drop);
        var rootItems = (IList)layout.GetType().GetProperty("Items")!.GetValue(layout)!;
        Check((string)rootItems[0]!.GetType().GetProperty("Id")!.GetValue(rootItems[0])! == draggedId,
            "dropping a second-page icon inserts it at the first-page position");
        var savedAfterDrop = store.GetType().GetMethod("Load")!.Invoke(store, null)!;
        var savedDropItems = (IList)savedAfterDrop.GetType().GetProperty("Items")!.GetValue(savedAfterDrop)!;
        Check(savedDropItems.Count == manualApps.Count
            && (string)savedDropItems[0]!.GetType().GetProperty("Id")!.GetValue(savedDropItems[0])! == draggedId,
            "cross-page drop persists order without losing apps or creating a folder");
        await Settle();
        var targetButton = (Button)grid.Children[0];
        var iconLayer = (Grid)((StackPanel)targetButton.Content).Children[0];
        var iconCenter = iconLayer.TranslatePoint(new Point(iconLayer.ActualWidth / 2, iconLayer.ActualHeight / 2), targetButton);
        Check((bool)Call("IsFolderDropZone", targetButton, iconCenter)!
            && !(bool)Call("IsFolderDropZone", targetButton, new Point(targetButton.ActualWidth / 2, targetButton.ActualHeight - 4))!,
            "folder grouping only targets the icon center, not the label or tile gaps");
        Call("UpdateDragEdge", new Point(70, 300));
        Check(!((DispatcherTimer)Get("_edgeHoverTimer")!).IsEnabled, "first page does not arm a previous-page timer");
        Call("UpdateDragEdge", new Point(_window.ActualWidth - 70, 300));
        Check(((DispatcherTimer)Get("_edgeHoverTimer")!).IsEnabled, "right edge arms next-page navigation");
        Call("ResetDragEdgeIfOutside", new Point(-1, 300));
        Check(!((DispatcherTimer)Get("_edgeHoverTimer")!).IsEnabled, "leaving the window cancels edge navigation");
        Call("ResetDragFeedback");
        Check(Element<Border>("DragPreview").Visibility == Visibility.Collapsed
            && Element<Border>("DragPreviousPage").Visibility == Visibility.Collapsed
            && Element<Border>("DragNextPage").Visibility == Visibility.Collapsed,
            "ending drag clears its preview and page hints");
        Call("UpdateGridSize");
        first = (Button)tiles.Children[0];
        var countBefore = ((IList)Get("_visibleTiles")!).Count;
        Call("Tile_Click", first, new RoutedEventArgs());
        await Settle();
        Check(_window.IsVisible && countBefore == ((IList)Get("_visibleTiles")!).Count, "clicking an app while editing keeps the launcher open");
        var orderedIds = rootItems.Cast<object>().Select(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)!).ToArray();
        var finishClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = Element<Grid>("GridArea") };
        Call("Root_MouseLeftButtonDown", root, finishClick);
        Check(!(bool)state.GetType().GetProperty("IsEditing")!.GetValue(state)! && _window.IsVisible && finishClick.Handled,
            "clicking blank space finishes organization without hiding the launcher");
        Check(orderedIds.SequenceEqual(rootItems.Cast<object>().Select(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)!)),
            "finishing organization preserves the current order");
        var editButton = Element<Button>("EditButton");
        Call("Edit_Click", editButton, new RoutedEventArgs());
        Call("Edit_Click", editButton, new RoutedEventArgs());
        editButton.Focus();
        editButton.UpdateLayout();
        Check(!(bool)state.GetType().GetProperty("IsEditing")!.GetValue(state)!
            && editButton.IsKeyboardFocused
            && ((Border)editButton.Template.FindName("Surface", editButton)).BorderBrush is SolidColorBrush editFocusBorder
            && editButton.BorderBrush is SolidColorBrush editNormalBorder && editFocusBorder.Color == editNormalBorder.Color,
            "organization button keeps a neutral focus border after a second click completes editing");
        var id1 = (string)appType.GetProperty("Id")!.GetValue(manualApps[0])!;
        var id2 = (string)appType.GetProperty("Id")!.GetValue(manualApps[1])!;
        _core.GetType("Launchpad.Core.LayoutEditor")!.GetMethod("MoveApp")!.Invoke(null, [layout, id1, id2, true, false]);
        Call("RenderTiles", false);
        var folderButton = Element<UniformGrid>("TilesGrid").Children.OfType<Button>()
            .First(b => b.Tag!.GetType().GetProperty("Item")!.GetValue(b.Tag)!.GetType().GetProperty("Kind")!.GetValue(b.Tag.GetType().GetProperty("Item")!.GetValue(b.Tag))!.ToString() == "Folder");
        Call("Tile_Click", folderButton, new RoutedEventArgs());
        Element<TextBox>("FolderNameInput").Text = "常用工具";
        await Shot("05-folder");
        Check(Element<Grid>("FolderOverlay").Visibility == Visibility.Visible, "folder opens inside the same window");
        Call("Edit_Click", _window, new RoutedEventArgs());
        var folderBlank = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = Element<UniformGrid>("FolderTilesGrid") };
        Call("Root_MouseLeftButtonDown", root, folderBlank);
        Check(!(bool)state.GetType().GetProperty("IsEditing")!.GetValue(state)!
            && Element<Grid>("FolderOverlay").Visibility == Visibility.Visible,
            "blank space finishes folder organization while keeping the folder open");
        Call("Edit_Click", _window, new RoutedEventArgs());
        Call("FolderBackdrop_Click", _window, new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Check(!(bool)state.GetType().GetProperty("IsEditing")!.GetValue(state)!
            && Element<Grid>("FolderOverlay").Visibility == Visibility.Visible,
            "folder backdrop also completes editing before closing the folder");
        Call("GoBack");
        Check(Element<Grid>("FolderOverlay").Visibility == Visibility.Collapsed, "escape closes folder before launcher");

        Call("ShowAppManagement");
        await Shot("06-settings-apps");
        Check(Element<Grid>("SettingsPage").Visibility == Visibility.Visible && Element<Grid>("HomePage").Visibility == Visibility.Collapsed, "app management is an in-window page");
        var appScroller = (ScrollViewer)Element<StackPanel>("ManagedApps").Parent;
        var scrollBar = Descendants<ScrollBar>(appScroller).Single(bar => bar.Orientation == Orientation.Vertical);
        Check(scrollBar.ActualWidth <= 12, "settings scroll bar keeps a narrow track");
        var thumb = Descendants<Thumb>(scrollBar).Single();
        Check(thumb.Template.FindName("ThumbSurface", thumb) is Border { ActualWidth: <= 4 },
            "scroll thumb displays as a slim rounded line");
        Check(appScroller.ScrollableHeight > 0, "app list overflows the settings viewport");
        appScroller.ScrollToEnd();
        await Settle();
        Check(Math.Abs(appScroller.VerticalOffset - appScroller.ScrollableHeight) < 1
            && Math.Abs(scrollBar.Value - appScroller.VerticalOffset) < 1, "scroll bar tracks the last application");
        appScroller.ScrollToTop();
        await Settle();
        Check(appScroller.VerticalOffset == 0 && scrollBar.Value == 0, "scroll bar returns to the first application");
        appScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, -120)
            { RoutedEvent = UIElement.MouseWheelEvent });
        await Settle();
        Check(appScroller.VerticalOffset > 0, "mouse wheel scrolls the settings app list");
        appScroller.ScrollToTop();
        await Settle();
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, 40) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, 40, false) { RoutedEvent = Thumb.DragCompletedEvent });
        await Settle();
        Check(appScroller.VerticalOffset > 0, "thumb drag events scroll the settings app list");
        appScroller.ScrollToTop();
        await Settle();
        var managedRow = (Grid)((Border)Element<StackPanel>("ManagedApps").Children[0]).Child;
        var moreButton = managedRow.Children.OfType<Button>().Single();
        moreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        var appMenu = moreButton.ContextMenu!;
        Check(appMenu.IsOpen && appMenu.Template.FindName("MenuSurface", appMenu) is Border,
            "app menu uses the glass template without the native icon gutter");
        var menuPosition = _window.PointFromScreen(appMenu.PointToScreen(new Point()));
        Check(menuPosition.X >= 0 && menuPosition.Y >= 0
            && menuPosition.X + appMenu.ActualWidth <= _window.ActualWidth + 1
            && menuPosition.Y + appMenu.ActualHeight <= _window.ActualHeight + 1,
            "app management menu opens within the launcher window");
        var menuItem = (MenuItem)appMenu.Items[0];
        Check(menuItem.Template.FindName("ItemSurface", menuItem) is Border,
            "menu items use a gutter-free rounded template");
        var menuBitmap = new RenderTargetBitmap((int)Math.Ceiling(appMenu.ActualWidth), (int)Math.Ceiling(appMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        menuBitmap.Render(appMenu);
        var menuEncoder = new PngBitmapEncoder();
        menuEncoder.Frames.Add(BitmapFrame.Create(menuBitmap));
        using (var menuFile = File.Create(Path.Combine(_output, "12-settings-menu.png"))) menuEncoder.Save(menuFile);
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        appMenu.IsOpen = false;
        Check(Element<Grid>("ActionOverlay").Visibility == Visibility.Visible, "menu rename opens the in-window action panel");
        Call("CancelAction_Click", _window, new RoutedEventArgs());
        Check(_window.FindName("FooterHint") is null, "bottom right shortcut hint is removed");
        Call("ShowSettings");
        await Shot("07-settings-hotkey");
        // Register a second HWND's hotkey and verify conflict rollback.
        var blocker = new Window { Width = 1, Height = 1, ShowInTaskbar = false };
        var blockerHandle = new WindowInteropHelper(blocker).EnsureHandle();
        try
        {
            if (RegisterHotKey(blockerHandle, 111, 3, 0x79))
            {
                var before = (string)layout.GetType().GetProperty("Hotkey")!.GetValue(layout)!;
                var error = Call("SaveHotkey", "Ctrl+Alt+F10") as string;
                Check(!string.IsNullOrEmpty(error) && (string)layout.GetType().GetProperty("Hotkey")!.GetValue(layout)! == before, "occupied hotkey reports failure and preserves old binding");
            }
            else Results.Add("LIMIT test conflict hotkey was already occupied");
        }
        finally { UnregisterHotKey(blockerHandle, 111); blocker.Close(); }
        Call("BackHome_Click", _window, new RoutedEventArgs());
        Call("SaveLayout");
        var reloaded = store.GetType().GetMethod("Load")!.Invoke(store, null)!;
        var reloadedItems = (IList)reloaded.GetType().GetProperty("Items")!.GetValue(reloaded)!;
        Check(reloadedItems.Cast<object>().Any(i => (string)i.GetType().GetProperty("Title")!.GetValue(i)! == "常用工具"), "folder name survives saving and reloading");
        var visible = (IList)Get("_visibleTiles")!;
        var editableTile = visible.Cast<object>().First(tile => tile.GetType().GetProperty("App")!.GetValue(tile) is not null);
        var editableItem = editableTile.GetType().GetProperty("Item")!.GetValue(editableTile)!;
        Call("BeginRename", editableItem);
        Element<TextBox>("ActionInput").Text = "重命名验证";
        Call("ConfirmAction_Click", _window, new RoutedEventArgs());
        Check(((IList)layout.GetType().GetProperty("ManualApps")!.GetValue(layout)!).Cast<object>().Any(app => (string)appType.GetProperty("Name")!.GetValue(app)! == "重命名验证"), "in-window rename updates the manual catalog");
        var renamedTile = ((IList)Get("_visibleTiles")!).Cast<object>().First(tile => (string)tile.GetType().GetProperty("Item")!.GetValue(tile)!.GetType().GetProperty("Title")!.GetValue(tile.GetType().GetProperty("Item")!.GetValue(tile))! == "重命名验证");
        var renamedItem = renamedTile.GetType().GetProperty("Item")!.GetValue(renamedTile)!;
        var beforeRemoval = ((IList)layout.GetType().GetProperty("ManualApps")!.GetValue(layout)!).Count;
        Call("BeginRemove", renamedItem);
        Check(Element<TextBlock>("ActionDescription").Text.Contains("不会卸载"), "removal dialog explains that application files are preserved");
        Call("ConfirmAction_Click", _window, new RoutedEventArgs());
        Check(((IList)layout.GetType().GetProperty("ManualApps")!.GetValue(layout)!).Count == beforeRemoval - 1, "in-window removal updates tiles and manual catalog together");

        // Real Shell link import and ShellExecuteEx launch, with quoted parameters and a working directory containing spaces.
        var probeDir = Path.Combine(_output, "probe working directory");
        Directory.CreateDirectory(probeDir);
        var probeResult = Path.Combine(probeDir, "launch result.json");
        if (File.Exists(probeResult)) File.Delete(probeResult);
        var linkPath = Path.Combine(_output, "参数测试.lnk");
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic shortcut = ((dynamic)shell).CreateShortcut(linkPath);
        shortcut.TargetPath = Environment.ProcessPath!;
        shortcut.Arguments = $"--probe \"{probeResult}\" \"中文 argument\"";
        shortcut.WorkingDirectory = probeDir;
        shortcut.Save();
        Marshal.ReleaseComObject(shortcut);
        Marshal.ReleaseComObject(shell);
        var probeApp = Import(linkPath);
        Check((string)appType.GetProperty("WorkingDirectory")!.GetValue(probeApp)! == probeDir, "Shell link import preserves the working directory");
        var launcher = Activator.CreateInstance(_product.GetType("Launchpad.Services.AppLauncher")!)!;
        await (Task)launcher.GetType().GetMethod("LaunchAsync")!.Invoke(launcher, [probeApp])!;
        for (var i = 0; i < 30 && !File.Exists(probeResult); i++) await Task.Delay(100);
        Check(File.Exists(probeResult), "published ShellExecuteEx implementation starts the probe application");
        var probeJson = File.ReadAllText(probeResult);
        using (var json = JsonDocument.Parse(probeJson))
        {
            Check(json.RootElement.GetProperty("Args")[2].GetString() == "中文 argument", "Shell launch preserves quoted Chinese arguments");
            Check(json.RootElement.GetProperty("Directory").GetString() == probeDir, "Shell launch preserves the requested working directory");
        }
        var missing = Activator.CreateInstance(appType, "missing", "缺失应用", Path.Combine(_output, "does-not-exist.exe"), "", "")!;
        var failed = false;
        try { await (Task)launcher.GetType().GetMethod("LaunchAsync")!.Invoke(launcher, [missing])!; }
        catch (System.ComponentModel.Win32Exception) { failed = true; }
        Check(failed && _window.IsVisible, "missing executable reports failure without closing the launcher");
        Element<TextBox>("SearchBox").Text = "记事";
        _window.GetType().GetField("_isImeComposing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, true);
        var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window)!, 0, System.Windows.Input.Key.Enter);
        Call("Window_PreviewKeyDown", _window, enter);
        Check(!enter.Handled && _window.IsVisible, "IME composition guard does not submit Enter as an app launch");
        _window.GetType().GetField("_isImeComposing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, false);
        Element<TextBox>("SearchBox").Clear();

        var imageFile = Path.Combine(sampleDirectory, "示例图片.png");
        File.Copy(Path.Combine(_output, "03-search.png"), imageFile, true);
        var imageEntry = Import(imageFile);
        var diskFolderAgain = Import(sampleDirectory + Path.DirectorySeparatorChar);
        Check((string)appType.GetProperty("Id")!.GetValue(diskFolder)! == (string)appType.GetProperty("Id")!.GetValue(diskFolderAgain)!,
            "folder import deduplicates trailing directory separators");
        Check(importer.GetMethod("CreateAppFromPath")!.Invoke(null, [Path.Combine(data, "missing.txt")]) is null,
            "import rejects missing files");
        var folderLink = Path.Combine(data, "文件夹快捷方式.lnk");
        var folderShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic folderShortcut = ((dynamic)folderShell).CreateShortcut(folderLink);
        folderShortcut.TargetPath = sampleDirectory;
        folderShortcut.WorkingDirectory = data;
        folderShortcut.Save();
        Marshal.ReleaseComObject(folderShortcut);
        Marshal.ReleaseComObject(folderShell);
        var folderLinkEntry = Import(folderLink);
        Check((string)appType.GetProperty("ParsingName")!.GetValue(folderLinkEntry)! == sampleDirectory,
            "shortcut import accepts directory targets");
        var mixed = (IList)Activator.CreateInstance(listType)!;
        foreach (var entry in new[] { diskFolder, textEntry, otherEntry, imageEntry, diskFolderAgain }) mixed.Add(entry);
        Call("CommitApps", mixed);
        Check(((IList)layout.GetType().GetProperty("ManualApps")!.GetValue(layout)!).Count == 4,
            "mixed files and folders share catalog deduplication");
        var fileIconLoads = new[] { diskFolder, textEntry, imageEntry }.Select(entry => (Task)iconCache.GetType().GetMethod("GetAsync")!.Invoke(iconCache, [entry])!).ToArray();
        await Task.WhenAll(fileIconLoads).WaitAsync(TimeSpan.FromSeconds(20));
        Check(fileIconLoads.All(task => task.GetType().GetProperty("Result")!.GetValue(task) is not null),
            "disk folders, documents and images use real Shell icons");
        Element<TextBox>("SearchBox").Text = "测试文档";
        await Settle();
        Check(Element<UniformGrid>("TilesGrid").Children.Count == 1, "search finds added files by name");
        Element<TextBox>("SearchBox").Clear();
        var mixedReload = store.GetType().GetMethod("Load")!.Invoke(store, null)!;
        var mixedItems = (IList)mixedReload.GetType().GetProperty("Items")!.GetValue(mixedReload)!;
        Check(mixedItems.Count == 4 && mixedItems.Cast<object>().All(item => item.GetType().GetProperty("Kind")!.GetValue(item)!.ToString() == "App"),
            "disk folder entries persist separately from Launchpad grouping folders");
        await Shot("14-files-and-folders");
        var addButton = Element<Button>("AddButton");
        Call("AddApps_Click", addButton, new RoutedEventArgs());
        await Settle();
        Check(addButton.ContextMenu is { IsOpen: true } addMenu && addMenu.Items.Count == 2
            && ((MenuItem)addMenu.Items[0]).Header.ToString()!.Contains("文件")
            && ((MenuItem)addMenu.Items[1]).Header.ToString()!.Contains("文件夹"),
            "add menu offers files and disk folders through the shared flow");
        Check(addButton.ContextMenu!.Items.Cast<MenuItem>().All(item => !item.Header.ToString()!.EndsWith("…") && !item.Header.ToString()!.EndsWith("...")),
            "add menu labels have no trailing ellipsis");
        var timedMenu = addButton.ContextMenu!;
        var firstAddItem = (MenuItem)timedMenu.Items[0];
        firstAddItem.Focus();
        timedMenu.UpdateLayout();
        Check(!firstAddItem.IsMouseOver
            && ((Border)firstAddItem.Template.FindName("ItemSurface", firstAddItem)).Background is SolidColorBrush hoverBrush
            && hoverBrush.Color.A == 0,
            "mouse-opened menu does not retain a hover fill on a focused item outside the pointer");
        void MenuPointerEvent(RoutedEvent pointerEvent) => timedMenu.RaiseEvent(
            new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = pointerEvent });
        firstAddItem.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(firstAddItem)!, Environment.TickCount, System.Windows.Input.Key.Down)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        timedMenu.UpdateLayout();
        Check(((Border)firstAddItem.Template.FindName("ItemSurface", firstAddItem)).Background is SolidColorBrush keyboardBrush
            && keyboardBrush.Color.A > 0, "keyboard navigation retains a visible menu selection");
        MenuPointerEvent(UIElement.MouseLeaveEvent);
        timedMenu.UpdateLayout();
        Check(((Border)firstAddItem.Template.FindName("ItemSurface", firstAddItem)).Background is SolidColorBrush leaveBrush
            && leaveBrush.Color.A == 0, "leaving the menu clears its previous selection fill immediately");
        Call("AddApps_Click", addButton, new RoutedEventArgs());
        await Settle();
        Check(!timedMenu.IsOpen && ReferenceEquals(addButton.ContextMenu, timedMenu)
            && !(bool)Get("_contextMenuOpen")!, "clicking plus again closes the existing add menu");
        addButton.Focus();
        addButton.UpdateLayout();
        Check(addButton.IsKeyboardFocused
            && ((Border)addButton.Template.FindName("Surface", addButton)).BorderBrush is SolidColorBrush addFocusBorder
            && addButton.BorderBrush is SolidColorBrush addNormalBorder && addFocusBorder.Color == addNormalBorder.Color,
            "plus button stays keyboard-focusable with a neutral border after closing its menu");
        Call("AddApps_Click", addButton, new RoutedEventArgs());
        await Settle();
        Check(timedMenu.IsOpen && ReferenceEquals(addButton.ContextMenu, timedMenu),
            "the next plus click reopens the same menu");
        MenuPointerEvent(UIElement.MouseLeaveEvent);
        await Task.Delay(2200);
        Check(timedMenu.IsOpen, "leaving the add menu for more than two seconds no longer dismisses it");
        timedMenu.IsOpen = false;
        await Settle();
        Check(!(bool)Get("_contextMenuOpen")!, "closing the add menu clears the menu-open guard");

        var desktopPreview = Path.Combine(Path.GetDirectoryName(_output)!, "desktop-apps-preview.json");
        if (File.Exists(desktopPreview))
        {
            var desktopLayout = JsonSerializer.Deserialize(File.ReadAllText(desktopPreview), layout.GetType())!;
            var desktopApps = desktopLayout.GetType().GetProperty("ManualApps")!.GetValue(desktopLayout)!;
            Call("CommitApps", desktopApps);
            var desktopLoads = ((IList)desktopApps).Cast<object>().Select(app => (Task)iconCache.GetType().GetMethod("GetAsync")!.Invoke(iconCache, [app])!).ToArray();
            await Task.WhenAll(desktopLoads).WaitAsync(TimeSpan.FromSeconds(30));
            var desktopIconCount = desktopLoads.Count(task => task.GetType().GetProperty("Result")!.GetValue(task) is not null);
            Check(desktopIconCount == desktopLoads.Length, $"all {desktopLoads.Length} desktop application icons load");
            await Shot("08-desktop-home");
            Call("ChangePage", 1);
            await Shot("09-desktop-page2");
            Call("ChangePage", 100);
            Check((int)Get("_page")! == 1, "page navigation clamps to the last desktop app page");
            Call("ChangePage", 0);
            Call("ShowAppManagement");
            await Shot("10-desktop-settings");
            Call("BackHome_Click", _window, new RoutedEventArgs());
            _window.Width = 680;
            _window.Height = 500;
            await Settle();
            Check((int)Get("_columns")! < 7 && (int)Get("_rows")! < 4, "live WPF layout reduces grid capacity for a smaller window");
            await Shot("11-small-window");
        }
        Results.Add("LIMIT screenshots render the live WPF visual tree; DWM Acrylic is not included in RenderTargetBitmap");
        Results.Add("LIMIT physical mouse dragging, interactive IME composition, multiple monitors and native file picker were not automated");
    }

    private static T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static object? Get(string name) => _window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window);
    private static object? Call(string name, params object?[] args)
    {
        try { return _window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(_window, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException!; }
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Results.Add("PASS " + description);
        Console.WriteLine("PASS " + description);
    }
    private static async Task Settle()
    {
        await Task.Delay(300);
        await _window.Dispatcher.InvokeAsync(() => _window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }
    private static async Task Shot(string name)
    {
        await Settle();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_window.ActualWidth), (int)Math.Ceiling(_window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Native desktop material is outside the WPF visual tree. A neutral backing makes exported UI renders readable.
        var backing = new DrawingVisual();
        using (var drawing = backing.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush((bool)Get("_isDark")! ? Color.FromRgb(22, 22, 25) : Color.FromRgb(234, 239, 246)), null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Render(backing);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_output, name + ".png"));
        encoder.Save(stream);
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
}
