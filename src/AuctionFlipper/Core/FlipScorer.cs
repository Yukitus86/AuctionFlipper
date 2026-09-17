namespace AuctionFlipper.Core;

[Flags]
public enum FlipFlags
{
    None = 0,
    /// <summary>
    /// Newly listed as far as the API is concerned. That means minutes, not seconds: the listing
    /// feed is a slow-moving view whose newest rows already have several minutes on them.
    /// </summary>
    New = 1 << 0,
    /// <summary>The id can carry enchantments the API refuses to show.</summary>
    NbtRisk = 1 << 1,
    /// <summary>A shulker box or bundle, valued from its contents.</summary>
    Container = 1 << 2,
    /// <summary>Few listings near this price, so the resale price is not well supported.</summary>
    ThinBook = 1 << 3,
    /// <summary>Has sat unsold for hours. The market has already looked at it and passed.</summary>
    Stale = 1 << 4,
    /// <summary>Sale prices for this item scatter widely.</summary>
    Volatile = 1 << 5,
    /// <summary>Discounted so far below value that something is probably wrong with it.</summary>
    TooGoodToBeTrue = 1 << 6,
    /// <summary>One seller holds much of the cheap side, so they set the price, not the market.</summary>
    SellerWall = 1 << 7,
    /// <summary>Value is guessed from the ask ladder because nothing has been seen to sell.</summary>
    NoSaleHistory = 1 << 8,
    TrendingUp = 1 << 9,
    TrendingDown = 1 << 10,
    /// <summary>Costs more than the capital configured in Settings.</summary>
    OverBudget = 1 << 11,
}

public enum FlipGrade
{
    S = 0,
    A = 1,
    B = 2,
    C = 3,
}

/// <summary>A scored buy-and-relist opportunity.</summary>
public sealed class FlipOpportunity
{
    public required Listing Listing { get; init; }
    public required string ItemId { get; init; }
    public required ItemInfo Info { get; init; }
    public required string SellerName { get; init; }

    public required double BuyTotal { get; init; }
    public required double BuyUnit { get; init; }
    public required int Count { get; init; }

    /// <summary>Unit price the flip should relist at, already undercutting the next ask.</summary>
    public required double ResellUnit { get; init; }
    public required double ResellTotal { get; init; }
    public required double FairUnit { get; init; }
    public required ValueSource ValueSource { get; init; }

    public required double NetProfit { get; init; }
    public required double Roi { get; init; }

    public required double SalesPerHour { get; init; }
    /// <summary>Estimated hours for the market to absorb this stack at its observed rate.</summary>
    public required double AbsorbHours { get; init; }
    public required double ProfitPerHour { get; init; }

    public required double Confidence { get; init; }
    public required FlipGrade Grade { get; init; }
    public required FlipFlags Flags { get; init; }

    /// <summary>Plain-language reasons confidence was reduced, shown in the detail panel.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    public required long AgeMs { get; init; }
    public required long ExpiresAtUnixMs { get; init; }

    /// <summary>Contents breakdown, for container flips only.</summary>
    public IReadOnlyList<ContainerLine> ContainerLines { get; init; } = [];

    /// <summary>Risk-adjusted profit per hour. The default ranking on the board.</summary>
    public double Score => ProfitPerHour * (Confidence / 100.0);

    /// <summary>What to type after /ah to find this listing in game.</summary>
    public string SearchText => Info.SearchText;
}

/// <summary>
/// Turns a listing into a flip, or rejects it.
///
/// The arithmetic is the easy half. The hard half is not being fooled: an auction house is full of
/// listings that look underpriced and are not, either because the item carries value the API will
/// not show, because one seller is quoting against themselves, or because the listing has been sat
/// there all day precisely because nobody wants it at that price. Every one of those shows up as a
/// confidence penalty with a reason attached, so a flip that scores badly says why.
/// </summary>
public sealed class FlipScorer
{
    private readonly double _taxRate;
    private readonly double _capital;
    private readonly int _listingSlots;

    /// <summary>
    /// Undercut applied when relisting. Matching the next ask exactly means queueing behind it;
    /// a hair under puts the flip at the front.
    /// </summary>
    private const double UndercutFraction = 0.005;

