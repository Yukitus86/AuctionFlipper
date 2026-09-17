using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuctionFlipper.Core;

namespace AuctionFlipper.Ui;

/// <summary>
/// Hands out one frozen bitmap per item icon, cut from the embedded sheet.
///
/// The board draws several hundred chips a second across its refreshes, so nothing here may decode
/// or allocate per draw. The sheet is decoded once, each cell is cropped once and cached, and every
/// bitmap is frozen - which is also what makes it safe to hand the same instance to the toast
/// window and to rows being rebuilt off the dispatcher.
/// </summary>
public static class ItemIcons
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, BitmapSource> Cache = new();
    private static BitmapSource? _sheet;
    private static bool _sheetLoaded;

    /// <summary>True once the sheet has been decoded and at least one icon is available.</summary>
    public static bool Available => LoadSheet() is not null;

    /// <summary>
    /// The icon for an item id, or null when the sheet does not cover it. A null is normal - the
    /// server can list an item this build has never heard of - and callers fall back to the
    /// coloured tile rather than showing a gap.
    /// </summary>
    public static BitmapSource? Get(string? itemId)
    {
        if (!ItemIconAtlas.TryGetCell(itemId, out int cell)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(cell, out BitmapSource? cached)) return cached;

            BitmapSource? sheet = LoadSheet();
            if (sheet is null) return null;

            (int x, int y) = ItemIconAtlas.CellOrigin(cell);
            int size = ItemIconAtlas.CellSize;
            if (x + size > sheet.PixelWidth || y + size > sheet.PixelHeight) return null;

            var icon = new CroppedBitmap(sheet, new Int32Rect(x, y, size, size));
            icon.Freeze();
            Cache[cell] = icon;
            return icon;
        }
    }

    private static BitmapSource? LoadSheet()
    {
        lock (Gate)
        {
            if (_sheetLoaded) return _sheet;
            _sheetLoaded = true;

            byte[]? bytes = ItemIconAtlas.ReadSheetBytes();
            if (bytes is null) return null;

            try
            {
                // OnLoad so the stream can be closed here rather than being held for the app's life.
                var decoded = new BitmapImage();
                decoded.BeginInit();
                decoded.StreamSource = new MemoryStream(bytes, writable: false);
                decoded.CacheOption = BitmapCacheOption.OnLoad;
                decoded.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                decoded.EndInit();
                decoded.Freeze();
                _sheet = decoded;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
            {
                _sheet = null;
            }

            return _sheet;
        }
    }
}
