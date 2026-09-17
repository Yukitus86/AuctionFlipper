using System.Media;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

/// <summary>
/// Decides which flips are worth interrupting the user for, and makes the noise.
///
/// The board updates constantly; an alert is a claim that something is worth stopping to look at.
/// Cooldowns matter more than the threshold here: one liquid item can throw a qualifying flip every
/// few seconds, and an alert that fires every few seconds is one the user turns off.
/// </summary>
public sealed class AlertService
{
    private readonly Dictionary<string, long> _lastAlertPerItem = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _lastAnyAlertMs;

    private AppConfig _config;
    private MemoryStream? _pingWav;

    public AlertService(AppConfig config)
    {
        _config = config;
    }

    public void UpdateConfig(AppConfig config) => _config = config;

    /// <summary>
    /// Asks whether the user is tracking an item. Supplied by the view model, which owns the pins.
    ///
    /// It is a callback rather than a copy of the set because pins change while the collectors are
    /// running, and a snapshot taken at construction would quietly go stale the first time the user
    /// clicked a star.
    /// </summary>
    public Func<string, bool>? IsPinned { get; set; }

    /// <summary>Raised when a flip clears the alert bar. The UI shows the toast and copies text.</summary>
    public event Action<FlipOpportunity>? AlertRaised;

    public void Consider(FlipOpportunity flip)
    {
        if (!Qualifies(flip)) return;

        long now = Environment.TickCount64;
        lock (_gate)
        {
            // One global cooldown stops a burst of listings turning into a burst of pings, and a
            // per-item cooldown stops a single busy item monopolising them.
            if (now - _lastAnyAlertMs < 1_500) return;

            if (_lastAlertPerItem.TryGetValue(flip.ItemId, out long last)
                && now - last < _config.AlertCooldownSeconds * 1000L)
                return;

            _lastAlertPerItem[flip.ItemId] = now;
            _lastAnyAlertMs = now;
        }

        if (_config.AlertSoundEnabled) PlayPing();
        AlertRaised?.Invoke(flip);
    }

    private bool Qualifies(FlipOpportunity flip)
    {
        if (flip.Flags.HasFlag(FlipFlags.OverBudget)) return false;
        if (flip.Flags.HasFlag(FlipFlags.NbtRisk)) return false;

        // A pinned item is one the user has said they are working, so the grade and profit floors -
        // which exist to keep the general feed quiet - are the wrong test for it. The cooldowns
        // still apply, so this cannot turn a busy pinned item into a siren.
        if (_config.AlertPinnedAlways && IsPinned?.Invoke(flip.ItemId) == true)
            return flip.NetProfit > 0;

        if ((int)flip.Grade > _config.AlertMinGrade) return false;
        if (flip.NetProfit < _config.AlertMinNetProfit) return false;
        return true;
    }

    /// <summary>
    /// Plays a short two-tone ping synthesised at startup.
    ///
    /// Generating the waveform avoids shipping an audio file and keeps the sound distinct from the
    /// Windows system sounds, which are easy to mistake for another application.
    /// </summary>
    public void PlayPing()
    {
        try
        {
            _pingWav ??= BuildPingWav();
            _pingWav.Position = 0;
            using var player = new SoundPlayer(_pingWav);
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No audio device, or the sound subsystem is busy - never worth failing over.
        }
    }

    private static MemoryStream BuildPingWav()
    {
        const int sampleRate = 44_100;
        const double duration = 0.16;
        int samplesPerTone = (int)(sampleRate * duration / 2);
        int totalSamples = samplesPerTone * 2;

        var pcm = new short[totalSamples];
        WriteTone(pcm, 0, samplesPerTone, 880, sampleRate);
        WriteTone(pcm, samplesPerTone, samplesPerTone, 1320, sampleRate);

        var stream = new MemoryStream(44 + totalSamples * 2);
        var w = new BinaryWriter(stream);

        int dataBytes = totalSamples * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);                      // PCM header size
        w.Write((short)1);                // PCM
        w.Write((short)1);                // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);          // byte rate
        w.Write((short)2);                // block align
        w.Write((short)16);               // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (short s in pcm) w.Write(s);

        w.Flush();
        stream.Position = 0;
        return stream;
    }

    private static void WriteTone(short[] buffer, int offset, int count, double frequency, int sampleRate)
    {
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / sampleRate;
            // Fade both ends of each tone, otherwise the discontinuity at the edges clicks.
            double envelope = Math.Min(1.0, Math.Min(i, count - i) / (sampleRate * 0.008));
            double sample = Math.Sin(2 * Math.PI * frequency * t) * envelope * 0.28;
            buffer[offset + i] = (short)(sample * short.MaxValue);
        }
    }
}