    /// <summary>Return above which a flip is treated as a valuation fault rather than a bargain.</summary>
    private const double AbsurdRoi = 20.0;   // 2000%

    /// <summary>
    /// Age below which a listing still counts as fresh. Set to fifteen minutes because the API's
    /// listing feed does not surface anything newer than a few minutes, so a tighter threshold
    /// would simply never be met.
    /// </summary>
    private const double FreshAgeHours = 0.25;

    public FlipScorer(double taxRate, double capital, int listingSlots = 25)
    {
        _taxRate = taxRate;
        _capital = capital;
        _listingSlots = Math.Max(1, listingSlots);
    }

    public FlipOpportunity? Score(
        Listing listing,
        string itemId,
        string sellerName,
        in ItemValue value,
        double nextAskUnit,
        int depthNearValue,
        int sellerConcentration,
        long nowUnixMs)
    {
        if (!value.IsKnown || listing.Count <= 0 || listing.Price <= 0)
            return null;

        ItemInfo info = ItemCatalog.Get(itemId);

        double buyUnit = listing.UnitPrice;
        double fair = value.Unit;

        // Relist price: undercut whatever is left on the book, and never ask more than the item has
        // actually been selling for. Whichever is lower is the price that will really clear.
        double target = nextAskUnit > 0 ? Math.Min(fair, nextAskUnit * (1 - UndercutFraction)) : fair;
        if (target <= buyUnit)
            return null;

        double resellTotal = target * listing.Count;
        double net = resellTotal * (1 - _taxRate) - listing.Price;
        if (net <= 0)
            return null;

        double roi = net / listing.Price;

        // Absorption: how long the market takes to eat this many units at its observed rate. A
        // 10,000-unit stack of something that trades twice an hour is not a one-hour flip.
        double unitsPerHour = Math.Max(value.Sales.UnitsPerHour, 0.001);
        double absorbHours = Math.Clamp(listing.Count / unitsPerHour, 0.05, 72);
        double profitPerHour = net / absorbHours;

        var notes = new List<string>();
        FlipFlags flags = FlipFlags.None;
        double confidence = 1.0;

        // --- how much sale evidence exists ---
        if (value.Source == ValueSource.Book)
        {
            confidence *= 0.45;
            flags |= FlipFlags.NoSaleHistory;
            notes.Add(Loc.T("NoteNoSales"));
        }
        else if (value.Sales.SampleCount < MarketConstants.MinSalesForConfidentValue)
        {
            confidence *= 0.55 + 0.09 * value.Sales.SampleCount;
            notes.Add(Loc.T("NoteFewSales", value.Sales.SampleCount));
        }

        // --- how tightly this item prices ---
        if (value.Sales.Dispersion > 0.15)
        {
            confidence *= 1.0 / (1.0 + 2.0 * value.Sales.Dispersion);
            flags |= FlipFlags.Volatile;
            notes.Add(Loc.T("NoteDispersion", value.Sales.Dispersion.ToString("P0")));
        }

        // --- is the resale price actually supported by other listings ---
        if (depthNearValue < 4)
        {
            confidence *= 0.55 + 0.11 * depthNearValue;
            flags |= FlipFlags.ThinBook;
            notes.Add(Loc.T("NoteThinBook", depthNearValue));
        }

        // --- hidden value the API will not show ---
        if (info.NbtRisk)
        {
            confidence *= 0.35;
            flags |= FlipFlags.NbtRisk;
            notes.Add(Loc.T("NoteNbt"));
        }

        if (info.IsContainer)
            flags |= FlipFlags.Container;

        // --- is one seller quoting against themselves ---
        if (sellerConcentration >= 3)
        {
            confidence *= Math.Max(0.5, 1.0 - 0.08 * (sellerConcentration - 2));
            flags |= FlipFlags.SellerWall;
            notes.Add(Loc.T("NoteSellerWall", sellerConcentration));
        }

        // --- a discount too large to be an accident ---
        double discount = fair > 0 ? 1 - buyUnit / fair : 0;
        if (discount > 0.80)
        {
            confidence *= 0.30;
            flags |= FlipFlags.TooGoodToBeTrue;
            notes.Add(Loc.T("NoteDeepDiscount", discount.ToString("P0")));
        }
        else if (discount > 0.65)
        {
            confidence *= 0.60;
            flags |= FlipFlags.TooGoodToBeTrue;
            notes.Add(Loc.T("NoteDiscount", discount.ToString("P0")));
        }

        // A last backstop against a broken valuation rather than a real bargain. Nothing on a live
        // auction house is genuinely available at a twentieth of what it resells for; a number like
        // that means the reference price is wrong, and it should not be presented as a find.
        if (roi > AbsurdRoi)
        {
            confidence *= 0.15;
            flags |= FlipFlags.TooGoodToBeTrue;
            notes.Add(Loc.T("NoteAbsurdRoi", roi.ToString("P0")));
        }

        // --- has the market already rejected it ---
        long ageMs = listing.AgeMs(nowUnixMs);
        double ageHours = ageMs / 3_600_000.0;
        if (ageHours < FreshAgeHours)
        {
            flags |= FlipFlags.New;
        }
        else if (ageHours > 1)
        {
            // Past roughly an hour, a standing bargain is evidence against itself: every other
            // player with a scanner has already seen it and left it alone.
            confidence *= Math.Max(0.35, 1.0 - 0.08 * ageHours);
            if (ageHours > 4)
            {
                flags |= FlipFlags.Stale;
                notes.Add(Loc.T("NoteStale", ageHours.ToString("F1")));
            }
        }

        // --- will it move at all ---
        double salesPerHour = value.Sales.SalesPerHour;
        if (salesPerHour < 1)
        {
            confidence *= Math.Max(0.4, 0.4 + 0.6 * salesPerHour);
            if (salesPerHour < 0.2)
                notes.Add(Loc.T("NoteIlliquid"));
        }

        // --- which way the price is heading ---
        if (value.Sales.TrendFraction > 0.08)
        {
            flags |= FlipFlags.TrendingUp;
            confidence *= 1.05;
        }
        else if (value.Sales.TrendFraction < -0.08)
        {
            flags |= FlipFlags.TrendingDown;
            confidence *= 0.85;
            notes.Add(Loc.T("NoteTrendDown", Math.Abs(value.Sales.TrendFraction).ToString("P0")));
        }

        if (_capital > 0 && listing.Price > _capital)
        {
            flags |= FlipFlags.OverBudget;
            notes.Add(Loc.T("NoteOverBudget"));
        }

        double confidencePct = Math.Clamp(confidence * 100, 0, 100);

        return new FlipOpportunity
        {
            Listing = listing,
            ItemId = itemId,
            Info = info,
            SellerName = sellerName,
            BuyTotal = listing.Price,
            BuyUnit = buyUnit,
            Count = listing.Count,
            ResellUnit = target,
            ResellTotal = resellTotal,
            FairUnit = fair,
            ValueSource = value.Source,
            NetProfit = net,
            Roi = roi,
            SalesPerHour = salesPerHour,
            AbsorbHours = absorbHours,
            ProfitPerHour = profitPerHour,
            Confidence = confidencePct,
            Grade = GradeFor(net, roi, confidencePct),
            Flags = flags,
            Notes = notes,
            AgeMs = ageMs,
            ExpiresAtUnixMs = listing.ExpiresAtUnixMs,
        };
    }

