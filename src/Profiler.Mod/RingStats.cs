using System;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Rolling-window timing stats for a single tracked unit (one type, or one
  /// assembly). Backing store is a fixed-capacity ring buffer of the most
  /// recent <see cref="Capacity"/> samples; <see cref="Average"/>,
  /// <see cref="P99"/> and <see cref="Max"/> walk the live portion of the ring
  /// on demand.
  ///
  /// <para>A <em>sample</em> here is one assembly's (or type's) summed
  /// per-frame cost in milliseconds — see <see cref="AssemblyProfilerSession"/>
  /// for how the per-frame totals are produced. P99 of those is "1% of active
  /// frames this unit cost more than X ms", the figure that maps to a frame-
  /// rate hitch.</para>
  ///
  /// <para><b>Hot vs cold.</b> <see cref="Add"/> is allocation-free (one array
  /// write plus a counter bump). The stat properties are cold paths — read by
  /// the profiler window at ~2 Hz — so a per-call scan/sort is fine.</para>
  ///
  /// <para><b>P99 index.</b> <c>P99 = sample[ceil(0.99 * count) - 1]</c> after
  /// sorting the live portion ascending. For count=100 that returns the 99th-
  /// largest sample, matching the conventional "99% of samples are at or below
  /// this value" framing.</para>
  ///
  /// <para>Pure data, no Unity dependency (trivially unit-testable on its own).</para>
  /// </summary>
  internal sealed class RingStats {

    #region Constants

    /// <summary>Ring-buffer size. ~3.5 s of active frames at 60 fps; long
    /// enough for a stable p99 without holding unbounded history. Tunable.</summary>
    public const int Capacity = 200;

    #endregion

    #region Fields

    private readonly double[] _samples = new double[Capacity];

    /// <summary>Total samples ever added; <see cref="SampleCount"/> caps this at <see cref="Capacity"/>.</summary>
    private int _totalAdded;

    /// <summary>Next write index (mod <see cref="Capacity"/>).</summary>
    private int _next;

    #endregion

    #region Properties

    /// <summary>Number of live samples currently in the ring (0..<see cref="Capacity"/>).</summary>
    public int SampleCount => _totalAdded < Capacity ? _totalAdded : Capacity;

    /// <summary>Arithmetic mean of the live samples in ms. 0 when empty.</summary>
    public double Average {
      get {
        var count = SampleCount;
        if (count == 0) {
          return 0.0;
        }
        var sum = 0.0;
        for (var i = 0; i < count; i++) {
          sum += _samples[i];
        }
        return sum / count;
      }
    }

    /// <summary>99th-percentile sample (ms). 0 when empty. Snapshot-sorts on demand.</summary>
    public double P99 => PercentileOrZero(0.99);

    /// <summary>Largest live sample (ms). 0 when empty.</summary>
    public double Max {
      get {
        var count = SampleCount;
        if (count == 0) {
          return 0.0;
        }
        var max = _samples[0];
        for (var i = 1; i < count; i++) {
          if (_samples[i] > max) {
            max = _samples[i];
          }
        }
        return max;
      }
    }

    #endregion

    #region Add / reset

    /// <summary>Record one per-frame total of <paramref name="elapsedMs"/>.
    /// Constant time, no allocation.</summary>
    public void Add(double elapsedMs) {
      _samples[_next] = elapsedMs;
      _next = (_next + 1) % Capacity;
      _totalAdded++;
    }

    /// <summary>Drop all samples and re-baseline (the window's Clear button).
    /// Leaves the backing array allocated.</summary>
    public void Reset() {
      _totalAdded = 0;
      _next = 0;
    }

    #endregion

    #region Percentile

    private double PercentileOrZero(double fraction) {
      var count = SampleCount;
      if (count == 0) {
        return 0.0;
      }
      // Snapshot the live samples and sort ascending. Allocates a small
      // double[] per call — acceptable on the cold panel-render path.
      var sorted = new double[count];
      Array.Copy(_samples, sorted, count);
      Array.Sort(sorted);
      var idx = (int)Math.Ceiling(fraction * count) - 1;
      if (idx < 0) {
        idx = 0;
      }
      if (idx >= count) {
        idx = count - 1;
      }
      return sorted[idx];
    }

    #endregion

  }

}
