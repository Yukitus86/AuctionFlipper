namespace AuctionFlipper.Core;

/// <summary>
/// Rolling statistics for one item, derived only from sales actually observed.
/// </summary>
/// <param name="SampleCount">Sales inside the valuation window.</param>
/// <param name="MedianUnit">Time-decayed weighted median unit price — the fair value estimate.</param>
/// <param name="Dispersion">Median absolute deviation over the median; 0 is a rock-steady price.</param>
/// <param name="TrendFraction">Recent median against older median; +0.1 is a 10% uptrend.</param>
public readonly record struct ItemSaleStats(
    int SampleCount,
    double MedianUnit,
    double P25Unit,
    double P75Unit,
    double Dispersion,
    double SalesPerHour,
    double UnitsPerHour,
    double TrendFraction,
    long NewestSoldAtUnixMs)
{
    public static readonly ItemSaleStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public bool IsConfident => SampleCount >= MarketConstants.MinSalesForConfidentValue;
}

/// <summary>
/// The sale tape: every completed sale the tool has seen, indexed by item.
///
/// This is the most valuable thing the tool owns and the reason it is worth leaving running. The
/// API only serves the last ~1000 sales, about two minutes of market, so genuine price history
/// exists only because the tape is polled continuously and persisted. Fair value, how fast an item
/// moves, and whether a price is trending are all derived from here.
/// </summary>
public sealed class SaleTape
{
    /// <summary>Samples kept per item. Ample for robust statistics, bounded for memory.</summary>
    private const int PerItemCapacity = 512;

    private const int GlobalRingCapacity = 4096;

    /// <summary>Per-item stats are recomputed at most this often; listings arrive far faster.</summary>
    private const long StatsCacheMs = 2_000;

    private sealed class ItemSales
    {
        public long[] Times = new long[32];
        public double[] UnitPrices = new double[32];
        public int[] Counts = new int[32];
        public int Head;
        public int Length;

        public ItemSaleStats Cached = ItemSaleStats.Empty;
        public long CachedAtMs;
        public bool Dirty = true;
    }

    private readonly Dictionary<int, ItemSales> _byItem = new(2048);
    private readonly Sale[] _globalRing = new Sale[GlobalRingCapacity];
    private int _globalHead;
    private int _globalLength;
    private long _totalSales;
    private long _firstSaleObservedAtMs;

    private readonly object _gate = new();

    public long TotalSales { get { lock (_gate) return _totalSales; } }

    public int TrackedItems { get { lock (_gate) return _byItem.Count; } }

    /// <summary>How long the tape has been watching. Bounds every per-hour rate it reports.</summary>
    public TimeSpan ObservedSpan
    {
        get
        {
            lock (_gate)
            {
                if (_firstSaleObservedAtMs == 0) return TimeSpan.Zero;
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return TimeSpan.FromMilliseconds(Math.Max(0, now - _firstSaleObservedAtMs));
            }
        }
    }

    public void Add(in Sale sale)
    {
        lock (_gate) AddLocked(sale);
    }

    public void AddRange(ReadOnlySpan<Sale> sales)
    {
        lock (_gate)
        {
            foreach (ref readonly Sale s in sales)
                AddLocked(s);
        }
    }

    private void AddLocked(in Sale sale)
    {
        if (_firstSaleObservedAtMs == 0 || sale.SoldAtUnixMs < _firstSaleObservedAtMs)
            _firstSaleObservedAtMs = sale.SoldAtUnixMs;

        _totalSales++;

        _globalRing[(_globalHead + _globalLength) % GlobalRingCapacity] = sale;
        if (_globalLength < GlobalRingCapacity)
            _globalLength++;
        else
            _globalHead = (_globalHead + 1) % GlobalRingCapacity;

        if (!_byItem.TryGetValue(sale.ItemIndex, out ItemSales? series))
        {
            series = new ItemSales();
            _byItem[sale.ItemIndex] = series;
        }

        Push(series, sale.SoldAtUnixMs, sale.UnitPrice, sale.Count);
        series.Dirty = true;
    }

