namespace AuctionFlipper.Core;

public enum ValueSource
{
    /// <summary>Nothing known about this item yet.</summary>
    None,
    /// <summary>Derived from observed sales. The only source worth trusting for real money.</summary>
    Sales,
    /// <summary>Derived from the ask ladder because nothing has been seen to sell yet.</summary>
    Book,
}

/// <summary>What one unit of an item is worth, and how much that estimate should be trusted.</summary>
public readonly record struct ItemValue(
    double Unit,
    ValueSource Source,
    ItemSaleStats Sales,
    BookStats Book)
{
    public bool IsKnown => Source != ValueSource.None && Unit > 0;

    /// <summary>Sale-derived values with a decent sample are the ones worth acting on.</summary>
    public bool IsConfident => Source == ValueSource.Sales && Sales.IsConfident;
}

public static class Valuation
{
    /// <summary>
    /// Fair unit value for an item.
    ///
    /// Sales come first: what people actually paid beats what sellers are asking, because an ask
    /// ladder can be talked up by anyone willing to list. Only when an item has not been seen to
    /// trade does this fall back to the ask side, and then to a trimmed slice of it rather than the
    /// lowest ask, which is exactly where mispricings and traps sit.
    /// </summary>
    public static ItemValue Compute(in ItemSaleStats sales, in BookStats book)
    {
        if (sales.IsConfident && sales.MedianUnit > 0)
            return new ItemValue(sales.MedianUnit, ValueSource.Sales, sales, book);

        // A handful of sales still beats the ask ladder, but blend toward the book so a single
        // outlier trade cannot define an item's value on its own.
        if (sales.SampleCount > 0 && sales.MedianUnit > 0 && HasUsableBook(book))
        {
            double weight = sales.SampleCount / (double)MarketConstants.MinSalesForConfidentValue;
            weight = Math.Clamp(weight, 0, 1);
            double blended = sales.MedianUnit * weight + book.TrimmedMeanUnit * (1 - weight);
            return new ItemValue(blended, ValueSource.Sales, sales, book);
        }

        if (sales.SampleCount > 0 && sales.MedianUnit > 0)
            return new ItemValue(sales.MedianUnit, ValueSource.Sales, sales, book);

        if (HasUsableBook(book))
            return new ItemValue(book.TrimmedMeanUnit, ValueSource.Book, sales, book);

        return new ItemValue(0, ValueSource.None, sales, book);
    }

    /// <summary>
    /// Whether the ask ladder is deep enough to infer a price from.
    ///
    /// The minimum is not fussiness. The auction house carries listings priced at a trillion coins,
    /// and the trimmed percentile band only steps around them when there are enough real listings
    /// either side. On a two-deep ladder a single troll listing becomes the item's median and
    /// every downstream number - fair value, container contents, profit - inherits the nonsense.
    /// Below this depth the item is left unpriced instead.
    /// </summary>
    private static bool HasUsableBook(in BookStats book)
    {
        const int minimumAsks = 6;
        return book.AskCount >= minimumAsks && book.TrimmedMeanUnit > 0;
    }
}
