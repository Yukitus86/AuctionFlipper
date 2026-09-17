using System.Windows.Media;
using AuctionFlipper.Core;

namespace AuctionFlipper.Ui.ViewModels;

/// <summary>
/// One row on the flip board.
///
/// Rows are reused in place rather than rebuilt each refresh: the board updates four times a
/// second against a list that is mostly the same from tick to tick, and replacing the objects would
/// throw away selection, scroll position and any hover state several times a second.
/// </summary>
public sealed class FlipRowVm : ObservableObject
{
    private static readonly SolidColorBrush GradeS = ColorUtil.Frozen(Color.FromRgb(0xC7, 0x7D, 0xFF));
    private static readonly SolidColorBrush GradeA = ColorUtil.Frozen(Color.FromRgb(0x2E, 0xD4, 0x7A));
    private static readonly SolidColorBrush GradeB = ColorUtil.Frozen(Color.FromRgb(0x35, 0xC2, 0xE4));
    private static readonly SolidColorBrush GradeC = ColorUtil.Frozen(Color.FromRgb(0x7A, 0x87, 0x98));

    private FlipOpportunity _flip = null!;

    public FlipRowVm(FlipOpportunity flip, double heatReference, bool perUnit, bool pinned)
    {
        Update(flip, heatReference, perUnit, pinned);
    }

    public FlipOpportunity Flip => _flip;
    public ulong Fingerprint => _flip.Listing.Fingerprint;

    private string _itemId = "";
    public string ItemId { get => _itemId; private set => Set(ref _itemId, value); }

    private string _itemName = "";
    public string ItemName { get => _itemName; private set => Set(ref _itemName, value); }

    private string _monogram = "";
    public string Monogram { get => _monogram; private set => Set(ref _monogram, value); }

    private double _hue;
    public double Hue { get => _hue; private set => Set(ref _hue, value); }

    private string _countText = "";
    public string CountText { get => _countText; private set => Set(ref _countText, value); }

    private string _buyText = "";
    public string BuyText { get => _buyText; private set => Set(ref _buyText, value); }

    private string _sellText = "";
    public string SellText { get => _sellText; private set => Set(ref _sellText, value); }

    private string _netText = "";
    public string NetText { get => _netText; private set => Set(ref _netText, value); }

    private string _roiText = "";
    public string RoiText { get => _roiText; private set => Set(ref _roiText, value); }

    private Brush _netBrush = Brushes.White;
    public Brush NetBrush { get => _netBrush; private set => Set(ref _netBrush, value); }

    private double _confidence;
    public double Confidence { get => _confidence; private set => Set(ref _confidence, value); }

    private string _gradeText = "";
    public string GradeText { get => _gradeText; private set => Set(ref _gradeText, value); }

    private Brush _gradeBrush = GradeC;
    public Brush GradeBrush { get => _gradeBrush; private set => Set(ref _gradeBrush, value); }

    private string _badges = "";
    public string Badges { get => _badges; private set => Set(ref _badges, value); }

    private bool _hasBadges;
    public bool HasBadges { get => _hasBadges; private set => Set(ref _hasBadges, value); }

    private string _ageText = "";
    public string AgeText { get => _ageText; private set => Set(ref _ageText, value); }

    /// <summary>1 when newly listed, fading to 0 over ten minutes. Drives the row glow.</summary>
    private double _freshness;
    public double Freshness { get => _freshness; private set => Set(ref _freshness, value); }

    private string _rateText = "";
    public string RateText { get => _rateText; private set => Set(ref _rateText, value); }

    private string _absorbText = "";
    public string AbsorbText { get => _absorbText; private set => Set(ref _absorbText, value); }

    private string _sellerName = "";
    public string SellerName { get => _sellerName; private set => Set(ref _sellerName, value); }

    private string _unitText = "";
    public string UnitText { get => _unitText; private set => Set(ref _unitText, value); }

