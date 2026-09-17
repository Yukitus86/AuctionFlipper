using System.Reflection;
using System.Text;

namespace AuctionFlipper.Core;

/// <summary>
/// The item icon sheet: one embedded PNG holding every icon, plus a table saying which cell of it
/// belongs to which item id.
///
/// Icons arrive as a single sheet rather than as ~1700 separate resources because the board draws
/// hundreds of them a second: one image is decoded once and every chip is a cheap crop of it.
///
/// The sheet is built from the inventory icons published on minecraft.wiki; see the README for the
/// attribution and for how to rebuild it.
/// </summary>
public static class ItemIconAtlas
{
    private static readonly object Gate = new();
    private static Dictionary<string, int>? _cells;
    private static bool _indexLoaded;

    /// <summary>Icons per row on the sheet.</summary>
    public static int Columns { get; private set; } = 64;

    /// <summary>Edge length of one icon, in pixels.</summary>
    public static int CellSize { get; private set; } = 32;

    /// <summary>How many icons the sheet holds.</summary>
    public static int Count { get; private set; }

    /// <summary>How many item ids resolve to an icon. Larger than <see cref="Count"/>, because
    /// waxed copper and its unwaxed twin share one picture.</summary>
    public static int MappedIds { get; private set; }

    /// <summary>
    /// Finds the sheet cell for an item id, with or without its <c>minecraft:</c> namespace.
    /// Returns false for anything the sheet does not cover, which is the caller's cue to fall back
    /// to the coloured tile.
    /// </summary>
    public static bool TryGetCell(string? itemId, out int cell)
    {
        cell = -1;
        if (string.IsNullOrEmpty(itemId)) return false;

        Dictionary<string, int> cells = LoadIndex();
        return cells.TryGetValue(ItemCatalog.StripNamespace(itemId), out cell);
    }

    /// <summary>The top-left corner of a cell on the sheet, in pixels.</summary>
    public static (int X, int Y) CellOrigin(int cell) =>
        (cell % Columns * CellSize, cell / Columns * CellSize);

    /// <summary>The sheet itself, as PNG bytes. Null only if the resource is missing.</summary>
    public static byte[]? ReadSheetBytes()
    {
        using Stream? stream = OpenResource("icons.png");
        if (stream is null) return null;

        using var buffer = new MemoryStream(1 << 20);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Dictionary<string, int> LoadIndex()
    {
        lock (Gate)
        {
            if (_indexLoaded) return _cells!;
            _indexLoaded = true;
            _cells = new Dictionary<string, int>(2048, StringComparer.Ordinal);

            using Stream? stream = OpenResource("icons.idx");
            if (stream is null) return _cells;

            using var reader = new StreamReader(stream, Encoding.UTF8);

            // Header: columns, cell size, cell count. The rest is one "id<tab>cell" line each.
            string? header = reader.ReadLine();
            string[] parts = header?.Split('\t') ?? [];
            if (parts.Length == 3
                && int.TryParse(parts[0], out int columns)
                && int.TryParse(parts[1], out int size)
                && int.TryParse(parts[2], out int count)
                && columns > 0 && size > 0)
            {
                Columns = columns;
                CellSize = size;
                Count = count;
            }

            while (reader.ReadLine() is { } line)
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (int.TryParse(line.AsSpan(tab + 1), out int cell))
                    _cells[line[..tab]] = cell;
            }

            MappedIds = _cells.Count;
            return _cells;
        }
    }

    private static Stream? OpenResource(string name)
    {
        Assembly assembly = typeof(ItemIconAtlas).Assembly;
        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Assets." + name, StringComparison.Ordinal));
        return resource is null ? null : assembly.GetManifestResourceStream(resource);
    }
}
