using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AuctionFlipper.Core;
using AuctionFlipper.Services;
using AuctionFlipper.Ui.Controls;

namespace AuctionFlipper.Ui.ViewModels;

public enum NavSection { Board, Pinned, Tape, Items, Settings }

public enum BoardSort { ProfitPerHour, NetProfit, Roi, Fastest, Newest }

public sealed class TapeRowVm
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string Monogram { get; init; }
    public required double Hue { get; init; }
    public required string CountText { get; init; }
    public required string PriceText { get; init; }
    public required string UnitText { get; init; }
    public required string AgoText { get; init; }
    public required string VersusValue { get; init; }
    public required bool AboveValue { get; init; }
    public required bool HasValue { get; init; }
}

public sealed class ItemRowVm
{
    public required string ItemId { get; init; }
    public required string ItemName { get; init; }
    public required string Monogram { get; init; }
    public required double Hue { get; init; }
    public required string ValueText { get; init; }
    public required string SourceText { get; init; }
    public required string SalesText { get; init; }
    public required string AsksText { get; init; }
    public required string LowestText { get; init; }
    public required string CategoryText { get; init; }
    public required bool NbtRisk { get; init; }
}

/// <summary>One line in the per-item sale popup: one completed sale of that item.</summary>
public sealed class SaleDetailRowVm
{
    public required string AgoText { get; init; }
    public required string CountText { get; init; }
    public required string PriceText { get; init; }
    public required string UnitText { get; init; }
    public required string VersusValue { get; init; }
    public required bool AboveValue { get; init; }
    public required bool HasValue { get; init; }
}

