namespace AuctionFlipper.Core;

public readonly record struct ContainerLine(string ItemId, string DisplayName, int Count, double UnitValue, bool Valued)
{
    public double LineValue => Valued ? Count * UnitValue : 0;
}

/// <param name="DominantShare">
/// Share of the container's value sitting in its single most valuable line. A box that is really
/// one expensive stack plus packaging is a bet on that one item, not a diversified basket.
/// </param>
public readonly record struct ContainerValuation(
    double GrossValue,
    double RecoverableValue,
    int ValuedItems,
    int UnvaluedItems,
    double SlowestAbsorbHours,
    double DominantShare,
    IReadOnlyList<ContainerLine> Lines)
{
    public bool HasValue => ValuedItems > 0 && GrossValue > 0;

    /// <summary>Share of the container, by item count, the tool can actually put a price on.</summary>
    public double Coverage => ValuedItems + UnvaluedItems == 0
        ? 0
        : (double)ValuedItems / (ValuedItems + UnvaluedItems);
}

/// <summary>
/// Prices shulker boxes and bundles from what is inside them.
///
/// This is the one place the API is unusually generous: <c>contents</c> is fully populated even
/// though enchantments and lore are not. Roughly one listing in ten is a container, and sellers
/// routinely price them as "a shulker" rather than totting up what they hold, so a box of stacked
/// commodities can sit well under the value of its contents. The flip is to buy it, unpack it and
/// sell the parts.
/// </summary>
public static class ContainerValuer
{
    public static ContainerValuation Value(
        ContainerContents contents,
        Func<int, ItemValue> valueOf,
        Func<int, string> idOf,
        double haircut)
    {
        var lines = new List<ContainerLine>(contents.Slots.Length);
        double gross = 0;
        double largestLine = 0;
        int valued = 0;
        int unvalued = 0;
        double slowestHours = 0;

        foreach (ContainerSlot slot in contents.Slots)
        {
            string id = idOf(slot.ItemIndex);
            ItemInfo info = ItemCatalog.Get(id);
            ItemValue v = valueOf(slot.ItemIndex);

            // Two rules, both deliberately strict, because a box is only ever worth what its
            // contents fetch and any error inside is multiplied by the stack size:
            //   * contents that could carry hidden enchantments are counted as worthless rather
            //     than guessed at, so a box is never sold to the user on the strength of a sword;
            //   * contents are priced only from observed sales, never from the ask ladder. An
            //     inferred price on one stacked item is how a 800k box comes to claim it is worth
            //     800 million.
            bool usable = v.IsConfident && !info.NbtRisk;

            if (usable)
            {
                double lineValue = slot.Count * v.Unit;
                gross += lineValue;
                if (lineValue > largestLine) largestLine = lineValue;
                valued += slot.Count;

                double unitsPerHour = Math.Max(v.Sales.UnitsPerHour, 0.001);
                slowestHours = Math.Max(slowestHours, Math.Clamp(slot.Count / unitsPerHour, 0.05, 72));
            }
            else
            {
                unvalued += slot.Count;
            }

            lines.Add(new ContainerLine(id, info.DisplayName, slot.Count, usable ? v.Unit : 0, usable));
        }

        // Unpacking and relisting a box takes many listing slots and undercuts of its own, so only
        // part of the raw contents value is genuinely recoverable.
        return new ContainerValuation(gross, gross * haircut, valued, unvalued,
            slowestHours <= 0 ? 1 : slowestHours,
            gross > 0 ? largestLine / gross : 0,
            lines);
    }

    /// <summary>Collapses a container payload into one line per distinct item.</summary>
    public static ContainerContents Build(IEnumerable<(int ItemIndex, int Count)> slots)
    {
        var totals = new Dictionary<int, int>(16);
        int total = 0;
        foreach ((int itemIndex, int count) in slots)
        {
            if (count <= 0) continue;
            totals[itemIndex] = totals.TryGetValue(itemIndex, out int existing) ? existing + count : count;
            total += count;
        }

        var array = new ContainerSlot[totals.Count];
        int i = 0;
        foreach ((int itemIndex, int count) in totals)
            array[i++] = new ContainerSlot(itemIndex, count);

        Array.Sort(array, static (a, b) => b.Count.CompareTo(a.Count));
        return new ContainerContents { Slots = array, TotalItems = total };
    }
}
