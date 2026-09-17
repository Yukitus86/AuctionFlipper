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

    public long TotalBytesOnDisk()
    {
        try
        {
            return System.IO.Directory.GetFiles(_directory, "sales-*.log")
                .Sum(p => new FileInfo(p).Length);
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