    private static void Push(ItemSales s, long time, double unitPrice, int count)
    {
        if (s.Length == s.Times.Length && s.Length < PerItemCapacity)
            Grow(s);

        if (s.Length < s.Times.Length)
        {
            int idx = (s.Head + s.Length) % s.Times.Length;
            s.Times[idx] = time;
            s.UnitPrices[idx] = unitPrice;
            s.Counts[idx] = count;
            s.Length++;
        }
        else
        {
            // At capacity: overwrite the oldest sample and advance the head.
            s.Times[s.Head] = time;
            s.UnitPrices[s.Head] = unitPrice;
            s.Counts[s.Head] = count;
            s.Head = (s.Head + 1) % s.Times.Length;
        }
    }

    private static void Grow(ItemSales s)
    {
        int newSize = Math.Min(PerItemCapacity, s.Times.Length * 2);
        var t = new long[newSize];
        var p = new double[newSize];
        var c = new int[newSize];
        for (int i = 0; i < s.Length; i++)
        {
            int src = (s.Head + i) % s.Times.Length;
            t[i] = s.Times[src];
            p[i] = s.UnitPrices[src];
            c[i] = s.Counts[src];
        }
        s.Times = t;
        s.UnitPrices = p;
        s.Counts = c;
        s.Head = 0;
    }

