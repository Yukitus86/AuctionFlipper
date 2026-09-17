using System.Diagnostics;

namespace AuctionFlipper.Api;

/// <summary>Which collector a request belongs to. Drives the guaranteed-share scheduling.</summary>
public enum Lane
{
    /// <summary>New-listing feed. Highest value per request, so it gets the largest guarantee.</summary>
    Sniper = 0,
    /// <summary>Sale tape. Small but must never be starved, or valuations go stale.</summary>
    Tape = 1,
    /// <summary>User-triggered lookups. Reserved so the UI always feels responsive.</summary>
    Interactive = 2,
    /// <summary>Background book sampling. Soaks up whatever is left over.</summary>
    Sweeper = 3,
}

/// <summary>
/// Enforces the API's 250 requests/minute cap with a true sliding window, and divides that budget
/// between collectors.
///
/// A fixed-window counter would allow 500 requests across a window boundary, so instead every
/// admitted request's timestamp is retained for 60 seconds and a new request is only admitted when
/// the window genuinely has room.
///
/// Lanes get a guaranteed minimum rather than a hard quota: a lane below its guarantee may always
/// spend, while a lane above it may only use capacity that no other lane still needs. That lets the
/// sweeper consume every spare request without ever pushing the sniper off its cadence.
/// </summary>
public sealed class RateLimiter
{
    public const int LaneCount = 4;

    private const int WindowMs = 60_000;

    /// <summary>
    /// The sub-window used to judge how busy a lane currently is. Reservations are sized from
    /// demand over these last few seconds, not from the whole minute.
    /// </summary>
    private const int DemandWindowMs = 10_000;

    /// <summary>
    /// Instant headroom always kept clear for each lane even when it is completely idle, indexed
    /// the same way as the guarantees. Roughly two seconds of work each, so a lane that wakes up
    /// finds room immediately instead of waiting for the window to drain.
    /// </summary>
    private static readonly int[] IdleFloors = [4, 1, 2, 0];

    /// <summary>
    /// Slots the background scan will not touch, so a live feed always finds room immediately.
    /// About four seconds of sniper polling.
    /// </summary>
    private const int SweeperHeadroom = 15;

    /// <summary>Ring capacity, sized above any limit the API could plausibly grant.</summary>
    private const int Capacity = 1024;

    private int _hardLimit;
    private int[] _guarantees;

    // Ring buffer of admitted request timestamps (Stopwatch ms), oldest at _head.
    private readonly long[] _timestamps;
    private readonly byte[] _lanes;
    private int _head;
    private int _count;

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Set when the server pushes back (HTTP 429); no lane may spend until it passes.</summary>
    private long _penaltyUntilMs;

    public RateLimiter(int hardLimitPerMinute = 235, int[]? guarantees = null)
    {
        _hardLimit = hardLimitPerMinute;

        // Sniper 120 + Tape 12 + Interactive 20 = 152 guaranteed; the sweeper works the remaining
        // ~83/min, plus anything the others leave unused (usually a lot).
        _guarantees = guarantees ?? [120, 12, 20, 0];

        _timestamps = new long[Capacity];
        _lanes = new byte[Capacity];
    }

    public int HardLimitPerMinute => _hardLimit;

    /// <summary>
    /// Applies edited budget settings in place, keeping the trailing window intact so a change
    /// cannot be used to slip past the cap by resetting the accounting.
    /// </summary>
    public void Reconfigure(int hardLimitPerMinute, int[] guarantees)
    {
        lock (_gate)
        {
            _hardLimit = Math.Clamp(hardLimitPerMinute, 10, Capacity - 8);
            _guarantees = guarantees;
        }
    }

    /// <summary>
    /// Waits until this lane may issue a request, then records it. Returns when the caller should
    /// fire; the caller must actually send, since the slot is consumed either way.
    /// </summary>
    public async Task AcquireAsync(Lane lane, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int waitMs;
            lock (_gate)
            {
                long now = _clock.ElapsedMilliseconds;
                Evict(now);

                if (now >= _penaltyUntilMs && CanAdmit(lane))
                {
                    Record(lane, now);
                    return;
                }

                waitMs = ComputeWaitMs(now);
            }

            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Non-blocking variant for the sweeper, which should skip a beat rather than queue up behind
    /// higher-priority lanes.
    /// </summary>
    public bool TryAcquire(Lane lane)
    {
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            Evict(now);

            if (now < _penaltyUntilMs || !CanAdmit(lane))
                return false;

            Record(lane, now);
            return true;
        }
    }