    /// <summary>
    /// Whether the user is tracking this item. Settable from outside because a pin toggled on one
    /// row has to light up on every other row showing the same item, without a board rebuild.
    /// </summary>
    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (!Set(ref _isPinned, value)) return;
            Raise(nameof(PinTip));
        }
    }

    public string PinTip => Loc.T(_isPinned ? "UnpinTip" : "PinTip");

    // ---------------------------------------------------------------- hover card
    //
    // The row is a summary: three-significant-digit money, a grade and a bar. The card behind it
    // carries what the summary had to drop - exact coins, where the price came from, how long the
    // stack takes to clear and every reason the score was marked down - so a flip can be judged
    // without leaving the list or losing your place in it.

    private string _tipBuy = "";
    public string TipBuy { get => _tipBuy; private set => Set(ref _tipBuy, value); }

    private string _tipBuyEach = "";
    public string TipBuyEach { get => _tipBuyEach; private set => Set(ref _tipBuyEach, value); }

    private string _tipSell = "";
    public string TipSell { get => _tipSell; private set => Set(ref _tipSell, value); }

    private string _tipNet = "";
    public string TipNet { get => _tipNet; private set => Set(ref _tipNet, value); }

    private string _tipFair = "";
    public string TipFair { get => _tipFair; private set => Set(ref _tipFair, value); }

    private string _tipSource = "";
    public string TipSource { get => _tipSource; private set => Set(ref _tipSource, value); }

    private string _tipPerHour = "";
    public string TipPerHour { get => _tipPerHour; private set => Set(ref _tipPerHour, value); }

    private string _tipConfidence = "";
    public string TipConfidence { get => _tipConfidence; private set => Set(ref _tipConfidence, value); }

    private IReadOnlyList<string> _tipNotes = [];
    public IReadOnlyList<string> TipNotes { get => _tipNotes; private set => Set(ref _tipNotes, value); }

    private bool _hasNotes;
    public bool HasNotes { get => _hasNotes; private set => Set(ref _hasNotes, value); }

    /// <summary>
    /// Rewrites the row from a flip.
    ///
    /// <paramref name="perUnit"/> switches the three money columns between the lot total and the
    /// price of a single item. ROI is deliberately not switched: it is a ratio, so it reads the
    /// same either way, and printing a second figure for it would only suggest otherwise.
    /// </summary>
    public void Update(FlipOpportunity flip, double heatReference, bool perUnit, bool pinned)
    {
        _flip = flip;

        ItemId = flip.Info.Id;
        ItemName = flip.Info.DisplayName;
        Monogram = Format.Monogram(flip.Info.DisplayName);
        Hue = flip.Info.Hue;
        CountText = flip.Count > 1 ? $"x{flip.Count}" : "";
        SellerName = flip.SellerName;
        IsPinned = pinned;

        int count = Math.Max(1, flip.Count);
        double netShown = perUnit ? flip.NetProfit / count : flip.NetProfit;

        BuyText = Format.Coins(perUnit ? flip.BuyUnit : flip.BuyTotal);
        SellText = Format.Coins(perUnit ? flip.ResellUnit : flip.ResellTotal);
        NetText = Format.Signed(netShown);
        RoiText = Format.Percent(flip.Roi);

        // Whichever way round the columns are reading, the other figure stays on the sub-line, so
        // switching modes never hides what the whole lot costs or what one of them is worth.
        UnitText = count == 1
            ? ""
            : perUnit ? Loc.T("LotSuffix", Format.Coins(flip.BuyTotal))
                      : Loc.T("EachSuffix", Format.Coins(flip.BuyUnit));

        // The heat ramp always reads the lot, so the brightest row is the biggest real payday
        // rather than whichever item happens to have the smallest lot size.
        NetBrush = ColorUtil.Frozen(ColorUtil.ProfitHeat(flip.NetProfit, heatReference));

        Confidence = flip.Confidence;
        GradeText = Format.Grade(flip.Grade);
        GradeBrush = flip.Grade switch
        {
            FlipGrade.S => GradeS,
            FlipGrade.A => GradeA,
            FlipGrade.B => GradeB,
            _ => GradeC,
        };

        string badges = string.Join("  ", Format.Badges(flip.Flags));
        Badges = badges;
        HasBadges = badges.Length > 0;

        AgeText = Format.Age(flip.AgeMs);
        Freshness = FreshnessFor(flip.AgeMs);

        RateText = Format.Rate(flip.SalesPerHour);
        AbsorbText = Format.Absorb(flip.AbsorbHours);

        TipBuy = Format.Exact(flip.BuyTotal);
        TipBuyEach = count > 1 ? Format.Exact(flip.BuyUnit) : "";
        TipSell = Format.Exact(flip.ResellUnit);
        TipNet = Format.Exact(flip.NetProfit);
        TipFair = Format.Exact(flip.FairUnit);
        TipSource = Format.ValueSourceLabel(flip.ValueSource);
        TipPerHour = Format.Coins(flip.ProfitPerHour);
        TipConfidence = $"{flip.Confidence:0}/100";

        TipNotes = flip.Notes;
        HasNotes = flip.Notes.Count > 0;
    }

    /// <summary>Refreshes only the fields that move with the clock, between full rescans.</summary>
    public void Tick(long nowUnixMs)
    {
        long age = Math.Max(0, nowUnixMs - _flip.Listing.ListedAtUnixMs);
        AgeText = Format.Age(age);
        Freshness = FreshnessFor(age);
    }

    /// <summary>
    /// How brightly a row glows, fading over ten minutes.
    ///
    /// A shorter window would never light up. The listing feed is a slow-moving view whose newest
    /// rows are already minutes old on arrival, so "just listed" here means minutes, not seconds,
    /// and the highlight is scaled to what the data can actually support.
    /// </summary>
    private static double FreshnessFor(long ageMs) =>
        Math.Clamp(1.0 - ageMs / 600_000.0, 0, 1);
}