    /// <summary>
    /// Scores a shulker box or bundle, which is a different trade entirely: the exit is unpacking
    /// the box and selling its contents at their own prices, not relisting the box.
    /// </summary>
    public FlipOpportunity? ScoreContainer(
        Listing listing,
        string itemId,
        string sellerName,
        in ContainerValuation valuation,
        long nowUnixMs)
    {
        if (!valuation.HasValue || listing.Price <= 0)
            return null;

        double net = valuation.RecoverableValue * (1 - _taxRate) - listing.Price;
        if (net <= 0)
            return null;

        ItemInfo info = ItemCatalog.Get(itemId);
        double roi = net / listing.Price;
        double absorbHours = Math.Clamp(valuation.SlowestAbsorbHours, 0.05, 72);
        double profitPerHour = net / absorbHours;

        var notes = new List<string>
        {
            $"Contents value {valuation.GrossValue:N0}; recoverable after undercuts {valuation.RecoverableValue:N0}.",
        };
        FlipFlags flags = FlipFlags.Container;
        double confidence = 1.0;

        // Part of a box the tool cannot price is a hole in the estimate, so coverage drives
        // confidence directly.
        if (valuation.Coverage < 1.0)
        {
            confidence *= Math.Max(0.25, valuation.Coverage);
            notes.Add(Loc.T("NoteUnvalued", valuation.UnvaluedItems));
        }

        // Emptying a box into the auction house eats listing slots, and slots are capped. A box
        // holding more distinct items than you can list at once cannot be turned over in one go,
        // which stretches the flip out well past what the absorption estimate suggests.
        int slotsNeeded = valuation.Lines.Count(l => l.Valued);
        notes.Add(Loc.T("NoteSlots", slotsNeeded, _listingSlots));

        if (slotsNeeded > _listingSlots)
        {
            confidence *= 0.7;
            notes.Add(Loc.T("NoteSlotsShort"));
        }
        else if (slotsNeeded > _listingSlots / 3)
        {
            confidence *= 0.85;
        }

        // Same backstop as an ordinary flip. A box selling for a fraction of its contents means one
        // of those contents is mispriced far more often than it means free money.
        if (roi > AbsurdRoi)
        {
            confidence *= 0.15;
            flags |= FlipFlags.TooGoodToBeTrue;
            notes.Add(Loc.T("NoteAbsurdRoiBox", roi.ToString("P0")));
        }

        // A box whose worth is concentrated in one line is a bet on that single item, and carries
        // that item's pricing risk undiluted rather than a basket's.
        if (valuation.DominantShare > 0.6)
        {
            confidence *= Math.Max(0.6, 1.0 - (valuation.DominantShare - 0.6));
            notes.Add(Loc.T("NoteDominant", valuation.DominantShare.ToString("P0")));
        }

        long ageMs = listing.AgeMs(nowUnixMs);
        double ageHours = ageMs / 3_600_000.0;
        if (ageHours < FreshAgeHours) flags |= FlipFlags.New;
        else if (ageHours > 4)
        {
            flags |= FlipFlags.Stale;
            confidence *= Math.Max(0.4, 1.0 - 0.06 * ageHours);
            notes.Add(Loc.T("NoteStale", ageHours.ToString("F1")));
        }

        if (_capital > 0 && listing.Price > _capital)
        {
            flags |= FlipFlags.OverBudget;
            notes.Add(Loc.T("NoteOverBudget"));
        }

        double confidencePct = Math.Clamp(confidence * 100, 0, 100);
        double perUnit = valuation.RecoverableValue / Math.Max(1, listing.Count);

        return new FlipOpportunity
        {
            Listing = listing,
            ItemId = itemId,
            Info = info,
            SellerName = sellerName,
            BuyTotal = listing.Price,
            BuyUnit = listing.UnitPrice,
            Count = listing.Count,
            ResellUnit = perUnit,
            ResellTotal = valuation.RecoverableValue,
            FairUnit = valuation.GrossValue / Math.Max(1, listing.Count),
            ValueSource = ValueSource.Sales,
            NetProfit = net,
            Roi = roi,
            SalesPerHour = 0,
            AbsorbHours = absorbHours,
            ProfitPerHour = profitPerHour,
            Confidence = confidencePct,
            Grade = GradeFor(net, roi, confidencePct),
            Flags = flags,
            Notes = notes,
            AgeMs = ageMs,
            ExpiresAtUnixMs = listing.ExpiresAtUnixMs,
            ContainerLines = valuation.Lines,
        };
    }

    /// <summary>
    /// Grades demand all three of profit, margin and confidence at once. A huge margin on an item
    /// nothing is known about is a C, and so is a confident flip worth 900 coins.
    /// </summary>
    private static FlipGrade GradeFor(double net, double roi, double confidence)
    {
        if (confidence >= 70 && roi >= 0.60 && net >= 250_000) return FlipGrade.S;
        if (confidence >= 55 && roi >= 0.30 && net >= 100_000) return FlipGrade.A;
        if (confidence >= 40 && roi >= 0.15 && net >= 25_000) return FlipGrade.B;
        return FlipGrade.C;
    }
}