    /// <summary>Applies server-requested backoff after an HTTP 429.</summary>
    public void ApplyPenalty(TimeSpan duration)
    {
        lock (_gate)
        {
            long until = _clock.ElapsedMilliseconds + (long)duration.TotalMilliseconds;
            if (until > _penaltyUntilMs)
                _penaltyUntilMs = until;
        }
    }

    /// <summary>Requests used in the trailing 60 s, total and per lane. Feeds the UI budget gauge.</summary>
    public RateSnapshot Snapshot()
    {
        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            Evict(now);

            var perLane = new int[LaneCount];
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % _timestamps.Length;
                perLane[_lanes[idx]]++;
            }

            return new RateSnapshot(_count, _hardLimit, perLane,
                PenaltyRemaining: _penaltyUntilMs > now
                    ? TimeSpan.FromMilliseconds(_penaltyUntilMs - now)
                    : TimeSpan.Zero);
        }
    }

    // ---- internals (all called under _gate) ----

    private void Evict(long now)
    {
        long cutoff = now - WindowMs;
        while (_count > 0 && _timestamps[_head] <= cutoff)
        {
            _head = (_head + 1) % _timestamps.Length;
            _count--;
        }
    }

    private bool CanAdmit(Lane lane)
    {
        if (_count >= _hardLimit)
            return false;

        // The background scan stops short of the ceiling so the live feeds never have to queue
        // behind it for a freed slot.
        //
        // Guarantees alone do not cover this. Once the sniper has spent its guaranteed share - which
        // it does routinely, that share being sized for the real listing rate - it is judged by the
        // same rule as everything else, and a scan that fills the window to the brim adds a wait to
        // every subsequent poll. Detection latency is the entire value of the tool, so a few
        // requests a minute are left unspent to protect it.
        if (lane == Lane.Sweeper && _count >= _hardLimit - SweeperHeadroom)
            return false;

        int laneIdx = (int)lane;
        int laneGuarantee = _guarantees[laneIdx];

        long now = _clock.ElapsedMilliseconds;
        long demandCutoff = now - DemandWindowMs;

        Span<int> perLane = stackalloc int[LaneCount];
        Span<int> recentDemand = stackalloc int[LaneCount];
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head + i) % _timestamps.Length;
            byte lane_ = _lanes[idx];
            perLane[lane_]++;
            if (_timestamps[idx] >= demandCutoff) recentDemand[lane_]++;
        }

        // Inside its own guarantee: always allowed while the global window has room.
        if (perLane[laneIdx] < laneGuarantee)
            return true;

        // Beyond its guarantee, a lane may use capacity no other lane is about to need.
        //
        // What counts as "about to need" decides whether the budget is both safe and fully spent.
        // Holding back a whole minute of every quota pins total usage far below the cap whenever a
        // lane is quiet, and absorbing that slack is the sweeper's entire job. Holding back nothing
        // lets the sweeper fill the window and pushes the sniper's detection latency out.
        //
        // Sizing the reservation from a lane's *measured* recent volume looks like the answer and
        // is a trap: a lane that is being squeezed issues fewer requests, which shrinks its
        // reservation, which squeezes it harder. In practice that fed back until the sniper was
        // capturing one listing a second out of nearly sixty.
        //
        // So the test is simply whether a lane is active at all. An active lane reserves everything
        // it is still owed, which lets it climb to its guarantee; an idle one reserves a token
        // amount so it can restart promptly, and the sweeper gets the rest.
        int reservedForOthers = 0;
        for (int i = 0; i < LaneCount; i++)
        {
            if (i == laneIdx) continue;

            int owed = _guarantees[i] - perLane[i];
            if (owed <= 0) continue;

            bool active = recentDemand[i] > 0;
            reservedForOthers += active ? owed : Math.Min(owed, IdleFloors[i]);
        }

        return _count + reservedForOthers < _hardLimit;
    }

    private void Record(Lane lane, long now)
    {
        int tail = (_head + _count) % _timestamps.Length;
        _timestamps[tail] = now;
        _lanes[tail] = (byte)lane;
        _count++;
    }

    private int ComputeWaitMs(long now)
    {
        if (now < _penaltyUntilMs)
            return (int)Math.Min(2_000, _penaltyUntilMs - now) + 1;

        if (_count == 0)
            return 5;

        // Wait until the oldest request falls out of the window.
        long expiresIn = _timestamps[_head] + WindowMs - now;
        return (int)Math.Clamp(expiresIn, 5, 2_000);
    }
}

public readonly record struct RateSnapshot(
    int Used,
    int Limit,
    int[] PerLane,
    TimeSpan PenaltyRemaining);