    public ItemSaleStats GetStats(int itemIndex, long nowUnixMs)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemSales? s) || s.Length == 0)
                return ItemSaleStats.Empty;

            if (!s.Dirty && nowUnixMs - s.CachedAtMs < StatsCacheMs)
                return s.Cached;

            s.Cached = Compute(s, nowUnixMs, _firstSaleObservedAtMs);
            s.CachedAtMs = nowUnixMs;
            s.Dirty = false;
            return s.Cached;
        }
    }

    private static ItemSaleStats Compute(ItemSales s, long nowMs, long tapeStartMs)
    {
        long windowStart = nowMs - (long)MarketConstants.ValuationWindow.TotalMilliseconds;

        double[] prices = new double[s.Length];
        double[] weights = new double[s.Length];

        double halfLifeMs = MarketConstants.ValuationHalfLife.TotalMilliseconds;
        int n = 0;
        long totalUnits = 0;
        long newest = 0;

        for (int i = 0; i < s.Length; i++)
        {
            int idx = (s.Head + i) % s.Times.Length;
            long t = s.Times[idx];
            if (t < windowStart) continue;

            prices[n] = s.UnitPrices[idx];
            // Exponential decay: a sale from an hour ago says more than one from yesterday.
            weights[n] = Math.Pow(0.5, (nowMs - t) / halfLifeMs);
            totalUnits += s.Counts[idx];
            if (t > newest) newest = t;
            n++;
        }

        if (n == 0) return ItemSaleStats.Empty;

        Span<double> p = prices.AsSpan(0, n);
        Span<double> w = weights.AsSpan(0, n);

        // Sort by price carrying the weights along, so the weighted quantiles below are one pass.
        SortByKey(p, w);

        double median = WeightedQuantile(p, w, 0.50);
        double p25 = WeightedQuantile(p, w, 0.25);
        double p75 = WeightedQuantile(p, w, 0.75);

        double mad = MedianAbsoluteDeviation(p, median);
        double dispersion = median > 0 ? mad / median : 0;

        // Rates divide by how long the tape has actually watched, never by a nominal 24 h, or a
        // freshly started session would report every item as nearly dead.
        double observedMs = Math.Max(60_000, nowMs - Math.Max(tapeStartMs, windowStart));
        double hours = observedMs / 3_600_000.0;

        double trend = ComputeTrend(s, windowStart, median);

        return new ItemSaleStats(n, median, p25, p75, dispersion, n / hours, totalUnits / hours, trend, newest);
    }

    /// <summary>Median of the newest third against the oldest third, as a fraction.</summary>
    private static double ComputeTrend(ItemSales s, long windowStart, double median)
    {
        if (s.Length < 6 || median <= 0) return 0;

        int third = s.Length / 3;
        var older = new List<double>(third + 1);
        var recent = new List<double>(third + 1);

        for (int i = 0; i < s.Length; i++)
        {
            int idx = (s.Head + i) % s.Times.Length;
            if (s.Times[idx] < windowStart) continue;
            if (i < third) older.Add(s.UnitPrices[idx]);
            else if (i >= s.Length - third) recent.Add(s.UnitPrices[idx]);
        }

        if (recent.Count == 0 || older.Count == 0) return 0;

        recent.Sort();
        older.Sort();
        double mRecent = recent[recent.Count / 2];
        double mOlder = older[older.Count / 2];
        if (mOlder <= 0) return 0;

        return Math.Clamp((mRecent - mOlder) / mOlder, -1.0, 1.0);
    }

    private static void SortByKey(Span<double> keys, Span<double> payload)
    {
        // Insertion sort: n is at most 512 and sale prices for one item cluster tightly, so the
        // input is usually close to sorted already.
        for (int i = 1; i < keys.Length; i++)
        {
            double k = keys[i];
            double pay = payload[i];
            int j = i - 1;
            while (j >= 0 && keys[j] > k)
            {
                keys[j + 1] = keys[j];
                payload[j + 1] = payload[j];
                j--;
            }
            keys[j + 1] = k;
            payload[j + 1] = pay;
        }
    }

    private static double WeightedQuantile(ReadOnlySpan<double> sortedPrices, ReadOnlySpan<double> weights, double q)
    {
        double total = 0;
        for (int i = 0; i < weights.Length; i++) total += weights[i];
        if (total <= 0)
            return sortedPrices.Length > 0 ? sortedPrices[sortedPrices.Length / 2] : 0;

        double target = total * q;
        double running = 0;
        for (int i = 0; i < sortedPrices.Length; i++)
        {
            running += weights[i];
            if (running >= target) return sortedPrices[i];
        }
        return sortedPrices[^1];
    }

    private static double MedianAbsoluteDeviation(ReadOnlySpan<double> sortedPrices, double median)
    {
        int n = sortedPrices.Length;
        if (n == 0) return 0;

        double[] dev = new double[n];
        for (int i = 0; i < n; i++) dev[i] = Math.Abs(sortedPrices[i] - median);
        Array.Sort(dev);
        return dev[n / 2];
    }

    /// <summary>Most recent sales across all items, newest first. Feeds the live tape view.</summary>
    public Sale[] RecentSales(int max)
    {
        lock (_gate)
        {
            int take = Math.Min(max, _globalLength);
            var result = new Sale[take];
            for (int i = 0; i < take; i++)
            {
                int idx = ((_globalHead + _globalLength - 1 - i) % GlobalRingCapacity + GlobalRingCapacity) % GlobalRingCapacity;
                result[i] = _globalRing[idx];
            }
            return result;
        }
    }

    /// <summary>
    /// The newest sales of one item, newest first.
    ///
    /// Read out of the per-item series rather than by filtering the global ring, because the ring
    /// only holds the last few thousand sales of the whole market - on a busy auction house that is
    /// a couple of minutes, and a quiet item would show nothing at all.
    /// </summary>
    public (long Time, double UnitPrice, int Count)[] RecentSalesFor(int itemIndex, int max)
    {
        lock (_gate)
        {
            if (max <= 0 || !_byItem.TryGetValue(itemIndex, out ItemSales? s) || s.Length == 0)
                return [];

            int take = Math.Min(max, s.Length);
            var result = new (long, double, int)[take];
            for (int i = 0; i < take; i++)
            {
                int idx = (s.Head + s.Length - 1 - i) % s.Times.Length;
                result[i] = (s.Times[idx], s.UnitPrices[idx], s.Counts[idx]);
            }
            return result;
        }
    }

    /// <summary>Sale points for one item inside the window, oldest first. Feeds the sparkline.</summary>
    public (long Time, double UnitPrice)[] History(int itemIndex, long nowUnixMs)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemSales? s) || s.Length == 0)
                return [];

            long windowStart = nowUnixMs - (long)MarketConstants.ValuationWindow.TotalMilliseconds;
            var list = new List<(long, double)>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int idx = (s.Head + i) % s.Times.Length;
                if (s.Times[idx] >= windowStart)
                    list.Add((s.Times[idx], s.UnitPrices[idx]));
            }
            return list.ToArray();
        }
    }
}
