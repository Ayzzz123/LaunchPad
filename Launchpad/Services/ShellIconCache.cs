using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Launchpad.Core;

namespace Launchpad.Services;

internal sealed class ShellIconCache(string dataDirectory)
{
    private const uint IconOnly = 0x00000004;
    private const uint BiggerIconOkay = 0x00000001;
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private readonly string _cacheDirectory = Path.Combine(dataDirectory, "icons-144");
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> _loads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _workers = new(4);

    public Task<BitmapSource?> GetAsync(LaunchableApp app) =>
        _loads.GetOrAdd(app.Id, _ => new Lazy<Task<BitmapSource?>>(() => LoadAsync(app))).Value;

    private async Task<BitmapSource?> LoadAsync(LaunchableApp app)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(ReadOrExtract(app)); }
                catch (Exception ex) { completion.SetException(ex); }
            }) { IsBackground = true, Name = "Launchpad icon extraction" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _workers.Release();
        }
    }

    private BitmapSource? ReadOrExtract(LaunchableApp app)
    {
        var path = Path.Combine(_cacheDirectory, $"{app.Id.Replace(':', '_')}.png");
        if (File.Exists(path))
        {
            try
            {
                return LoadPng(path);
            }
            catch (IOException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (FormatException)
            {
            }
        }

        var interfaceId = ImageFactoryId;
        var iconSource = app.IconSource is { } shortcut && File.Exists(shortcut) ? shortcut : app.ParsingName;
        var factoryResult = NativeMethods.SHCreateItemFromParsingName(iconSource, IntPtr.Zero, ref interfaceId, out var factory);
        if (factoryResult < 0)
        {
            return null;
        }

        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var result = factory.GetImage(new NativeMethods.IconSize { Width = 144, Height = 144 }, IconOnly | BiggerIconOkay, out bitmapHandle);
            for (var attempt = 0; result == unchecked((int)0x8000000A) && attempt < 3; attempt++)
            {
                if (bitmapHandle != IntPtr.Zero) NativeMethods.DeleteObject(bitmapHandle);
                bitmapHandle = IntPtr.Zero;
                Thread.Sleep(100);
                result = factory.GetImage(new NativeMethods.IconSize { Width = 144, Height = 144 }, IconOnly | BiggerIconOkay, out bitmapHandle);
            }
            BitmapSource? image = result >= 0 && bitmapHandle != IntPtr.Zero
                ? Imaging.CreateBitmapSourceFromHBitmap(bitmapHandle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())
                : ExtractFileIcon(app.ParsingName);
            if (image is null) return null;
            image.Freeze();
            Directory.CreateDirectory(_cacheDirectory);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(temporaryPath))
                {
                    encoder.Save(stream);
                }
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            return image;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
            Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? ExtractFileIcon(string path)
    {
        if (!File.Exists(path)) return null;
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) return null;
        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }
}
