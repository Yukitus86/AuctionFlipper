using System.Text;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

/// <summary>
/// Keeps the sale tape on disk so price history survives restarts.
///
/// This matters more here than in most tools. The API only serves the last ~1000 sales - about two
/// minutes - so a session that starts from nothing knows almost nothing for its first half hour.
/// Persisting the tape means the second launch starts with a day of real prices behind it.
///
/// The format is a flat append-only log of 24-byte records against a separate item-name table.
/// Sales arrive several per second, so each one costs three writes into a buffer and nothing else;
/// there is no database to open, migrate, or ship.
/// </summary>
public sealed class Persistence : IDisposable
{
    private const int RecordSize = 24;   // long soldAt | int item | int count | double price

    private readonly string _directory;
    private readonly object _gate = new();

    private BinaryWriter? _writer;
    private string _currentLogDate = "";
    private int _pendingWrites;
    private int _itemsWrittenToTable;

    public Persistence(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    private string ItemTablePath => Path.Combine(_directory, "items.tbl");

    private string LogPathFor(DateTime utcDate) =>
        Path.Combine(_directory, $"sales-{utcDate:yyyyMMdd}.log");

    // ------------------------------------------------------------------ item table

    /// <summary>
    /// Restores the item table so indexes written by previous sessions still mean the same thing.
    /// Must run before any sale log is read.
    /// </summary>
    public void LoadItemTable(ItemRegistry registry)
    {
        try
        {
            if (!File.Exists(ItemTablePath)) return;

            foreach (string line in File.ReadLines(ItemTablePath, Encoding.UTF8))
            {
                if (line.Length > 0) registry.GetOrAdd(line);
            }
            _itemsWrittenToTable = registry.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Appends any newly discovered item ids. Order is the index, so it is append-only.</summary>
    public void SaveItemTable(ItemRegistry registry)
    {
        try
        {
            string[] names = registry.Snapshot();
            if (names.Length <= _itemsWrittenToTable) return;

            using var stream = new FileStream(ItemTablePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            for (int i = _itemsWrittenToTable; i < names.Length; i++)
                writer.WriteLine(names[i]);

            _itemsWrittenToTable = names.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ sales

    public void RecordSale(Sale sale)
    {
        lock (_gate)
        {
            try
            {
                BinaryWriter writer = EnsureWriter();
                writer.Write(sale.SoldAtUnixMs);
                writer.Write(sale.ItemIndex);
                writer.Write(sale.Count);
                writer.Write(sale.Price);

                // Flushing every couple of hundred sales bounds what a crash can lose to a few
                // seconds of tape without paying for a syscall per sale.
                if (++_pendingWrites >= 256)
                {
                    writer.Flush();
                    _pendingWrites = 0;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private BinaryWriter EnsureWriter()
    {
        string today = DateTime.UtcNow.ToString("yyyyMMdd");
        if (_writer is not null && _currentLogDate == today)
            return _writer;

        _writer?.Flush();
        _writer?.Dispose();

        var stream = new FileStream(LogPathFor(DateTime.UtcNow), FileMode.Append, FileAccess.Write,
            FileShare.Read, bufferSize: 64 * 1024);
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        _currentLogDate = today;
        return _writer;
    }

    /// <summary>
    /// Replays recent sales into the tape. Only the valuation window is loaded - older logs stay on
    /// disk for reference but would not change any current price.
    /// </summary>
    public int LoadRecentSales(SaleTape tape, TimeSpan window)
    {
        long cutoff = DateTimeOffset.UtcNow.Add(-window).ToUnixTimeMilliseconds();
        int loaded = 0;

        try
        {
            // Two days of files is enough to cover a 24 h window across a date boundary.
            var candidates = new[] { DateTime.UtcNow.AddDays(-1), DateTime.UtcNow }
                .Select(LogPathFor)
                .Where(File.Exists);

            var batch = new List<Sale>(4096);
            foreach (string path in candidates)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    bufferSize: 128 * 1024);
                using var reader = new BinaryReader(stream);

                long records = stream.Length / RecordSize;
                for (long i = 0; i < records; i++)
                {
                    long soldAt = reader.ReadInt64();
                    int item = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    double price = reader.ReadDouble();

                    if (soldAt < cutoff) continue;

                    batch.Add(new Sale(item, count, price, 0, soldAt));
                    if (batch.Count >= 4096)
                    {
                        tape.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch));
                        loaded += batch.Count;
                        batch.Clear();
                    }
                }
            }

            if (batch.Count > 0)
            {
                tape.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(batch));
                loaded += batch.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            // A log truncated by a hard shutdown just means slightly less history.
        }

        return loaded;
    }

    /// <summary>Deletes sale logs past the retention window.</summary>
    public void PruneOldLogs(int retentionDays)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.Date.AddDays(-Math.Max(1, retentionDays));
            foreach (string path in System.IO.Directory.GetFiles(_directory, "sales-*.log"))
            {
                string stamp = Path.GetFileNameWithoutExtension(path)["sales-".Length..];
                if (DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime date)
                    && date.Date < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ book snapshot

    /// <summary>Magic bytes, so a truncated or foreign file is rejected instead of misread.</summary>
    private const int BookMagic = 0x4B424641;   // "AFBK"
    private const int BookVersion = 1;

    private string BookPath => Path.Combine(_directory, "book.snap");

    /// <summary>
    /// Writes the standing book so the next launch starts with a market instead of a blank board.
    ///
    /// The file carries its own name tables rather than leaning on <c>items.tbl</c>. Indexes are
    /// positional, and a snapshot that resolved them against a table written later would silently
    /// attach every price to the wrong item - the kind of fault that produces confident nonsense
    /// rather than an error. Self-contained tables cost a few hundred kilobytes and cannot drift.
    /// </summary>
    public int SaveBook(MarketState market)
    {
        Listing[] listings = market.Book.AllListings();
        if (listings.Length == 0) return 0;

        try
        {
            // Local tables: only the ids this snapshot actually references, numbered from zero.
            var itemLocal = new Dictionary<int, int>(4096);
            var itemNames = new List<string>(4096);
            var sellerLocal = new Dictionary<int, int>(8192);
            var sellerNames = new List<string>(8192);

            int LocalItem(int globalIndex)
            {
                if (itemLocal.TryGetValue(globalIndex, out int local)) return local;
                local = itemNames.Count;
                itemLocal[globalIndex] = local;
                itemNames.Add(market.Items.GetName(globalIndex));
                return local;
            }

            int LocalSeller(int globalIndex)
            {
                if (sellerLocal.TryGetValue(globalIndex, out int local)) return local;
                local = sellerNames.Count;
                sellerLocal[globalIndex] = local;
                sellerNames.Add(market.Sellers.GetName(globalIndex));
                return local;
            }

            // Resolve every name before writing a byte, so the tables can go in the header.
            var rows = new (int Item, int Seller, Listing L)[listings.Length];
            for (int i = 0; i < listings.Length; i++)
            {
                Listing l = listings[i];
                rows[i] = (LocalItem(l.ItemIndex), LocalSeller(l.SellerIndex), l);
                if (l.Contents is { Slots.Length: > 0 } contents)
                    foreach (ContainerSlot slot in contents.Slots) LocalItem(slot.ItemIndex);
            }

            string tmp = BookPath + ".tmp";
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                       bufferSize: 256 * 1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(BookMagic);
                writer.Write(BookVersion);
                writer.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                writer.Write(itemNames.Count);
                foreach (string name in itemNames) writer.Write(name);
                writer.Write(sellerNames.Count);
                foreach (string name in sellerNames) writer.Write(name);

                writer.Write(rows.Length);
                foreach ((int item, int seller, Listing l) in rows)
                {
                    writer.Write(item);
                    writer.Write(seller);
                    writer.Write(l.Count);
                    writer.Write(l.Price);
                    writer.Write(l.ListedAtUnixMs);
                    writer.Write(l.ExpiresAtUnixMs);
                    writer.Write(l.Multiplicity);

                    // Looked up rather than interned here: the name tables are already written, so
                    // a name added at this point would be given an index the reader cannot resolve.
                    // The pre-pass above resolved every slot, so nothing is actually dropped.
                    ContainerSlot[] slots = l.Contents?.Slots ?? [];
                    ContainerSlot[] writable = slots
                        .Where(slot => itemLocal.ContainsKey(slot.ItemIndex))
                        .ToArray();

                    writer.Write(writable.Length);
                    foreach (ContainerSlot slot in writable)
                    {
                        writer.Write(itemLocal[slot.ItemIndex]);
                        writer.Write(slot.Count);
                    }
                }
            }

            File.Move(tmp, BookPath, overwrite: true);
            return rows.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the book back, dropping anything that cannot still be on sale.
    ///
    /// Three filters apply, in order of how sure they are. A snapshot older than <paramref
    /// name="maxAge"/> is ignored outright, because prices move and a day-old book would argue for
    /// flips that stopped existing hours ago. A listing whose 24 h expiry has passed is skipped.
    /// What survives is added with its last-seen time set to when the file was written, so the
    /// ordinary unseen-eviction sweep clears anything the collectors never confirm.
    /// </summary>
    public int LoadBook(MarketState market, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(BookPath)) return 0;

            using var stream = new FileStream(BookPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 256 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            if (stream.Length < 16) return 0;
            if (reader.ReadInt32() != BookMagic) return 0;
            if (reader.ReadInt32() != BookVersion) return 0;

            long savedAt = reader.ReadInt64();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - savedAt > (long)maxAge.TotalMilliseconds) return 0;

            int itemCount = reader.ReadInt32();
            if (itemCount is < 0 or > 1_000_000) return 0;
            var items = new int[itemCount];
            for (int i = 0; i < itemCount; i++) items[i] = market.Items.GetOrAdd(reader.ReadString());

            int sellerCount = reader.ReadInt32();
            if (sellerCount is < 0 or > 1_000_000) return 0;
            var sellers = new int[sellerCount];
            for (int i = 0; i < sellerCount; i++) sellers[i] = market.Sellers.GetOrAdd(reader.ReadString());

            int listingCount = reader.ReadInt32();
            if (listingCount is < 0 or > 5_000_000) return 0;

            int restored = 0;
            for (int i = 0; i < listingCount; i++)
            {
                int itemLocal = reader.ReadInt32();
                int sellerLocal = reader.ReadInt32();
                int count = reader.ReadInt32();
                double price = reader.ReadDouble();
                long listedAt = reader.ReadInt64();
                long expiresAt = reader.ReadInt64();
                int multiplicity = reader.ReadInt32();
                int slotCount = reader.ReadInt32();

                ContainerSlot[] slots = [];
                int totalItems = 0;
                if (slotCount > 0)
                {
                    slots = new ContainerSlot[slotCount];
                    for (int s = 0; s < slotCount; s++)
                    {
                        int slotItem = reader.ReadInt32();
                        int slotCountValue = reader.ReadInt32();
                        slots[s] = new ContainerSlot(
                            (uint)slotItem < (uint)items.Length ? items[slotItem] : 0, slotCountValue);
                        totalItems += slotCountValue;
                    }
                }

                if ((uint)itemLocal >= (uint)items.Length || (uint)sellerLocal >= (uint)sellers.Length)
                    continue;
                if (count <= 0 || price <= 0 || expiresAt <= now) continue;

                int itemIndex = items[itemLocal];
                int sellerIndex = sellers[sellerLocal];

                var listing = new Listing
                {
                    Fingerprint = Fingerprint.For(sellerIndex, itemIndex, count, price),
                    ItemIndex = itemIndex,
                    Count = count,
                    Price = price,
                    SellerIndex = sellerIndex,
                    ListedAtUnixMs = listedAt,
                    ExpiresAtUnixMs = expiresAt,
                    Contents = slots.Length > 0
                        ? new ContainerContents { Slots = slots, TotalItems = totalItems }
                        : null,
                    Multiplicity = Math.Max(1, multiplicity),
                    Restored = true,
                };

                if (market.Book.Add(listing, savedAt)) restored++;
            }

            return restored;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException
                                      or UnauthorizedAccessException or OutOfMemoryException)
        {
            // A snapshot truncated by a hard shutdown just means a cold start, which is survivable.
            return 0;
        }
    }

    public long TotalBytesOnDisk()
    {
        try
        {
            long sales = System.IO.Directory.GetFiles(_directory, "sales-*.log")
                .Sum(p => new FileInfo(p).Length);

            return File.Exists(BookPath) ? sales + new FileInfo(BookPath).Length : sales;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _pendingWrites = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}
