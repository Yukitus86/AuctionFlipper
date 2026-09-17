using System.Text;

namespace AuctionFlipper.Core;

public enum ItemCategory
{
    Commodity,      // ingots, gems, dusts - the bread and butter of bulk flipping
    Block,
    Container,      // shulker boxes and bundles, whose contents the API does expose
    Gear,           // tools, weapons, armour - hidden enchantments make these unpriceable
    Consumable,     // potions, food, arrows
    Decoration,
    Misc,
}

/// <summary>
/// Everything the tool knows about an item id without asking the server.
///
/// The API never fills in <c>display_name</c>, <c>lore</c> or enchantment levels, so the id is the
/// only identity available. That makes one classification critical: whether an item is able to
/// carry hidden NBT. Two listings of <c>minecraft:diamond_sword</c> can differ in value by orders
/// of magnitude and nothing in the payload distinguishes them, so those items are held back from
/// the main board rather than scored as if the id told the whole story.
/// </summary>
public sealed record ItemInfo(
    string Id,
    string DisplayName,
    string SearchText,
    ItemCategory Category,
    bool NbtRisk,
    bool IsContainer,
    double Hue);

public static class ItemCatalog
{
    private static readonly Dictionary<string, ItemInfo> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    // Suffixes and names that can carry enchantments, custom names, potion effects, map data,
    // written pages or firework payloads - anything whose worth is invisible to this API.
    private static readonly string[] GearSuffixes =
    [
        "_sword", "_pickaxe", "_axe", "_shovel", "_hoe", "_spear",
        "_helmet", "_chestplate", "_leggings", "_boots", "_hammer",
    ];

    private static readonly HashSet<string> GearExact = new(StringComparer.Ordinal)
    {
        "bow", "crossbow", "trident", "shield", "elytra", "mace", "fishing_rod",
        "turtle_helmet", "carrot_on_a_stick", "warped_fungus_on_a_stick", "shears", "flint_and_steel",
        "brush", "wolf_armor", "horse_armor", "leather_horse_armor", "iron_horse_armor",
        "golden_horse_armor", "diamond_horse_armor",
    };

    private static readonly HashSet<string> NbtExact = new(StringComparer.Ordinal)
    {
        "enchanted_book", "potion", "splash_potion", "lingering_potion", "tipped_arrow",
        "filled_map", "written_book", "writable_book", "firework_rocket", "firework_star",
        "player_head", "suspicious_stew", "goat_horn", "ominous_bottle", "knowledge_book",
        "spawner", "trial_key", "ominous_trial_key", "enchanted_golden_apple", "bundle",
        "white_banner", "orange_banner", "magenta_banner", "light_blue_banner", "yellow_banner",
        "lime_banner", "pink_banner", "gray_banner", "light_gray_banner", "cyan_banner",
        "purple_banner", "blue_banner", "brown_banner", "green_banner", "red_banner", "black_banner",
    };

    private static readonly HashSet<string> Commodities = new(StringComparer.Ordinal)
    {
        "diamond", "emerald", "netherite_ingot", "netherite_scrap", "iron_ingot", "gold_ingot",
        "copper_ingot", "coal", "charcoal", "redstone", "lapis_lazuli", "quartz", "amethyst_shard",
        "ender_pearl", "blaze_rod", "blaze_powder", "ghast_tear", "nether_star", "echo_shard",
        "prismarine_shard", "prismarine_crystals", "slime_ball", "honeycomb", "gunpowder",
        "glowstone_dust", "bone_meal", "string", "leather", "feather", "flint", "clay_ball",
        "brick", "nether_brick", "sugar", "paper", "book", "stick", "totem_of_undying",
        "experience_bottle", "dragon_breath", "shulker_shell", "phantom_membrane", "rabbit_hide",
        "scute", "turtle_scute", "heart_of_the_sea", "nautilus_shell", "trident_shard",
    };

    private static readonly HashSet<string> Consumables = new(StringComparer.Ordinal)
    {
        "golden_apple", "cooked_beef", "cooked_porkchop", "cooked_chicken", "cooked_mutton",
        "cooked_rabbit", "cooked_cod", "cooked_salmon", "bread", "carrot", "golden_carrot",
        "potato", "baked_potato", "beetroot", "melon_slice", "apple", "pumpkin_pie", "cake",
        "cookie", "honey_bottle", "milk_bucket", "rotten_flesh", "spider_eye",
        "fermented_spider_eye", "arrow", "spectral_arrow", "egg", "snowball",
    };

    public static ItemInfo Get(string id)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(id, out ItemInfo? cached))
                return cached;

            ItemInfo info = Build(id);
            Cache[id] = info;
            return info;
        }
    }

    private static ItemInfo Build(string id)
    {
        string bare = StripNamespace(id);
        string display = Prettify(bare);

        bool isContainer = bare.EndsWith("shulker_box", StringComparison.Ordinal) || bare == "bundle";
        bool gear = GearExact.Contains(bare) || GearSuffixes.Any(s => bare.EndsWith(s, StringComparison.Ordinal));
        bool nbt = gear || NbtExact.Contains(bare);

        ItemCategory category =
            isContainer ? ItemCategory.Container
            : gear ? ItemCategory.Gear
            : Commodities.Contains(bare) ? ItemCategory.Commodity
            : Consumables.Contains(bare) ? ItemCategory.Consumable
            : bare.EndsWith("_block", StringComparison.Ordinal)
              || bare.EndsWith("_ore", StringComparison.Ordinal)
              || bare.EndsWith("_log", StringComparison.Ordinal)
              || bare.EndsWith("_planks", StringComparison.Ordinal)
              || bare.EndsWith("_concrete", StringComparison.Ordinal)
              || bare.EndsWith("_terracotta", StringComparison.Ordinal)
                ? ItemCategory.Block
            : bare.EndsWith("_carpet", StringComparison.Ordinal)
              || bare.EndsWith("_banner", StringComparison.Ordinal)
              || bare.EndsWith("_bed", StringComparison.Ordinal)
              || bare.EndsWith("_sign", StringComparison.Ordinal)
              || bare.Contains("flower", StringComparison.Ordinal)
                ? ItemCategory.Decoration
            : ItemCategory.Misc;

        return new ItemInfo(id, display, display, category, nbt, isContainer, HueFor(bare));
    }

    public static string StripNamespace(string id)
    {
        int colon = id.IndexOf(':');
        return colon >= 0 ? id[(colon + 1)..] : id;
    }

    /// <summary>"minecraft:netherite_ingot" becomes "Netherite Ingot".</summary>
    private static string Prettify(string bare)
    {
        var sb = new StringBuilder(bare.Length + 4);
        bool upper = true;
        foreach (char c in bare)
        {
            if (c == '_') { sb.Append(' '); upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A stable hue per item id. Each item gets its own recognisable colour chip on the board
    /// without shipping or downloading any textures.
    /// </summary>
    private static double HueFor(string bare)
    {
        uint h = 2166136261u;
        foreach (char c in bare)
        {
            h ^= c;
            h *= 16777619u;
        }
        return h % 360u;
    }
}