/// <summary>
/// Drives the whole window.
///
/// The UI is refreshed from snapshots on a timer rather than from per-event notifications. The
/// collectors push tens of thousands of updates a minute into the model, and marshalling each one
/// onto the dispatcher would spend more time in WPF than in the market. Pulling a ranked list four
/// times a second keeps the board smooth no matter how busy the auction house gets.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 250;

    /// <summary>Sales listed in the per-item popup. Deeper than a screen, bounded for the rebuild.</summary>
    private const int MaxSalePopupRows = 120;

    private readonly Coordinator _coordinator;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<ulong, FlipRowVm> _rowCache = new();

    /// <summary>
    /// Pinned item ids, held as a set beside the config's list because it is consulted once per
    /// flip on every board rebuild - four times a second against a few thousand candidates.
    /// </summary>
    private readonly HashSet<string> _pinned;

    private int _tickCounter;

    /// <summary>Measures how fast the book is being scanned, for the countdown in the header.</summary>
    private readonly SweepEta _sweepEta = new();

    /// <summary>
    /// Latched once the book has been read end to end, and never cleared.
    ///
    /// The sweeper starts counting from zero on every new cycle, so without the latch the countdown
    /// would reappear the moment the first sweep finished and tell the user their data had gone
    /// cold again. The second pass is re-verification, not warm-up.
    /// </summary>
    private bool _bookScanned;

    public MainViewModel(Coordinator coordinator, AppConfig config)
    {
        _coordinator = coordinator;
        Config = config;

        _pinned = new HashSet<string>(config.PinnedItems, StringComparer.OrdinalIgnoreCase);
        _coordinator.Alerts.IsPinned = IsPinned;

        Loc.Current.SetLanguage(config.Language);
        Loc.Current.LanguageChanged += OnLanguageChanged;

        if (Enum.TryParse(config.BoardSort, out BoardSort savedSort))
            _sort = savedSort;

        // Reopen on the screen the user left, not always on the board. Settings is excluded on
        // purpose: it is where the app lands when there is no API key, and reopening on it after
        // a visit would make it look like the key had been lost again.
        if (Enum.TryParse(config.LastSection, out NavSection savedSection)
            && savedSection != NavSection.Settings)
        {
            _section = savedSection;
        }

        _statusMessage = Loc.T("StatusStarting");

        CopySearchCommand = new RelayCommand(_ => CopySearchText());
        RefreshItemCommand = new RelayCommand(_ => _ = RefreshSelectedAsync());
        SetSortCommand = new RelayCommand(p => Sort = Enum.Parse<BoardSort>((string)p!));
        SetUnitModeCommand = new RelayCommand(p => ShowPerUnit = (string)p! == "unit");
        SetLanguageCommand = new RelayCommand(p => Language = (string)p!);
        TogglePinCommand = new RelayCommand(TogglePin);
        NavigateCommand = new RelayCommand(p => Section = Enum.Parse<NavSection>((string)p!));
        SaveSettingsCommand = new RelayCommand(_ => SaveSettings());
        TogglePauseCommand = new RelayCommand(_ => _ = TogglePauseAsync());
        TestAlertCommand = new RelayCommand(_ => _coordinator.Alerts.PlayPing());
        ShowItemSalesCommand = new RelayCommand(ShowItemSales);
        CloseItemSalesCommand = new RelayCommand(_ => CloseItemSales());

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public AppConfig Config { get; }

    public ObservableCollection<FlipRowVm> Flips { get; } = [];
    public ObservableCollection<TapeRowVm> RecentSales { get; } = [];
    public ObservableCollection<ItemRowVm> Items { get; } = [];

    /// <summary>The sale popup's rows: the last sales of whichever item was clicked.</summary>
    public ObservableCollection<SaleDetailRowVm> ItemSales { get; } = [];

    public RelayCommand CopySearchCommand { get; }
    public RelayCommand RefreshItemCommand { get; }
    public RelayCommand SetSortCommand { get; }
    public RelayCommand SetUnitModeCommand { get; }
    public RelayCommand SetLanguageCommand { get; }
    public RelayCommand TogglePinCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand TestAlertCommand { get; }
    public RelayCommand ShowItemSalesCommand { get; }
    public RelayCommand CloseItemSalesCommand { get; }

    // ------------------------------------------------------------------ language

    /// <summary>
    /// Interface language, switched live.
    ///
    /// Restarting to change a label would throw away the session's accumulated sale tape, which is
    /// the one asset the tool cannot rebuild in a hurry - the API only serves two minutes of it.
    /// </summary>
    public string Language
    {
        get => Config.Language;
        set
        {
            string normalised = value == "de" ? "de" : "en";
            if (Config.Language == normalised) return;

            Config.Language = normalised;
            Config.Save();
            Loc.Current.SetLanguage(normalised);
        }
    }

    public bool LanguageIsEnglish => Config.Language != "de";
    public bool LanguageIsGerman => Config.Language == "de";

    private void OnLanguageChanged()
    {
        Raise(nameof(Language));
        Raise(nameof(LanguageIsEnglish));
        Raise(nameof(LanguageIsGerman));
        Raise(nameof(BoardTitle));
        Raise(nameof(BoardSubtitle));
        Raise(nameof(BuyLabel));
        Raise(nameof(RelistLabel));
        Raise(nameof(NetLabel));
        Raise(nameof(PauseButtonText));
        Raise(nameof(ConfigPathText));
        Raise(nameof(EmptyBoardHint));
        Raise(nameof(PinnedNavText));

        // Rows and notes carry text that was formatted when they were built, so rebuild rather
        // than waiting for the next natural refresh to reach them.
        RefreshBoard();
        RefreshTape();
        RefreshItems();
        if (_salesItemId is not null) RefreshItemSales();
        UpdateDetail();
        UpdateStatus();
    }

    // ------------------------------------------------------------------ navigation

    private NavSection _section = NavSection.Board;
    public NavSection Section
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            Config.LastSection = value.ToString();
            Raise(nameof(IsBoardVisible));
            Raise(nameof(IsTapeVisible));
            Raise(nameof(IsItemsVisible));
            Raise(nameof(IsSettingsVisible));
            Raise(nameof(BoardTitle));
            Raise(nameof(BoardSubtitle));
            Raise(nameof(EmptyBoardHint));

            // The popup belongs to the tape and the item table; it carries over between those two
            // but has no business hanging around on the board or in settings.
            if (value is not (NavSection.Tape or NavSection.Items)) CloseItemSales();

            RefreshBoard();
        }
    }

    public bool IsBoardVisible => _section is NavSection.Board or NavSection.Pinned;
    public bool IsTapeVisible => _section == NavSection.Tape;
    public bool IsItemsVisible => _section == NavSection.Items;
    public bool IsSettingsVisible => _section == NavSection.Settings;

    public string BoardTitle => _section == NavSection.Pinned
        ? Loc.T("PinnedTitle")
        : Loc.T("BoardTitle");

    public string BoardSubtitle => _section == NavSection.Pinned
        ? Loc.T("PinnedSubtitle")
        : Loc.T("BoardSubtitle");

    /// <summary>What to say when the list is empty, which on the pinned tab is the normal first state.</summary>
    public string EmptyBoardHint => _section == NavSection.Pinned && _pinned.Count == 0
        ? Loc.T("PinnedEmpty")
        : "";

    private BoardSort _sort = BoardSort.ProfitPerHour;
    public BoardSort Sort
    {
        get => _sort;
        set
        {
            if (!Set(ref _sort, value)) return;
            Config.BoardSort = value.ToString();
            Raise(nameof(SortIsProfitPerHour));
            Raise(nameof(SortIsNet));
            Raise(nameof(SortIsRoi));
            Raise(nameof(SortIsFastest));
            Raise(nameof(SortIsNewest));
            RefreshBoard();
        }
    }

    public bool SortIsProfitPerHour => _sort == BoardSort.ProfitPerHour;
    public bool SortIsNet => _sort == BoardSort.NetProfit;
    public bool SortIsRoi => _sort == BoardSort.Roi;
    public bool SortIsFastest => _sort == BoardSort.Fastest;
    public bool SortIsNewest => _sort == BoardSort.Newest;

    // ------------------------------------------------------------------ pins

    public bool IsPinned(string itemId) => _pinned.Contains(itemId);

    public int PinnedCount => _pinned.Count;

    /// <summary>
    /// The nav entry for the watchlist, carrying its size.
    ///
    /// The count is on the button because a pin is otherwise invisible until the item happens to
    /// have a live flip: a restart with four pinned items and a cold book looks exactly like a
    /// restart that forgot them.
    /// </summary>
    public string PinnedNavText => _pinned.Count > 0
        ? $"{Loc.T("NavPinned")}  ·  {_pinned.Count}"
        : Loc.T("NavPinned");

    /// <summary>
    /// Adds or removes a pin and writes it straight through to disk.
    ///
    /// Saving on the click rather than on exit is deliberate: a watchlist that only survives a
    /// clean shutdown is a watchlist you cannot trust, and the write is a few hundred bytes.
    /// </summary>
    private void TogglePin(object? parameter)
    {
        string? itemId = parameter switch
        {
            FlipRowVm row => row.Flip.ItemId,
            ItemRowVm item => item.ItemId,
            string id => id,
            _ => _selected?.Flip.ItemId,
        };

        if (itemId is not { Length: > 0 }) return;

        if (!_pinned.Remove(itemId)) _pinned.Add(itemId);

        Config.PinnedItems = _pinned.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        Config.Save();

        Raise(nameof(PinnedCount));
        Raise(nameof(PinnedNavText));
        Raise(nameof(EmptyBoardHint));

        foreach (FlipRowVm row in Flips)
            row.IsPinned = _pinned.Contains(row.Flip.ItemId);

        if (_section == NavSection.Pinned) RefreshBoard();
    }

    // ------------------------------------------------------------------ price basis

    /// <summary>
    /// Whether the money columns read one item or the whole lot.
    ///
    /// The lot is the default because that is the transaction: the auction house sells a stack of
    /// 64 as one indivisible purchase, and the total is the figure that has to clear your balance.
    /// Per-item exists because that is the only basis on which two different lot sizes of the same
    /// block can be compared at a glance.
    /// </summary>
    public bool ShowPerUnit
    {
        get => Config.ShowPerUnit;
        set
        {
            if (Config.ShowPerUnit == value) return;
            Config.ShowPerUnit = value;
            Raise(nameof(ShowPerUnit));
            Raise(nameof(ShowPerLot));
            Raise(nameof(BuyLabel));
            Raise(nameof(RelistLabel));
            Raise(nameof(NetLabel));
            RefreshBoard();
        }
    }

    public bool ShowPerLot => !Config.ShowPerUnit;

    public string BuyLabel => Loc.T(Config.ShowPerUnit ? "ColBuyUnit" : "ColBuy");
    public string RelistLabel => Loc.T(Config.ShowPerUnit ? "ColRelistUnit" : "ColRelist");
    public string NetLabel => Loc.T(Config.ShowPerUnit ? "ColNetUnit" : "ColNet");

    /// <summary>
    /// Width of the detail panel, in device-independent pixels, persisted across runs.
    ///
    /// The splitter writes here on drag, rather than the panel binding its width from here, because
    /// a two-way binding on a ColumnDefinition fights the splitter for control of the same value.
    /// </summary>
    public double DetailPanelWidth
    {
        get => Config.DetailPanelWidth;
        set
        {
            double clamped = Math.Clamp(value, 260, 1200);
            if (Math.Abs(Config.DetailPanelWidth - clamped) < 0.5) return;
            Config.DetailPanelWidth = clamped;
            Raise(nameof(DetailPanelWidth));
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) RefreshBoard(); }
    }

    /// <summary>
    /// The item catalogue has its own filter. Sharing one with the board meant looking something up
    /// on the prices screen quietly filtered the flip board too, and the board then appeared empty
    /// for no visible reason.
    /// </summary>
    private string _itemSearchText = "";
    public string ItemSearchText
    {
        get => _itemSearchText;
        set { if (Set(ref _itemSearchText, value)) RefreshItems(); }
    }

    /// <summary>
    /// Set by the data views while their list is scrolled away from the top.
    ///
    /// Both lists are rebuilt on a timer against a feed that reorders underneath them, so a refresh
    /// arriving mid-scroll moves the row the cursor is over. Holding the refresh while the user is
    /// reading costs nothing - the data is still collected, it just is not redrawn until they come
    /// back to the top.
    /// </summary>
    public bool TapeScrolled { get; set; }
    public bool ItemsScrolled { get; set; }

    // ------------------------------------------------------------------ selection and detail

    private FlipRowVm? _selected;
    public FlipRowVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            UpdateDetail();
            Raise(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selected is not null;

    private IReadOnlyList<LadderRung> _ladder = [];
    public IReadOnlyList<LadderRung> Ladder { get => _ladder; private set => Set(ref _ladder, value); }

    private IReadOnlyList<double> _sparkPoints = [];
    public IReadOnlyList<double> SparkPoints { get => _sparkPoints; private set => Set(ref _sparkPoints, value); }

    private string _detailTitle = "";
    public string DetailTitle { get => _detailTitle; private set => Set(ref _detailTitle, value); }

    private string _detailSummary = "";
    public string DetailSummary { get => _detailSummary; private set => Set(ref _detailSummary, value); }

    private double _detailFairValue;
    public double DetailFairValue { get => _detailFairValue; private set => Set(ref _detailFairValue, value); }

    public ObservableCollection<string> DetailNotes { get; } = [];
    public ObservableCollection<ContainerLine> DetailContents { get; } = [];

    private bool _detailHasContents;
    public bool DetailHasContents { get => _detailHasContents; private set => Set(ref _detailHasContents, value); }

    // ------------------------------------------------------------------ status

    private string _budgetText = "";
    public string BudgetText { get => _budgetText; private set => Set(ref _budgetText, value); }

    private int[] _budgetLanes = [0, 0, 0, 0];
    public int[] BudgetLanes { get => _budgetLanes; private set => Set(ref _budgetLanes, value); }

    private int _budgetLimit = 235;
    public int BudgetLimit { get => _budgetLimit; private set => Set(ref _budgetLimit, value); }

    private string _bookText = "";
    public string BookText { get => _bookText; private set => Set(ref _bookText, value); }

    private string _tapeText = "";
    public string TapeText { get => _tapeText; private set => Set(ref _tapeText, value); }

    private string _sniperText = "";
    public string SniperText { get => _sniperText; private set => Set(ref _sniperText, value); }

    private string _sweeperText = "";
    public string SweeperText { get => _sweeperText; private set => Set(ref _sweeperText, value); }

    private double _sweeperCoverage;
    public double SweeperCoverage { get => _sweeperCoverage; private set => Set(ref _sweeperCoverage, value); }

    private string _uptimeText = "";
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

    /// <summary>How long until the data is as good as it gets, under the uptime clock.</summary>
    private string _warmupText = "";
    public string WarmupText { get => _warmupText; private set => Set(ref _warmupText, value); }

    private bool _warmupDone;
    public bool WarmupDone { get => _warmupDone; private set => Set(ref _warmupDone, value); }

    private string _statusMessage;
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    private string _resultCountText = "";
    public string ResultCountText { get => _resultCountText; private set => Set(ref _resultCountText, value); }

    private bool _paused;
    public bool Paused { get => _paused; private set => Set(ref _paused, value); }

    /// <summary>Lets the window push a message into the status bar without owning the property.</summary>
    public void StatusMessageFromHost(string message) => StatusMessage = message;

    public string ConfigPathText => Loc.T("ConfigPath", AppConfig.ConfigPath);

    private string _storageText = "";
    public string StorageText { get => _storageText; private set => Set(ref _storageText, value); }

    // ------------------------------------------------------------------ tick

    private void Tick()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Ages move every tick; the ranked list only needs rebuilding once a second.
        foreach (FlipRowVm row in Flips) row.Tick(now);

        UpdateStatus();

        if (++_tickCounter % 4 != 0) return;

        if (IsBoardVisible) RefreshBoard();
        else if (IsTapeVisible) RefreshTape();
        else if (IsItemsVisible) RefreshItems();

        if (_salesItemId is not null) RefreshItemSales();

        if (_selected is not null) UpdateDetail();

        // Settings reach disk while the app runs rather than on the way out. Closing is the one
        // moment that is not guaranteed to happen - a crash, a killed process or a Windows restart
        // all skip it - and a preferences file that only survives a polite exit is not a
        // preferences file. The write itself is a few hundred bytes and only happens when
        // something actually changed.
        if (_tickCounter % 20 == 0) Config.SaveIfDirty();
    }

    /// <summary>
    /// The countdown under the uptime clock: how long until the book has been seen once.
    ///
    /// That is the point the board can be trusted to be complete. Until the sweep has been round
    /// once, the cheapest listing for an item may simply be one nobody has looked at yet, and a
    /// missing cheapest ask is the one error that makes a flip look better than it is.
    ///
    /// It is not the end of the story, which is why the line says what is still improving once the
    /// scan is done: sale prices are weighted with a six-hour half-life, so the valuations keep
    /// sharpening for hours after the book itself is complete.
    /// </summary>
    private void UpdateWarmup(MarketStatus status)
    {
        if (status.Sweeper.CoverageFraction >= 0.999) _bookScanned = true;

        if (_bookScanned)
        {
            WarmupDone = true;
            WarmupText = Loc.T("WarmupDone", Format.Duration(status.TapeSpan));
            return;
        }

        WarmupDone = false;

        if (Paused || !status.Sweeper.Running)
        {
            _sweepEta.Reset();
            WarmupText = Loc.T("WarmupPaused");
            return;
        }

        TimeSpan? left = _sweepEta.Estimate(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            status.Sweeper.PagesVisited,
            status.Sweeper.EstimatedTotalPages);

        WarmupText = left is { } remaining
            ? Loc.T("WarmupEta", Format.Clock(remaining))
            : Loc.T("WarmupMeasuring");
    }

    private void UpdateStatus()
    {
        MarketStatus status = _coordinator.GetStatus();

        BudgetLanes = status.Budget.PerLane;
        BudgetLimit = status.Budget.Limit;
        BudgetText = $"{status.Budget.Used}/{status.Budget.Limit} req/min";

        BookText = Loc.T("BookCount", status.BookListings.ToString("N0"), status.BookItems.ToString("N0"));

        // The tape is the tool's core asset, so whether it is keeping up belongs on screen.
        TapeText = status.TapeSales > 0
            ? Loc.T("TapeCount", status.TapeSales.ToString("N0"), status.Tape.SalesPerSecond.ToString("0.#"))
              + (status.Tape.KeepingUp ? "" : Loc.T("TapeCatchingUp"))
            : Loc.T("TapeWaiting");

        // Only measured quantities go on screen here. The listing feed reports timestamps that
        // look like a live rate and are not one, so the figure shown is what the tool has actually
        // taken in, alongside how often it is currently asking.
        SniperText = Loc.T("SniperRate",
            status.Sniper.NewListingsPerMinute.ToString("0.#"),
            Format.Duration(TimeSpan.FromMilliseconds(status.Sniper.PollIntervalMs)));

        SweeperCoverage = status.Sweeper.CoverageFraction;
        SweeperText = status.Sweeper.Running
            ? Loc.T("ScanProgress",
                status.Sweeper.CoverageFraction.ToString("P0"),
                status.Sweeper.PagesVisited.ToString("N0"),
                status.Sweeper.EstimatedTotalPages.ToString("N0"))
            : Loc.T("ScanPaused");

        UptimeText = Format.Duration(status.Uptime);
        UpdateWarmup(status);

        StatusMessage = status.LastError is { Length: > 0 } error
            ? error
            : Loc.T("StatusLatency", status.AverageLatencyMs.ToString("0"));

        if (_tickCounter % 40 == 0)
        {
            long bytes = _coordinator.Storage.TotalBytesOnDisk();
            StorageText = Loc.T("StoragePath",
                (bytes / 1024.0 / 1024.0).ToString("0.#"), AppConfig.DataDirectory);
        }
    }

    // ------------------------------------------------------------------ board

    private void RefreshBoard()
    {
        // The board reorders constantly, and replacing a row at its index makes the ListBox drop
        // its selection - so inspecting a flip would blank the detail panel a second later, which
        // is precisely when the user is reading it. Remember the selection and put it back.
        FlipRowVm? previouslySelected = _selected;

        FlipOpportunity[] all = _coordinator.Market.CurrentFlips();

        List<FlipOpportunity> filtered = new(Math.Min(all.Length, MaxRows * 2));
        foreach (FlipOpportunity flip in all)
            if (Passes(flip)) filtered.Add(flip);

        filtered.Sort(Comparer());

        int take = Math.Min(MaxRows, filtered.Count);
        double heatReference = take > 0 ? Math.Max(filtered[0].NetProfit, 1) : 1;
        bool perUnit = Config.ShowPerUnit;

        for (int i = 0; i < take; i++)
        {
            FlipOpportunity flip = filtered[i];

            if (i < Flips.Count && Flips[i].Fingerprint == flip.Listing.Fingerprint)
            {
                Flips[i].Update(flip, heatReference, perUnit, _pinned.Contains(flip.ItemId));
                continue;
            }

            // Reusing the row object for a listing that merely moved rank keeps its identity, so
            // selection survives the constant reordering a live board produces.
            if (!_rowCache.TryGetValue(flip.Listing.Fingerprint, out FlipRowVm? row))
            {
                row = new FlipRowVm(flip, heatReference, perUnit, _pinned.Contains(flip.ItemId));
                _rowCache[flip.Listing.Fingerprint] = row;
            }
            else
            {
                row.Update(flip, heatReference, perUnit, _pinned.Contains(flip.ItemId));
            }

            if (i < Flips.Count) Flips[i] = row;
            else Flips.Add(row);
        }

        while (Flips.Count > take) Flips.RemoveAt(Flips.Count - 1);

        if (_rowCache.Count > MaxRows * 6) PruneRowCache();

        ResultCountText = filtered.Count > take
            ? Loc.T("MatchingOf", take, filtered.Count.ToString("N0"))
            : Loc.T("Matching", filtered.Count.ToString("N0"));

        RestoreSelection(previouslySelected);
    }

    /// <summary>
    /// Re-selects the row the user had chosen, if it is still on the board.
    ///
    /// Row objects are reused per listing, so identity survives reordering even though position
    /// does not. If the flip has genuinely gone - sold, expired, or filtered out - the selection is
    /// left cleared rather than pointing at whatever now occupies that row.
    /// </summary>
    private void RestoreSelection(FlipRowVm? previous)
    {
        if (previous is null || ReferenceEquals(_selected, previous)) return;
        if (!Flips.Contains(previous)) return;

        _selected = previous;
        Raise(nameof(Selected));
        Raise(nameof(HasSelection));
    }

    private void PruneRowCache()
    {
        var live = new HashSet<ulong>(Flips.Select(f => f.Fingerprint));
        foreach (ulong key in _rowCache.Keys.ToArray())
            if (!live.Contains(key)) _rowCache.Remove(key);
    }

    private bool Passes(FlipOpportunity flip)
    {
        // Anything that can carry hidden enchantments is dropped outright.
        //
        // It used to have its own tab. That was a mistake: the API returns no enchantment, lore or
        // custom-name data whatsoever, so a cheap diamond sword is either plain or worth a hundred
        // times the ask and nothing in the payload distinguishes them. A tab full of coin flips
        // dressed as analysis is worse than no tab, because it looks like the rest of the board.
        if (flip.Flags.HasFlag(FlipFlags.NbtRisk)) return false;

        bool isContainer = flip.Flags.HasFlag(FlipFlags.Container);
        if (isContainer && !Config.IncludeContainers) return false;

        if (_section == NavSection.Pinned)
        {
            // The pinned tab answers "what is happening with the things I am working", so the
            // general filters do not apply: a thin edge on an item you are already holding is
            // exactly the thing those filters exist to hide from the main feed.
            if (!_pinned.Contains(flip.ItemId)) return false;
            return flip.NetProfit > 0;
        }

        if (flip.NetProfit < Config.MinNetProfit) return false;
        if (flip.Roi < Config.MinRoi) return false;
        if (flip.Confidence < Config.MinConfidence) return false;

        // Containers do not trade as a unit, so a market rate for "shulker box" means nothing.
        if (!isContainer && flip.SalesPerHour < Config.MinSalesPerHour) return false;

        if (Config.MaxBuyPrice > 0 && flip.BuyTotal > Config.MaxBuyPrice) return false;

        if (_searchText.Length > 0
            && !flip.Info.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private Comparison<FlipOpportunity> Comparer() => _sort switch
    {
        BoardSort.NetProfit => static (a, b) => b.NetProfit.CompareTo(a.NetProfit),
        BoardSort.Roi => static (a, b) => b.Roi.CompareTo(a.Roi),
        BoardSort.Fastest => static (a, b) => a.AbsorbHours.CompareTo(b.AbsorbHours),
        BoardSort.Newest => static (a, b) => a.AgeMs.CompareTo(b.AgeMs),
        _ => static (a, b) => b.Score.CompareTo(a.Score),
    };

    // ------------------------------------------------------------------ detail

    private void UpdateDetail()
    {
        FlipRowVm? row = _selected;
        if (row is null)
        {
            Ladder = [];
            SparkPoints = [];
            DetailNotes.Clear();
            DetailContents.Clear();
            DetailHasContents = false;
            return;
        }

        FlipOpportunity flip = row.Flip;
        MarketState market = _coordinator.Market;

        DetailTitle = $"{flip.Info.DisplayName}" + (flip.Count > 1 ? $"  x{flip.Count}" : "");
        DetailFairValue = flip.FairUnit;

        ItemDetail? detail = market.GetDetail(flip.ItemId);
        if (detail is not null)
        {
            var rungs = new List<LadderRung>(16);
            bool targetMarked = false;
            foreach (Listing listing in detail.Ladder.Take(14))
            {
                bool isTarget = !targetMarked && listing.UnitPrice >= flip.ResellUnit;
                if (isTarget) targetMarked = true;

                rungs.Add(new LadderRung(
                    listing.UnitPrice,
                    listing.Count * listing.Multiplicity,
                    isTarget));
            }
            Ladder = rungs;

            SparkPoints = Downsample(detail.History.Select(h => h.UnitPrice).ToArray(), 140);

            ItemValue value = detail.Value;
            DetailSummary = Loc.T("DetailSummary",
                Format.Coins(value.Unit),
                Format.ValueSourceLabel(value.Source),
                value.Sales.SampleCount,
                Format.Rate(value.Sales.SalesPerHour),
                detail.Ladder.Length);
        }

        DetailNotes.Clear();
        foreach (string note in flip.Notes) DetailNotes.Add(note);
        if (DetailNotes.Count == 0)
            DetailNotes.Add(Loc.T("NoPenalties"));

        DetailContents.Clear();
        foreach (ContainerLine line in flip.ContainerLines) DetailContents.Add(line);
        DetailHasContents = DetailContents.Count > 0;
    }

    /// <summary>Thins a series to at most <paramref name="max"/> points for the sparkline.</summary>
    private static double[] Downsample(double[] source, int max)
    {
        if (source.Length <= max) return source;

        var result = new double[max];
        double step = (double)source.Length / max;
        for (int i = 0; i < max; i++) result[i] = source[(int)(i * step)];
        return result;
    }

    // ------------------------------------------------------------------ tape and items

    /// <summary>
    /// Writes a freshly built list into a bound collection without clearing it first.
    ///
    /// Clear-then-add raises a reset, which makes the list rebuild every container and jump back to
    /// the top - once a second, while the user is trying to read it. Assigning by index raises a
    /// replace per row instead, so the scroll position holds and only the rows actually on screen
    /// are re-bound.
    /// </summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> source)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count) target[i] = source[i];
            else target.Add(source[i]);
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }

    private void RefreshTape()
    {
        if (TapeScrolled && RecentSales.Count > 0) return;

        MarketState market = _coordinator.Market;
        Sale[] sales = market.Tape.RecentSales(200);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rows = new List<TapeRowVm>(sales.Length);
        foreach (Sale sale in sales)
        {
            string id = market.Items.GetName(sale.ItemIndex);
            ItemInfo info = ItemCatalog.Get(id);
            ItemValue value = market.ValueOf(sale.ItemIndex);

            bool known = value.IsKnown && value.Unit > 0;
            double delta = known ? sale.UnitPrice / value.Unit - 1 : 0;

            rows.Add(new TapeRowVm
            {
                ItemId = id,
                ItemName = info.DisplayName,
                Monogram = Format.Monogram(info.DisplayName),
                Hue = info.Hue,
                CountText = sale.Count > 1 ? $"x{sale.Count}" : "",
                PriceText = Format.Coins(sale.Price),
                UnitText = Format.Coins(sale.UnitPrice),
                AgoText = Format.Age(Math.Max(0, now - sale.SoldAtUnixMs)),
                VersusValue = known ? Format.Signed(delta * 100) + "%" : "-",
                AboveValue = delta >= 0,
                HasValue = known,
            });
        }

        Sync(RecentSales, rows);
    }

    private void RefreshItems()
    {
        if (ItemsScrolled && Items.Count > 0) return;

        MarketState market = _coordinator.Market;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var rows = new List<ItemRowVm>(512);
        foreach (int itemIndex in market.Book.TrackedItemIndexes())
        {
            string id = market.Items.GetName(itemIndex);
            ItemInfo info = ItemCatalog.Get(id);

            if (_itemSearchText.Length > 0
                && !info.DisplayName.Contains(_itemSearchText, StringComparison.OrdinalIgnoreCase))
                continue;

            ItemSaleStats sales = market.Tape.GetStats(itemIndex, now);
            BookStats book = market.Book.GetStats(itemIndex);
            ItemValue value = Valuation.Compute(sales, book);

            rows.Add(new ItemRowVm
            {
                ItemId = id,
                ItemName = info.DisplayName,
                Monogram = Format.Monogram(info.DisplayName),
                Hue = info.Hue,
                ValueText = value.IsKnown ? Format.Coins(value.Unit) : "-",
                SourceText = Format.ValueSourceLabel(value.Source),
                SalesText = sales.SampleCount > 0 ? $"{sales.SampleCount} @ {Format.Rate(sales.SalesPerHour)}" : "-",
                AsksText = book.AskCount.ToString("N0"),
                LowestText = book.AskCount > 0 ? Format.Coins(book.LowestUnit) : "-",
                CategoryText = Format.CategoryLabel(info.Category),
                NbtRisk = info.NbtRisk,
            });
        }

        rows.Sort(static (a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        if (rows.Count > 600) rows.RemoveRange(600, rows.Count - 600);

        Sync(Items, rows);
    }

    // ------------------------------------------------------------------ per-item sale popup

    /// <summary>
    /// Sale history for one item, opened by clicking a row on the tape or in the item table.
    ///
    /// The tape answers "what just sold"; this answers "what does this thing normally go for",
    /// which is the question you actually have in front of a listing. It is read out of the tape
    /// the tool has already accumulated, so opening it costs no API budget at all.
    /// </summary>
    private string? _salesItemId;

    public bool HasItemSales => _salesItemId is not null;

    private string _salesItemName = "";
    public string SalesItemName { get => _salesItemName; private set => Set(ref _salesItemName, value); }

    private string _salesItemIcon = "";
    public string SalesItemIcon { get => _salesItemIcon; private set => Set(ref _salesItemIcon, value); }

    private string _salesMonogram = "";
    public string SalesMonogram { get => _salesMonogram; private set => Set(ref _salesMonogram, value); }

    private double _salesHue;
    public double SalesHue { get => _salesHue; private set => Set(ref _salesHue, value); }

    private string _salesSummary = "";
    public string SalesSummary { get => _salesSummary; private set => Set(ref _salesSummary, value); }

    private string _salesEmptyText = "";
    public string SalesEmptyText { get => _salesEmptyText; private set => Set(ref _salesEmptyText, value); }

    private void ShowItemSales(object? parameter)
    {
        string? itemId = parameter switch
        {
            TapeRowVm tape => tape.ItemId,
            ItemRowVm item => item.ItemId,
            FlipRowVm flip => flip.Flip.ItemId,
            string id => id,
            _ => null,
        };

        if (itemId is not { Length: > 0 }) return;

        // A second click on the row that is already open closes it again, so the same click both
        // opens and dismisses and the panel never has to be hunted for a close button.
        if (string.Equals(_salesItemId, itemId, StringComparison.Ordinal))
        {
            CloseItemSales();
            return;
        }

        _salesItemId = itemId;

        ItemInfo info = ItemCatalog.Get(itemId);
        SalesItemName = info.DisplayName;
        SalesItemIcon = itemId;
        SalesMonogram = Format.Monogram(info.DisplayName);
        SalesHue = info.Hue;

        Raise(nameof(HasItemSales));
        RefreshItemSales();
    }

    private void CloseItemSales()
    {
        if (_salesItemId is null) return;

        _salesItemId = null;
        ItemSales.Clear();
        Raise(nameof(HasItemSales));
    }

    private void RefreshItemSales()
    {
        if (_salesItemId is null) return;

        MarketState market = _coordinator.Market;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!market.Items.TryGetIndex(_salesItemId, out int itemIndex))
        {
            ItemSales.Clear();
            SalesSummary = Loc.T("SalesPopupUnknown");
            SalesEmptyText = Loc.T("SalesPopupEmpty");
            return;
        }

        ItemValue value = market.ValueOf(itemIndex);
        bool known = value.IsKnown && value.Unit > 0;
        ItemSaleStats stats = market.Tape.GetStats(itemIndex, now);

        SalesSummary = known
            ? Loc.T("SalesPopupSummary", Format.Coins(value.Unit), Format.Rate(stats.SalesPerHour))
            : Loc.T("SalesPopupUnknown");

        (long Time, double UnitPrice, int Count)[] sales = market.Tape.RecentSalesFor(itemIndex, MaxSalePopupRows);

        var rows = new List<SaleDetailRowVm>(sales.Length);
        foreach ((long time, double unitPrice, int count) in sales)
        {
            double delta = known ? unitPrice / value.Unit - 1 : 0;
            rows.Add(new SaleDetailRowVm
            {
                AgoText = Format.Age(Math.Max(0, now - time)),
                CountText = count > 1 ? $"x{count}" : "x1",
                PriceText = Format.Coins(unitPrice * count),
                UnitText = Format.Coins(unitPrice),
                VersusValue = known ? Format.Signed(delta * 100) + "%" : "-",
                AboveValue = delta >= 0,
                HasValue = known,
            });
        }

        SalesEmptyText = rows.Count == 0 ? Loc.T("SalesPopupEmpty") : "";
        Sync(ItemSales, rows);
    }

    // ------------------------------------------------------------------ commands

    private void CopySearchText()
    {
        if (_selected is null) return;
        TryCopy(_selected.Flip.SearchText);
    }

    /// <summary>
    /// Copies with retries: the Windows clipboard is a shared resource and another process holding
    /// it open makes the first attempt throw.
    /// </summary>
    public static bool TryCopy(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Thread.Sleep(30);
            }
        }
        return false;
    }

    private async Task RefreshSelectedAsync()
    {
        if (_selected is null) return;

        string itemId = _selected.Flip.ItemId;
        StatusMessage = Loc.T("StatusRefreshing", _selected.Flip.Info.DisplayName);
        try
        {
            int added = await _coordinator.RefreshItemAsync(itemId, CancellationToken.None);
            StatusMessage = Loc.T("StatusRefreshed", itemId, added);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.T("StatusRefreshFailed", ex.Message);
        }
    }

    /// <summary>
    /// Stops and restarts the collectors outright, so a paused tool spends no API budget at all.
    /// The board keeps showing what it already knows; it just stops being updated.
    /// </summary>
    private async Task TogglePauseAsync()
    {
        if (_coordinator.IsRunning)
        {
            StatusMessage = Loc.T("StatusStopping");
            await _coordinator.StopAsync();
            Paused = true;
            StatusMessage = Loc.T("StatusPaused");
        }
        else
        {
            _coordinator.Start();
            Paused = false;
            StatusMessage = Loc.T("StatusResumed");
        }

        Raise(nameof(PauseButtonText));
    }

    public string PauseButtonText => Loc.T(Paused ? "Resume" : "Pause");

    private void SaveSettings()
    {
        Config.Save();
        _coordinator.ApplyConfig(Config);
        StatusMessage = Loc.T("StatusSaved", AppConfig.ConfigPath);
        RefreshBoard();
    }

    public void Dispose()
    {
        Loc.Current.LanguageChanged -= OnLanguageChanged;
        _timer.Stop();
    }
}
