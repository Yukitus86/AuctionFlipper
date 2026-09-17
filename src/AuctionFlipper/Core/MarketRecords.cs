namespace AuctionFlipper.Core;

/// <summary>Contents of a shulker box or bundle, flattened and totalled by item.</summary>
public sealed class ContainerContents
{
    public required ContainerSlot[] Slots { get; init; }
    public int TotalItems { get; init; }
}

public readonly record struct ContainerSlot(int ItemIndex, int Count);

/// <summary>
/// A live auction listing.
///
/// Prices on the wire are per listing, not per item, so <see cref="UnitPrice"/> is what every
/// comparison uses: a stack of 64 at 640k and a single at 10k are the same offer.
/// </summary>
public sealed class Listing
{
    public required ulong Fingerprint { get; init; }
    public required int ItemIndex { get; init; }
    public required int Count { get; init; }
    public required double Price { get; init; }
    public required int SellerIndex { get; init; }

    /// <summary>Server-derived listing time, reconstructed from <c>time_left</c>.</summary>
    public required long ListedAtUnixMs { get; init; }
    public required long ExpiresAtUnixMs { get; init; }

    public ContainerContents? Contents { get; init; }

    /// <summary>Local clock time this listing was last observed, used for eviction.</summary>
    public long LastSeenUnixMs { get; set; }

    /// <summary>
    /// How many indistinguishable listings this record stands for. The API exposes no listing id,
    /// so a seller offering two identical stacks at one price collapses into a single record with
    /// a multiplicity of two. Depth calculations count it properly.
    /// </summary>
    public int Multiplicity { get; set; } = 1;

    /// <summary>True the first time the sniper saw it, so the board can badge it as fresh.</summary>
    public bool FromSniper { get; init; }

    public double UnitPrice => Count > 0 ? Price / Count : Price;

    public long AgeMs(long nowUnixMs) => Math.Max(0, nowUnixMs - ListedAtUnixMs);
}

/// <summary>A completed sale from the transaction tape.</summary>
public readonly struct Sale
{
    public readonly int ItemIndex;
    public readonly int Count;
    public readonly double Price;
    public readonly int SellerIndex;
    public readonly long SoldAtUnixMs;

    public Sale(int itemIndex, int count, double price, int sellerIndex, long soldAtUnixMs)
    {
        ItemIndex = itemIndex;
        Count = count;
        Price = price;
        SellerIndex = sellerIndex;
        SoldAtUnixMs = soldAtUnixMs;
    }

    public double UnitPrice => Count > 0 ? Price / Count : Price;
}

public static class Fingerprint
{
    /// <summary>
    /// Identity for a listing. The API gives out no listing id, so identity is composed from the
    /// fields that are stable across polls: seller, item, stack size and price.
    ///
    /// <c>time_left</c> is deliberately excluded - it counts down between observations, and a
    /// listing time reconstructed from it carries network jitter, so including either would make
    /// the same listing look new on every poll.
    /// </summary>
    public static ulong For(int sellerIndex, int itemIndex, int count, double price)
    {
        // Prices arrive with cents (31899999.99), so quantise before hashing to keep the identity
        // stable against floating-point round-trips.
        long priceKey = (long)Math.Round(price * 100.0);

        ulong h = 1469598103934665603UL;
        h = Mix(h, (ulong)sellerIndex);
        h = Mix(h, (ulong)itemIndex);
        h = Mix(h, (ulong)count);
        h = Mix(h, (ulong)priceKey);
        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return h;
    }
}
