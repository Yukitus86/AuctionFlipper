using System.ComponentModel;

namespace AuctionFlipper;

/// <summary>
/// The app's string table, in English and German.
///
/// This is a hand-rolled dictionary rather than satellite resource assemblies, for two reasons.
/// The tool ships as a single self-contained file, and satellite assemblies would either have to be
/// extracted at runtime or embedded through a resource loader that costs more code than the table
/// itself. More importantly, the language has to be switchable while the app runs: the board is a
/// live feed, and restarting it to read a label in another language would throw away the sale
/// history accumulated that session, which is the one thing the tool cannot get back quickly.
///
/// Every lookup goes through the indexer so XAML can bind to it, and a language change raises a
/// single indexer notification that refreshes every bound string at once.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>
    /// The singleton, built by a static constructor rather than a field initializer.
    ///
    /// Static field initializers run in declaration order, and this one is declared above the two
    /// string tables it depends on - so as a field initializer it would construct an instance whose
    /// active table was still null, and every lookup would throw. A static constructor runs after
    /// all of them, which lets the tables stay at the bottom of the file where they belong.
    /// </summary>
    public static Loc Current { get; }

    static Loc() => Current = new Loc();

    private Dictionary<string, string> _active = English;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Two-letter code of the language in use: <c>en</c> or <c>de</c>.</summary>
    public string Language { get; private set; } = "en";

    public bool IsEnglish => Language == "en";
    public bool IsGerman => Language == "de";

    /// <summary>
    /// Looks up a key. A key missing from the active language falls back to English rather than
    /// showing a blank, and a key missing from both shows itself, which makes a typo obvious on
    /// screen instead of silently swallowing the label.
    /// </summary>
    public string this[string key] =>
        _active.TryGetValue(key, out string? value) ? value
        : English.TryGetValue(key, out string? fallback) ? fallback
        : key;

    public static string T(string key) => Current[key];

    public static string T(string key, params object[] args) =>
        string.Format(Current[key], args);

    public void SetLanguage(string code)
    {
        string normalised = code == "de" ? "de" : "en";
        if (normalised == Language) return;

        Language = normalised;
        _active = normalised == "de" ? German : English;

        // "Item[]" is the notification WPF listens for on an indexer binding, so one raise here
        // re-reads every localised string in the window.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGerman)));

        LanguageChanged?.Invoke();
    }

    /// <summary>Raised after a language switch, for view models holding strings they built themselves.</summary>
    public event Action? LanguageChanged;

    // ---------------------------------------------------------------- audit
    //
    // A missing translation does not fail - it silently falls back to English, which is the right
    // behaviour on screen and the wrong behaviour in a build. These expose the gap so the offline
    // checks can catch a half-translated table before it ships.

    public static int KeyCount => English.Count;

    /// <summary>Keys the English table has and the German one does not.</summary>
    public static string[] UntranslatedKeys() =>
        English.Keys.Where(k => !German.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>German keys with no English original, which is always a typo in one of the two.</summary>
    public static string[] OrphanKeys() =>
        German.Keys.Where(k => !English.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Keys whose two versions disagree about their placeholders.
    ///
    /// A translation that drops a <c>{0}</c> loses a number the sentence was built around; one that
    /// invents a <c>{2}</c> throws FormatException at the moment it is shown, which on a status bar
    /// means a crash in front of the user. Counting the highest index in each is enough to catch both.
    /// </summary>
    public static string[] PlaceholderMismatches() =>
        English.Keys
            .Where(k => German.ContainsKey(k) && HighestPlaceholder(English[k]) != HighestPlaceholder(German[k]))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

    private static int HighestPlaceholder(string text)
    {
        int highest = -1;
        for (int i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] != '{' || !char.IsDigit(text[i + 1]) || text[i + 2] != '}') continue;
            highest = Math.Max(highest, text[i + 1] - '0');
        }
        return highest;
    }

    // ---------------------------------------------------------------- English

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // header
        ["HeaderBudget"] = "REQUEST BUDGET",
        ["HeaderBook"] = "ORDER BOOK",
        ["HeaderTape"] = "SALE TAPE",
        ["HeaderListings"] = "NEW LISTINGS",
        ["HeaderUptime"] = "UPTIME",
        ["Pause"] = "Pause",
        ["Resume"] = "Resume",
        ["PauseTip"] = "Stop every collector, so the tool makes no API requests at all",

        // navigation
        ["NavMarket"] = "MARKET",
        ["NavBoard"] = "Flip board",
        ["NavPinned"] = "Pinned",
        ["NavData"] = "DATA",
        ["NavTape"] = "Sale tape",
        ["NavItems"] = "Item prices",
        ["NavSetup"] = "SETUP",
        ["NavSettings"] = "Settings",

        // board
        ["BoardTitle"] = "Flip board",
        ["BoardSubtitle"] = "Every listing priced below what the item has actually been selling for, "
                            + "ranked by risk-adjusted profit per hour.",
        ["PinnedTitle"] = "Pinned items",
        ["PinnedSubtitle"] = "Only the items you are working. Board filters are relaxed here on purpose, "
                             + "so a pinned item still shows up when its edge is thin.",
        ["PinnedEmpty"] = "Nothing pinned yet. Click the star on a row to keep an item here.",

        ["Sort"] = "SORT",
        ["SortProfitPerHour"] = "Profit / hour",
        ["SortNetProfit"] = "Net profit",
        ["SortRoi"] = "ROI",
        ["SortFastest"] = "Sells fastest",
        ["SortNewest"] = "Newest",
        ["Prices"] = "PRICES",
        ["PerLot"] = "Per lot",
        ["PerItem"] = "Per item",
        ["PerLotTip"] = "Show what the whole listing costs and earns - this is what you actually pay",
        ["PerItemTip"] = "Show the price of a single item, so lots of different sizes compare directly",
        ["FilterByName"] = "filter by item name",
        ["FilterBoardTip"] = "Filter the board by item name",
        ["Matching"] = "{0} matching",
        ["MatchingOf"] = "{0} of {1} matching",

        // row columns
        ["ColBuy"] = "BUY",
        ["ColBuyUnit"] = "BUY / ITEM",
        ["ColRelist"] = "RELIST AT",
        ["ColRelistUnit"] = "RELIST / ITEM",
        ["ColNet"] = "NET PROFIT",
        ["ColNetUnit"] = "NET / ITEM",
        ["ColRoi"] = "ROI",
        ["ColSoldPerHour"] = "SOLD / H",
        ["ColConfidence"] = "CONFIDENCE",
        ["PinTip"] = "Pin this item so it stays on your Pinned tab",
        ["UnpinTip"] = "Stop tracking this item on the Pinned tab",
        ["LotSuffix"] = "{0} lot",
        ["EachSuffix"] = "{0} ea",

        // hover card
        ["TipFairValue"] = "Fair value",
        ["TipEach"] = "each",
        ["TipSpread"] = "Spread",
        ["TipDemand"] = "Demand",
        ["TipClears"] = "Clears in",
        ["TipSeller"] = "Seller",
        ["TipListed"] = "Listed",
        ["TipAgo"] = "{0} ago",
        ["TipConfidence"] = "Confidence",
        ["TipWatchOut"] = "WATCH OUT FOR",
        ["TipClean"] = "Nothing against this one - clean read on a liquid item.",
        ["TipClickHint"] = "Click the row for the full order book - click the star to pin it",

        // detail panel
        ["DetailEmpty"] = "Select a flip to see its order book, price history and why it scored the way it did.",
        ["CopySearch"] = "Copy /ah search",
        ["CopySearchTip"] = "Copies the item name so it can be pasted straight into /ah in game",
        ["RefreshItem"] = "Refresh item",
        ["RefreshItemTip"] = "Spends reserved budget to re-read this item's cheapest listings",
        ["PriceHistory"] = "PRICE HISTORY (24 H OF SALES)",
        ["AskLadder"] = "ASK LADDER - CHEAPEST LISTINGS",
        ["Contents"] = "CONTENTS",
        ["WhyThisScore"] = "WHY THIS SCORE",
        ["DetailSummary"] = "Fair {0} each ({1}) - {2} sales seen, {3} - {4} asks known",
        ["NoPenalties"] = "No penalties applied - this is a clean read on a liquid item.",
        ["ResizeTip"] = "Drag to resize the detail panel",

        // sale tape
        ["TapeTitle"] = "Sale tape",
        ["TapeSubtitle"] = "Every sale the tool has captured, newest first. The server only keeps about two "
                           + "minutes of history, so this feed is the only place longer-term prices come from - "
                           + "which is why leaving the tool running makes its valuations better.",
        ["TapeUnitTip"] = "Price per item",

        // item prices
        ["ItemsTitle"] = "Item prices",
        ["ItemsSubtitle"] = "What the tool believes each item is worth, and where that belief came from. Values "
                            + "marked from asks have not been seen to trade yet and are the least reliable; values "
                            + "from sales are what the flip board actually trusts.",
        ["ColItem"] = "ITEM",
        ["ColValueEach"] = "VALUE EACH",
        ["ColLowestAsk"] = "LOWEST ASK",
        ["ColSalesSeen"] = "SALES SEEN",
        ["ColAsks"] = "ASKS",
        ["ColSource"] = "SOURCE",
        ["NbtTip"] = "Can carry enchantments the API does not expose",

        // settings
        ["SettingsTitle"] = "Settings",
        ["SecConnection"] = "CONNECTION",
        ["ConnectionHint"] = "Generate a key in game with /api. It is stored in your Windows app-data folder, "
                             + "never in the project, and is never sent anywhere except api.donutsmp.net. "
                             + "It is masked here; the eye beside it reveals it.",
        ["ApiKey"] = "API key",
        ["ShowApiKey"] = "Show the key",
        ["HideApiKey"] = "Hide the key",

        ["SecLanguage"] = "LANGUAGE",
        ["LanguageHint"] = "Changes apply immediately and are remembered. Item names come from the server and "
                           + "stay in English, because that is what you have to type after /ah in game.",
        ["LangEnglish"] = "English",
        ["LangGerman"] = "Deutsch",

        ["SecMarket"] = "YOUR MARKET",
        ["MarketHint"] = "Profit is calculated after tax. DonutSMP currently takes nothing when a listing sells, "
                         + "so this is left at zero - correct it here if that ever changes.",
        ["SaleTax"] = "Sale tax",
        ["SaleTaxHint"] = "percent taken when a listing sells",
        ["Capital"] = "Capital",
        ["CapitalHint"] = "coins you can spend; dearer flips are flagged over budget",
        ["ListingSlots"] = "Listing slots",
        ["ListingSlotsHint"] = "listings you can hold at once; makes profit per hour the ranking that matters",
        ["ContainerRecovery"] = "Container recovery",
        ["ContainerRecoveryHint"] = "percent of a shulker box's contents value you expect to actually recover",

        ["SecFilters"] = "WHAT REACHES THE BOARD",
        ["FiltersHint"] = "Raising the confidence floor is the single most useful filter here: it removes flips "
                          + "whose value is guessed rather than observed.",
        ["MinProfit"] = "Minimum profit",
        ["Coins"] = "coins",
        ["MinRoi"] = "Minimum ROI",
        ["MinRoiHint"] = "percent return on the buy price",
        ["MinConfidence"] = "Minimum confidence",
        ["ZeroToHundred"] = "0 to 100",
        ["MinDemand"] = "Minimum demand",
        ["MinDemandHint"] = "sales per hour the item must be doing",
        ["MaxBuy"] = "Maximum buy price",
        ["NoLimit"] = "0 for no limit",
        ["IncludeContainers"] = "Include shulker boxes and bundles on the board",
        ["NbtHint"] = "Anything that can carry hidden enchantments is never shown. The API returns no "
                      + "enchantment, lore or custom-name data at all, so a cheap diamond sword may be plain or "
                      + "may be worth a hundred times the asking price, and nothing in the response can tell you "
                      + "which. There is no analysis to do on a coin flip, so those listings are dropped rather "
                      + "than ranked.",

        ["SecAlerts"] = "ALERTS",
        ["AlertSound"] = "Play a sound",
        ["AlertToast"] = "Show a desktop notification",
        ["AlertCopy"] = "Copy the item name to the clipboard automatically",
        ["AlertCopyHint"] = "With auto-copy on, an alert leaves the item name ready to paste straight into /ah "
                            + "in game, which is most of the race won.",
        ["AlertFromGrade"] = "Alert from grade",
        ["GradeSOnly"] = "S only",
        ["GradeAUp"] = "A and up",
        ["GradeBUp"] = "B and up",
        ["GradeEverything"] = "Everything",
        ["AlertAbove"] = "Alert above",
        ["AlertAboveHint"] = "coins of net profit",
        ["Cooldown"] = "Cooldown",
        ["CooldownHint"] = "seconds before the same item may alert again",
        ["TestSound"] = "Test the alert sound",
        ["AlertPinned"] = "Always alert for pinned items",
        ["AlertPinnedHint"] = "A pinned item skips the grade and profit thresholds, on the grounds that you "
                              + "pinned it because you want to know.",

        ["SecBudget"] = "REQUEST BUDGET",
        ["BudgetHint"] = "The API allows 250 requests a minute per key. The tool holds itself below that on a "
                         + "rolling sixty-second window rather than a per-minute counter, so it cannot spike "
                         + "across a boundary. Each collector has a guaranteed share; the book scan runs on "
                         + "whatever is left.",
        ["Ceiling"] = "Ceiling",
        ["CeilingHint"] = "requests per minute; keep a margin under 250",
        ["NewListings"] = "New listings",
        ["NewListingsHint"] = "guaranteed share for the feed that finds the flips",
        ["TapeShare"] = "Sale tape",
        ["TapeShareHint"] = "small, but never starved - it is where prices come from",
        ["YourClicks"] = "Your clicks",
        ["YourClicksHint"] = "held back so manual refreshes never queue behind the collectors",
        ["SweeperEnabled"] = "Scan the standing order book with leftover budget",

        ["SecStorage"] = "STORAGE",
        ["KeepHistory"] = "Keep sale history",
        ["KeepHistoryHint"] = "days of recorded sales to keep on disk",
        ["SaveSettings"] = "Save settings",

        // status messages
        ["StatusStarting"] = "Starting collectors...",
        ["StatusNoKey"] = "No API key yet. Generate one in game with /api, paste it below and save.",
        ["StatusStopping"] = "Stopping collectors...",
        ["StatusPaused"] = "Paused - no requests are being made.",
        ["StatusResumed"] = "Collectors resumed.",
        ["StatusSaved"] = "Settings saved to {0}",
        ["StatusRefreshing"] = "Refreshing {0}...",
        ["StatusRefreshed"] = "Refreshed {0}: {1} new listing(s).",
        ["StatusRefreshFailed"] = "Refresh failed: {0}",
        ["StatusLatency"] = "{0} ms average response",
        ["StatusRestored"] = "Restored {0} sales and {1} standing listings from previous sessions.",
        ["AlreadyRunning"] = "Auction Flipper is already running. Two copies share one API key and one "
                             + "settings file, so the second one is closing.",
        ["WarmStart"] = "Remember the order book between runs",
        ["WarmStartHint"] = "Writes what is currently for sale to disk every few minutes and reads it back at "
                            + "the next launch, so the board has prices from the first second instead of after "
                            + "a quarter of an hour of scanning. Anything restored is badged UNCHECKED until a "
                            + "collector sees it live again, and a save older than six hours is ignored.",
        ["StatusDropped"] = "Dropped {0} listing(s) that are no longer on sale.",
        ["StatusRateLimited"] = "Rate limited by the server - backing off.",
        ["StoragePath"] = "Sale history: {0} MB in {1}",
        ["ConfigPath"] = "Stored in {0}",

        ["TapeWaiting"] = "waiting for sales...",
        ["TapeCatchingUp"] = " - catching up",
        ["TapeCount"] = "{0} sales, {1}/s",
        ["BookCount"] = "{0} listings / {1} items",
        ["SniperRate"] = "{0}/min, polling every {1}",
        ["ScanProgress"] = "book scan {0} ({1} of {2} pages)",
        ["ScanPaused"] = "book scan paused",

        // value sources and time phrasing
        ["FromSales"] = "from sales",
        ["FromAsks"] = "from asks",
        ["SourceUnknown"] = "unknown",
        ["Instant"] = "instant",

        // row badges - short, shouty, and the first thing read on a busy board
        ["BadgeNbt"] = "NBT?",
        ["BadgeBox"] = "BOX",
        ["BadgeTrap"] = "TRAP?",
        ["BadgeThin"] = "THIN",
        ["BadgeStale"] = "STALE",
        ["BadgeSwingy"] = "SWINGY",
        ["BadgeOneSeller"] = "1 SELLER",
        ["BadgeNoSales"] = "NO SALES",
        ["BadgeRising"] = "RISING",
        ["BadgeFalling"] = "FALLING",
        ["BadgeUnverified"] = "UNCHECKED",
        ["BadgeOverBudget"] = "OVER BUDGET",

        // item categories
        ["CategoryCommodity"] = "Commodity",
        ["CategoryBlock"] = "Block",
        ["CategoryContainer"] = "Container",
        ["CategoryGear"] = "Gear",
        ["CategoryConsumable"] = "Consumable",
        ["CategoryDecoration"] = "Decoration",
        ["CategoryOther"] = "Other",

        // scoring notes
        ["NoteNoSales"] = "No sales observed yet - value is inferred from the ask ladder.",
        ["NoteFewSales"] = "Only {0} sale(s) observed for this item.",
        ["NoteDispersion"] = "Sale prices scatter by {0} around the median.",
        ["NoteThinBook"] = "Only {0} listing(s) sit near the target price.",
        ["NoteNbt"] = "Enchantments and custom data are not exposed by the API, so two listings of this item "
                      + "can be worth wildly different amounts.",
        ["NoteSellerWall"] = "This seller holds {0} of the 10 cheapest listings, so they are setting this price, "
                             + "not the market.",
        ["NoteDeepDiscount"] = "Listed {0} below value. A discount that big usually means the item is not what "
                               + "its id suggests.",
        ["NoteDiscount"] = "Listed {0} below value - treat with suspicion.",
        ["NoteAbsurdRoi"] = "A {0} return is not credible - the reference price for this item is probably wrong.",
        ["NoteStale"] = "Has been listed for {0} h without selling.",
        ["NoteIlliquid"] = "This item trades rarely, so the flip could sit unsold for a long time.",
        ["NoteTrendDown"] = "Price is trending down {0}.",
        ["NoteOverBudget"] = "Costs more than the capital set in Settings.",
        ["NoteUnvalued"] = "{0} item(s) inside could not be valued and were counted as worthless.",
        ["NoteSlots"] = "Selling the contents needs roughly {0} of your {1} listing slots.",
        ["NoteSlotsShort"] = "That is more distinct items than you can list at once, so it will take several rounds.",
        ["NoteAbsurdRoiBox"] = "A {0} return is not credible - one of the contents is probably valued wrongly.",
        ["NoteDominant"] = "{0} of the value is a single item, so this is really a bet on that one price.",
        ["NoteUnverified"] = "Restored from the last session and not seen live yet - it may already have been "
                             + "bought. Check it in game before counting on it.",
        ["NoteContentsValue"] = "Contents value {0}; recoverable after undercuts {1}.",
    };

    // ---------------------------------------------------------------- German

    private static readonly Dictionary<string, string> German = new(StringComparer.Ordinal)
    {
        // header
        ["HeaderBudget"] = "ANFRAGE-BUDGET",
        ["HeaderBook"] = "ORDERBUCH",
        ["HeaderTape"] = "VERKAUFSTICKER",
        ["HeaderListings"] = "NEUE ANGEBOTE",
        ["HeaderUptime"] = "LAUFZEIT",
        ["Pause"] = "Pause",
        ["Resume"] = "Fortsetzen",
        ["PauseTip"] = "Hält alle Sammler an, damit das Tool überhaupt keine API-Anfragen mehr stellt",

        // navigation
        ["NavMarket"] = "MARKT",
        ["NavBoard"] = "Flip-Board",
        ["NavPinned"] = "Angeheftet",
        ["NavData"] = "DATEN",
        ["NavTape"] = "Verkaufsticker",
        ["NavItems"] = "Item-Preise",
        ["NavSetup"] = "EINRICHTUNG",
        ["NavSettings"] = "Einstellungen",

        // board
        ["BoardTitle"] = "Flip-Board",
        ["BoardSubtitle"] = "Jedes Angebot, das unter dem Preis liegt, zu dem das Item tatsächlich verkauft "
                            + "wird - sortiert nach risikobereinigtem Gewinn pro Stunde.",
        ["PinnedTitle"] = "Angeheftete Items",
        ["PinnedSubtitle"] = "Nur die Items, an denen du gerade arbeitest. Die Board-Filter sind hier bewusst "
                             + "gelockert, damit ein angeheftetes Item auch mit dünner Marge sichtbar bleibt.",
        ["PinnedEmpty"] = "Noch nichts angeheftet. Klicke den Stern in einer Zeile, um ein Item hier zu behalten.",

        ["Sort"] = "SORTIERUNG",
        ["SortProfitPerHour"] = "Gewinn / Std.",
        ["SortNetProfit"] = "Nettogewinn",
        ["SortRoi"] = "Rendite",
        ["SortFastest"] = "Schnellster Verkauf",
        ["SortNewest"] = "Neueste",
        ["Prices"] = "PREISE",
        ["PerLot"] = "Pro Stapel",
        ["PerItem"] = "Pro Item",
        ["PerLotTip"] = "Zeigt, was das gesamte Angebot kostet und einbringt - das ist der Betrag, den du "
                        + "wirklich zahlst",
        ["PerItemTip"] = "Zeigt den Preis eines einzelnen Items, damit sich unterschiedlich große Stapel "
                         + "direkt vergleichen lassen",
        ["FilterByName"] = "nach Item-Namen filtern",
        ["FilterBoardTip"] = "Board nach Item-Namen filtern",
        ["Matching"] = "{0} Treffer",
        ["MatchingOf"] = "{0} von {1} Treffern",

        // row columns
        ["ColBuy"] = "KAUF",
        ["ColBuyUnit"] = "KAUF / ITEM",
        ["ColRelist"] = "NEU AB",
        ["ColRelistUnit"] = "NEU AB / ITEM",
        ["ColNet"] = "NETTOGEWINN",
        ["ColNetUnit"] = "NETTO / ITEM",
        ["ColRoi"] = "RENDITE",
        ["ColSoldPerHour"] = "VERK. / STD",
        ["ColConfidence"] = "VERTRAUEN",
        ["PinTip"] = "Item anheften, damit es im Tab \"Angeheftet\" bleibt",
        ["UnpinTip"] = "Item nicht mehr im Tab \"Angeheftet\" verfolgen",
        ["LotSuffix"] = "{0} Stapel",
        ["EachSuffix"] = "{0} St.",

        // hover card
        ["TipFairValue"] = "Fairer Wert",
        ["TipEach"] = "pro Stück",
        ["TipSpread"] = "Spanne",
        ["TipDemand"] = "Nachfrage",
        ["TipClears"] = "Abverkauf in",
        ["TipSeller"] = "Verkäufer",
        ["TipListed"] = "Eingestellt",
        ["TipAgo"] = "vor {0}",
        ["TipConfidence"] = "Vertrauen",
        ["TipWatchOut"] = "DARAUF ACHTEN",
        ["TipClean"] = "Nichts auszusetzen - saubere Datenlage bei einem liquiden Item.",
        ["TipClickHint"] = "Zeile anklicken für das ganze Orderbuch - Stern anklicken zum Anheften",

        // detail panel
        ["DetailEmpty"] = "Wähle einen Flip aus, um Orderbuch, Preisverlauf und die Begründung der Bewertung zu sehen.",
        ["CopySearch"] = "/ah-Suche kopieren",
        ["CopySearchTip"] = "Kopiert den Item-Namen, damit er direkt hinter /ah eingefügt werden kann",
        ["RefreshItem"] = "Item aktualisieren",
        ["RefreshItemTip"] = "Nutzt reserviertes Budget, um die günstigsten Angebote dieses Items neu zu lesen",
        ["PriceHistory"] = "PREISVERLAUF (24 STD. VERKÄUFE)",
        ["AskLadder"] = "ANGEBOTSLEITER - GÜNSTIGSTE ANGEBOTE",
        ["Contents"] = "INHALT",
        ["WhyThisScore"] = "WARUM DIESE BEWERTUNG",
        ["DetailSummary"] = "Fair {0} pro Stück ({1}) - {2} Verkäufe gesehen, {3} - {4} Angebote bekannt",
        ["NoPenalties"] = "Keine Abzüge - saubere Datenlage bei einem liquiden Item.",
        ["ResizeTip"] = "Ziehen, um die Detailspalte zu verbreitern oder zu verschmälern",

        // sale tape
        ["TapeTitle"] = "Verkaufsticker",
        ["TapeSubtitle"] = "Jeder Verkauf, den das Tool erfasst hat, neueste zuerst. Der Server hält nur etwa "
                           + "zwei Minuten Historie vor, deshalb ist dieser Ticker die einzige Quelle für "
                           + "längerfristige Preise - und deshalb werden die Bewertungen besser, je länger das "
                           + "Tool läuft.",
        ["TapeUnitTip"] = "Preis pro Item",

        // item prices
        ["ItemsTitle"] = "Item-Preise",
        ["ItemsSubtitle"] = "Was das Tool für den Wert jedes Items hält und woher diese Einschätzung stammt. "
                            + "Werte aus Angeboten wurden noch nie gehandelt und sind am unsichersten; Werte aus "
                            + "Verkäufen sind die, denen das Flip-Board wirklich vertraut.",
        ["ColItem"] = "ITEM",
        ["ColValueEach"] = "WERT / STÜCK",
        ["ColLowestAsk"] = "GÜNSTIGSTES",
        ["ColSalesSeen"] = "VERKÄUFE",
        ["ColAsks"] = "ANGEBOTE",
        ["ColSource"] = "QUELLE",
        ["NbtTip"] = "Kann Verzauberungen tragen, die die API nicht ausliefert",

        // settings
        ["SettingsTitle"] = "Einstellungen",
        ["SecConnection"] = "VERBINDUNG",
        ["ConnectionHint"] = "Erzeuge im Spiel mit /api einen Schlüssel. Er wird in deinem Windows-App-Data-Ordner "
                             + "gespeichert, niemals im Projekt, und geht an niemanden außer api.donutsmp.net. "
                             + "Hier ist er verdeckt; das Auge daneben zeigt ihn.",
        ["ApiKey"] = "API-Schlüssel",
        ["ShowApiKey"] = "Schlüssel anzeigen",
        ["HideApiKey"] = "Schlüssel verbergen",

        ["SecLanguage"] = "SPRACHE",
        ["LanguageHint"] = "Änderungen gelten sofort und werden gespeichert. Item-Namen kommen vom Server und "
                           + "bleiben englisch, weil genau das im Spiel hinter /ah getippt werden muss.",
        ["LangEnglish"] = "English",
        ["LangGerman"] = "Deutsch",

        ["SecMarket"] = "DEIN MARKT",
        ["MarketHint"] = "Der Gewinn wird nach Steuer berechnet. DonutSMP nimmt beim Verkauf derzeit nichts, "
                         + "deshalb steht hier null - ändere es hier, falls sich das je ändert.",
        ["SaleTax"] = "Verkaufssteuer",
        ["SaleTaxHint"] = "Prozent, die beim Verkauf einbehalten werden",
        ["Capital"] = "Kapital",
        ["CapitalHint"] = "Coins, die du ausgeben kannst; teurere Flips werden als über Budget markiert",
        ["ListingSlots"] = "Angebots-Slots",
        ["ListingSlotsHint"] = "Angebote, die du gleichzeitig halten kannst; macht Gewinn pro Stunde zur "
                               + "entscheidenden Sortierung",
        ["ContainerRecovery"] = "Container-Ausbeute",
        ["ContainerRecoveryHint"] = "Prozent des Inhaltswerts einer Shulkerbox, die du realistisch wieder erlöst",

        ["SecFilters"] = "WAS AUFS BOARD KOMMT",
        ["FiltersHint"] = "Die Vertrauensschwelle anzuheben ist hier der nützlichste Filter: er entfernt Flips, "
                          + "deren Wert geraten statt beobachtet ist.",
        ["MinProfit"] = "Mindestgewinn",
        ["Coins"] = "Coins",
        ["MinRoi"] = "Mindestrendite",
        ["MinRoiHint"] = "Prozent Rendite auf den Kaufpreis",
        ["MinConfidence"] = "Mindestvertrauen",
        ["ZeroToHundred"] = "0 bis 100",
        ["MinDemand"] = "Mindestnachfrage",
        ["MinDemandHint"] = "Verkäufe pro Stunde, die das Item schaffen muss",
        ["MaxBuy"] = "Maximaler Kaufpreis",
        ["NoLimit"] = "0 für kein Limit",
        ["IncludeContainers"] = "Shulkerboxen und Bündel aufs Board lassen",
        ["NbtHint"] = "Alles, was versteckte Verzauberungen tragen kann, wird nie angezeigt. Die API liefert "
                      + "überhaupt keine Verzauberungs-, Lore- oder Namensdaten, also kann ein billiges "
                      + "Diamantschwert schlicht sein oder das Hundertfache wert - und nichts in der Antwort "
                      + "verrät, was davon zutrifft. An einem Münzwurf gibt es nichts zu analysieren, deshalb "
                      + "fliegen diese Angebote raus statt bewertet zu werden.",

        ["SecAlerts"] = "BENACHRICHTIGUNGEN",
        ["AlertSound"] = "Ton abspielen",
        ["AlertToast"] = "Desktop-Benachrichtigung zeigen",
        ["AlertCopy"] = "Item-Namen automatisch in die Zwischenablage kopieren",
        ["AlertCopyHint"] = "Mit automatischem Kopieren liegt der Item-Name nach einer Meldung bereit, um ihn "
                            + "direkt hinter /ah einzufügen - damit ist das Rennen halb gewonnen.",
        ["AlertFromGrade"] = "Ab Bewertung melden",
        ["GradeSOnly"] = "Nur S",
        ["GradeAUp"] = "Ab A",
        ["GradeBUp"] = "Ab B",
        ["GradeEverything"] = "Alles",
        ["AlertAbove"] = "Melden ab",
        ["AlertAboveHint"] = "Coins Nettogewinn",
        ["Cooldown"] = "Sperrzeit",
        ["CooldownHint"] = "Sekunden, bevor dasselbe Item erneut melden darf",
        ["TestSound"] = "Meldeton testen",
        ["AlertPinned"] = "Für angeheftete Items immer melden",
        ["AlertPinnedHint"] = "Ein angeheftetes Item überspringt die Schwellen für Bewertung und Gewinn - du "
                              + "hast es schließlich angeheftet, weil du es wissen willst.",

        ["SecBudget"] = "ANFRAGE-BUDGET",
        ["BudgetHint"] = "Die API erlaubt 250 Anfragen pro Minute und Schlüssel. Das Tool bleibt darunter, "
                         + "gemessen über ein gleitendes 60-Sekunden-Fenster statt über einen Minutenzähler, "
                         + "sodass es nicht über eine Minutengrenze hinweg ausschlagen kann. Jeder Sammler hat "
                         + "einen garantierten Anteil; der Buchscan läuft auf dem Rest.",
        ["Ceiling"] = "Obergrenze",
        ["CeilingHint"] = "Anfragen pro Minute; halte Abstand zu 250",
        ["NewListings"] = "Neue Angebote",
        ["NewListingsHint"] = "garantierter Anteil für den Feed, der die Flips findet",
        ["TapeShare"] = "Verkaufsticker",
        ["TapeShareHint"] = "klein, aber nie ausgehungert - hier kommen die Preise her",
        ["YourClicks"] = "Deine Klicks",
        ["YourClicksHint"] = "zurückgehalten, damit manuelle Aktualisierungen nie hinter den Sammlern warten",
        ["SweeperEnabled"] = "Das stehende Orderbuch mit dem Restbudget scannen",

        ["SecStorage"] = "SPEICHER",
        ["KeepHistory"] = "Verkaufshistorie behalten",
        ["KeepHistoryHint"] = "Tage aufgezeichneter Verkäufe, die auf der Platte bleiben",
        ["SaveSettings"] = "Einstellungen speichern",

        // status messages
        ["StatusStarting"] = "Sammler werden gestartet...",
        ["StatusNoKey"] = "Noch kein API-Schlüssel. Erzeuge im Spiel einen mit /api, füge ihn unten ein und speichere.",
        ["StatusStopping"] = "Sammler werden angehalten...",
        ["StatusPaused"] = "Pausiert - es werden keine Anfragen gestellt.",
        ["StatusResumed"] = "Sammler laufen wieder.",
        ["StatusSaved"] = "Einstellungen gespeichert in {0}",
        ["StatusRefreshing"] = "{0} wird aktualisiert...",
        ["StatusRefreshed"] = "{0} aktualisiert: {1} neue Angebote.",
        ["StatusRefreshFailed"] = "Aktualisierung fehlgeschlagen: {0}",
        ["StatusLatency"] = "{0} ms mittlere Antwortzeit",
        ["StatusRestored"] = "{0} Verkäufe und {1} laufende Angebote aus früheren Sitzungen geladen.",
        ["AlreadyRunning"] = "Auction Flipper läuft bereits. Zwei Kopien teilen sich einen API-Schlüssel "
                             + "und eine Einstellungsdatei, deshalb wird die zweite geschlossen.",
        ["WarmStart"] = "Orderbuch zwischen Starts merken",
        ["WarmStartHint"] = "Schreibt alle paar Minuten auf die Festplatte, was gerade angeboten wird, und lädt "
                            + "es beim nächsten Start zurück - das Board hat damit ab der ersten Sekunde Preise "
                            + "statt erst nach einer Viertelstunde Scannen. Wiederhergestellte Angebote tragen "
                            + "das Abzeichen UNGEPRÜFT, bis ein Sammler sie wieder live sieht; ein Stand, der "
                            + "älter als sechs Stunden ist, wird verworfen.",
        ["StatusDropped"] = "{0} Angebote entfernt, die nicht mehr im Verkauf sind.",
        ["StatusRateLimited"] = "Vom Server gedrosselt - das Tool nimmt sich zurück.",
        ["StoragePath"] = "Verkaufshistorie: {0} MB in {1}",
        ["ConfigPath"] = "Gespeichert in {0}",

        ["TapeWaiting"] = "warte auf Verkäufe...",
        ["TapeCatchingUp"] = " - holt auf",
        ["TapeCount"] = "{0} Verkäufe, {1}/s",
        ["BookCount"] = "{0} Angebote / {1} Items",
        ["SniperRate"] = "{0}/Min., Abfrage alle {1}",
        ["ScanProgress"] = "Buchscan {0} ({1} von {2} Seiten)",
        ["ScanPaused"] = "Buchscan pausiert",

        // value sources and time phrasing
        ["FromSales"] = "aus Verkäufen",
        ["FromAsks"] = "aus Angeboten",
        ["SourceUnknown"] = "unbekannt",
        ["Instant"] = "sofort",

        // row badges - short, shouty, and the first thing read on a busy board
        ["BadgeNbt"] = "NBT?",
        ["BadgeBox"] = "KISTE",
        ["BadgeTrap"] = "FALLE?",
        ["BadgeThin"] = "DÜNN",
        ["BadgeStale"] = "ALT",
        ["BadgeSwingy"] = "SCHWANKT",
        ["BadgeOneSeller"] = "1 VERKÄUFER",
        ["BadgeNoSales"] = "KEINE VERKÄUFE",
        ["BadgeRising"] = "STEIGT",
        ["BadgeFalling"] = "FÄLLT",
        ["BadgeUnverified"] = "UNGEPRÜFT",
        ["BadgeOverBudget"] = "ÜBER BUDGET",

        // item categories
        ["CategoryCommodity"] = "Rohstoff",
        ["CategoryBlock"] = "Block",
        ["CategoryContainer"] = "Behälter",
        ["CategoryGear"] = "Ausrüstung",
        ["CategoryConsumable"] = "Verbrauchsgut",
        ["CategoryDecoration"] = "Dekoration",
        ["CategoryOther"] = "Sonstiges",

        // scoring notes
        ["NoteNoSales"] = "Noch keine Verkäufe beobachtet - der Wert stammt aus der Angebotsleiter.",
        ["NoteFewSales"] = "Nur {0} Verkauf/Verkäufe für dieses Item beobachtet.",
        ["NoteDispersion"] = "Die Verkaufspreise streuen um {0} um den Median.",
        ["NoteThinBook"] = "Nur {0} Angebot(e) liegen nahe am Zielpreis.",
        ["NoteNbt"] = "Verzauberungen und Zusatzdaten liefert die API nicht aus, deshalb können zwei Angebote "
                      + "desselben Items völlig unterschiedlich viel wert sein.",
        ["NoteSellerWall"] = "Dieser Verkäufer hält {0} der 10 günstigsten Angebote und setzt damit den Preis - "
                             + "nicht der Markt.",
        ["NoteDeepDiscount"] = "{0} unter Wert eingestellt. Ein so großer Abschlag heißt meist, dass das Item "
                               + "nicht das ist, was seine ID vermuten lässt.",
        ["NoteDiscount"] = "{0} unter Wert eingestellt - mit Vorsicht behandeln.",
        ["NoteAbsurdRoi"] = "Eine Rendite von {0} ist unglaubwürdig - der Referenzpreis für dieses Item ist "
                            + "wahrscheinlich falsch.",
        ["NoteStale"] = "Steht seit {0} Std. ohne Verkauf im Angebot.",
        ["NoteIlliquid"] = "Dieses Item wird selten gehandelt, der Flip könnte also lange unverkauft liegen.",
        ["NoteTrendDown"] = "Der Preis fällt um {0}.",
        ["NoteOverBudget"] = "Kostet mehr als das in den Einstellungen hinterlegte Kapital.",
        ["NoteUnvalued"] = "{0} enthaltene Item(s) konnten nicht bewertet werden und zählen als wertlos.",
        ["NoteSlots"] = "Der Inhalt braucht zum Verkaufen rund {0} deiner {1} Angebots-Slots.",
        ["NoteSlotsShort"] = "Das sind mehr verschiedene Items, als du gleichzeitig einstellen kannst - es "
                             + "wird also mehrere Runden dauern.",
        ["NoteAbsurdRoiBox"] = "Eine Rendite von {0} ist unglaubwürdig - einer der Inhalte ist wahrscheinlich "
                               + "falsch bewertet.",
        ["NoteDominant"] = "{0} des Werts steckt in einem einzigen Item - das hier ist in Wahrheit eine Wette "
                           + "auf genau diesen Preis.",
        ["NoteUnverified"] = "Aus der letzten Sitzung wiederhergestellt und noch nicht live gesehen - kann "
                             + "bereits gekauft sein. Vor dem Zuschlagen im Spiel prüfen.",
        ["NoteContentsValue"] = "Inhaltswert {0}; nach Unterbieten erzielbar {1}.",
    };
}
