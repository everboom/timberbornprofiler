using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// On-demand CPU profiler over loaded <em>mod</em> code. <see cref="Start"/>
  /// discovers every mod tick/update method (<see cref="AssemblyScanner"/>),
  /// wraps each in a Harmony timing patch, and begins accumulating per-frame
  /// cost; <see cref="Stop"/> unpatches and freezes the numbers for reading.
  ///
  /// <para><b>Sample model.</b> Each patched call adds its elapsed stopwatch
  /// ticks into a per-method accumulator. Once per Unity frame
  /// (<see cref="FrameDriver.FrameEnded"/>, on <c>LateUpdate</c>), the
  /// accumulators are drained: every <em>type</em> that ran this frame
  /// contributes one sample equal to its summed ms, pushed into that type's
  /// <see cref="RingStats"/>; likewise every <em>assembly</em> contributes one
  /// sample equal to the sum across its types. avg / p99 / max are then read
  /// per type and per assembly from their own rings. Per-frame totals add, so
  /// the assembly figure is a true roll-up of its classes; p99 must be tracked
  /// separately at each level (you can't average two p99s), which is exactly
  /// why each level keeps its own ring.</para>
  ///
  /// <para><b>Concurrency.</b> Patch bodies are <c>static</c> (Harmony
  /// requirement) and may fire from background threads — Timberborn can tick
  /// components off the main thread. The bucket dictionary is built in full at
  /// <see cref="Start"/> and never structurally mutated while live, so
  /// concurrent reads are safe; the per-frame counters are bumped with
  /// <see cref="Interlocked"/> and drained with
  /// <see cref="Interlocked.Exchange(ref long, long)"/>. The frame flush and
  /// the window's snapshot both run on the Unity main thread (LateUpdate vs
  /// Update), so they never overlap and need no lock between them.</para>
  ///
  /// <para><b>Timing fidelity.</b> The measured span is only the original
  /// method body (prefix stamps the start as its last act; postfix reads the
  /// end first). All bookkeeping is outside that span.</para>
  ///
  /// <para><b>LateUpdate caveat.</b> The flush runs from <c>FrameDriver</c>'s
  /// own <c>LateUpdate</c>, whose ordering against game components'
  /// <c>LateUpdate</c> is undefined, so a few component <c>LateUpdate</c> calls
  /// can roll into the next frame's sample. Acceptable for a profiler; if counts
  /// ever look split, switch the flush trigger rather than mask it.</para>
  /// </summary>
  internal sealed class AssemblyProfilerSession {

    #region Harmony id

    private const string HarmonyId = "SylvanGames.TimberbornProfiler";

    #endregion

    #region Aggregates

    /// <summary>Rolling stats + lifetime call total for one assembly.</summary>
    private sealed class AsmAgg {
      public readonly string Name;
      public readonly RingStats Stats = new();
      public long TotalCalls;
      public AsmAgg(string name) => Name = name;
    }

    /// <summary>Rolling stats + lifetime call total for one declaring type,
    /// plus a back-reference to its assembly aggregate.</summary>
    private sealed class TypeAgg {
      public readonly Type Type;
      public readonly AsmAgg Asm;
      public readonly RingStats Stats = new();
      public long TotalCalls;
      public int MethodCount;
      public TypeAgg(Type type, AsmAgg asm) {
        Type = type;
        Asm = asm;
      }
    }

    /// <summary>Per-method accumulator. Frame counters are bumped by the static
    /// timing postfix (possibly off-thread) and drained by the main-thread
    /// flush.</summary>
    private sealed class MethodBucket {
      public readonly TypeAgg Type;
      public readonly string Phase;
      public long FrameTicks;
      public long FrameCalls;
      public MethodBucket(TypeAgg type, string phase) {
        Type = type;
        Phase = phase;
      }
    }

    #endregion

    #region State

    private readonly Harmony _harmony = new(HarmonyId);
    private readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;

    private readonly Dictionary<Type, TypeAgg> _types = new();
    private readonly Dictionary<string, AsmAgg> _asms = new();

    // Scratch reused each frame so the flush allocates nothing.
    private readonly Dictionary<TypeAgg, double> _frameTypeMs = new();
    private readonly Dictionary<AsmAgg, double> _frameAsmMs = new();

    /// <summary>The live bucket map. Static so the patch postfix can reach it;
    /// non-null only while profiling. Set once at <see cref="Start"/>, cleared
    /// at <see cref="Stop"/>; never structurally mutated in between.</summary>
    private static Dictionary<MethodBase, MethodBucket>? _buckets;

    private bool _running;
    private int _patchedMethodCount;
    private int _patchedAssemblyCount;
    private int _failedCount;

    #endregion

    #region Public surface

    /// <summary>Whether patches are currently installed and accumulating.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Discover tick/update methods, patch them, and begin accumulating. Clears
    /// any stats from a previous run. No-op if already running. Returns the
    /// number of methods patched (0 if nothing patchable was found).
    /// </summary>
    /// <param name="includeVanilla">Also profile vanilla <c>Timberborn.*</c>
    /// code (large patch surface + overhead); otherwise mod code only.</param>
    public int Start(bool includeVanilla) {
      if (_running) {
        return _patchedMethodCount;
      }

      _types.Clear();
      _asms.Clear();
      _patchedMethodCount = 0;
      _failedCount = 0;

      var targets = AssemblyScanner.Discover(includeVanilla);
      if (targets.Count == 0) {
        ProfilerLog.Warn("No tick/update methods found to profile "
                         + (includeVanilla
                             ? "(unexpected with vanilla included — game version may have changed)."
                             : "(no content mods installed, or their hot methods aren't tick/update)."));
        return 0;
      }

      var prefix = new HarmonyMethod(typeof(AssemblyProfilerSession), nameof(TimerPrefix));
      var postfix = new HarmonyMethod(typeof(AssemblyProfilerSession), nameof(TimerPostfix));
      var buckets = new Dictionary<MethodBase, MethodBucket>(targets.Count);

      foreach (var target in targets) {
        var declaring = target.Method.DeclaringType!; // never null: AssemblyScanner filters nulls
        try {
          _harmony.Patch(target.Method, prefix: prefix, postfix: postfix);
        } catch (Exception ex) {
          _failedCount++;
          ProfilerLog.Warn($"Could not patch {declaring.FullName}.{target.Method.Name} — {ex.Message}");
          continue;
        }
        var typeAgg = GetOrAddType(declaring);
        typeAgg.MethodCount++;
        buckets[target.Method] = new MethodBucket(typeAgg, target.Phase);
      }

      if (buckets.Count == 0) {
        ProfilerLog.Warn($"Discovered {targets.Count} method(s) but none were patchable.");
        return 0;
      }

      _patchedMethodCount = buckets.Count;
      _patchedAssemblyCount = _asms.Count;
      _buckets = buckets;
      _running = true;

      FrameDriver.EnsureExists();
      FrameDriver.FrameEnded += OnFrameEnd;

      ProfilerLog.Info(
          $"Started — patched {_patchedMethodCount} method(s) across "
          + $"{_patchedAssemblyCount} assembly(ies) "
          + (includeVanilla ? "(mods + vanilla)" : "(mods only)")
          + (_failedCount > 0 ? $", {_failedCount} unpatchable skipped" : "") + ".");
      return _patchedMethodCount;
    }

    /// <summary>Stop accumulating and remove all patches. Keeps the collected
    /// stats so the window can still display the final numbers. No-op if not
    /// running.</summary>
    public void Stop() {
      if (!_running) {
        return;
      }
      FrameDriver.FrameEnded -= OnFrameEnd;
      _running = false;
      _buckets = null;
      try {
        _harmony.UnpatchAll(HarmonyId);
      } catch (Exception ex) {
        ProfilerLog.Error($"UnpatchAll failed: {ex}");
      }
      ProfilerLog.Info("Stopped and unpatched.");
    }

    /// <summary>Re-baseline: drop all accumulated samples and call totals
    /// without unpatching. Lets the user wipe cold-start spikes once the game
    /// has settled. Safe to call while running.</summary>
    public void Clear() {
      foreach (var t in _types.Values) {
        t.Stats.Reset();
        t.TotalCalls = 0;
      }
      foreach (var a in _asms.Values) {
        a.Stats.Reset();
        a.TotalCalls = 0;
      }
    }

    #endregion

    #region Frame flush

    /// <summary>Drain every per-method accumulator into per-type and per-assembly
    /// per-frame samples. Runs once per frame on the main thread.</summary>
    private void OnFrameEnd() {
      var buckets = _buckets;
      if (buckets == null) {
        return;
      }
      _frameTypeMs.Clear();
      _frameAsmMs.Clear();

      foreach (var bucket in buckets.Values) {
        var ticks = Interlocked.Exchange(ref bucket.FrameTicks, 0L);
        var calls = Interlocked.Exchange(ref bucket.FrameCalls, 0L);
        if (calls == 0L) {
          continue;
        }
        var ms = ticks * _msPerTick;
        var type = bucket.Type;
        type.TotalCalls += calls;
        type.Asm.TotalCalls += calls;
        _frameTypeMs.TryGetValue(type, out var tMs);
        _frameTypeMs[type] = tMs + ms;
        _frameAsmMs.TryGetValue(type.Asm, out var aMs);
        _frameAsmMs[type.Asm] = aMs + ms;
      }

      foreach (var entry in _frameTypeMs) {
        entry.Key.Stats.Add(entry.Value);
      }
      foreach (var entry in _frameAsmMs) {
        entry.Key.Stats.Add(entry.Value);
      }
    }

    #endregion

    #region Patch bodies (static)

    /// <summary>Stamp the start time as the prefix's final act.</summary>
    public static void TimerPrefix(out long __state) => __state = Stopwatch.GetTimestamp();

    /// <summary>Read the end time first, then attribute the elapsed span to the
    /// method's bucket. Guarded against a teardown race (buckets nulled by
    /// <see cref="Stop"/> while a patched call is in flight) — a sample lost
    /// during unpatch is expected and harmless, not a masked error.</summary>
    public static void TimerPostfix(long __state, MethodBase __originalMethod) {
      var elapsed = Stopwatch.GetTimestamp() - __state;
      var buckets = _buckets;
      if (buckets == null || !buckets.TryGetValue(__originalMethod, out var bucket)) {
        return;
      }
      Interlocked.Add(ref bucket.FrameTicks, elapsed);
      Interlocked.Increment(ref bucket.FrameCalls);
    }

    #endregion

    #region Snapshot

    /// <summary>Immutable view of one assembly's rolled-up cost plus its
    /// per-class breakdown, for the window to render.</summary>
    internal sealed class AssemblyRow {
      public string Assembly = "";
      public double AvgMs;
      public double P99Ms;
      public double MaxMs;
      public long TotalCalls;
      public int TypeCount;
      public List<TypeRow> Types = new();
    }

    /// <summary>Immutable view of one class's rolled-up cost.</summary>
    internal sealed class TypeRow {
      public string Type = "";
      public double AvgMs;
      public double P99Ms;
      public double MaxMs;
      public long TotalCalls;
      public int MethodCount;
    }

    /// <summary>Full render model: status + the assembly rows, sorted
    /// alphabetically (assemblies, and classes within each).</summary>
    internal sealed class ProfilerSnapshot {
      public bool Running;
      public int PatchedMethods;
      public int PatchedAssemblies;
      public int FailedToPatch;
      public List<AssemblyRow> Assemblies = new();
    }

    /// <summary>Build the current render model. Main thread only (called from
    /// the window's update); cold path, so the per-call allocation/sort is
    /// fine.</summary>
    public ProfilerSnapshot Snapshot() {
      // Group types under their assembly once (avoids an O(types*asms) scan).
      var rowsByAsm = new Dictionary<AsmAgg, AssemblyRow>(_asms.Count);
      foreach (var asm in _asms.Values) {
        rowsByAsm[asm] = new AssemblyRow {
          Assembly = asm.Name,
          AvgMs = asm.Stats.Average,
          P99Ms = asm.Stats.P99,
          MaxMs = asm.Stats.Max,
          TotalCalls = asm.TotalCalls,
        };
      }
      foreach (var t in _types.Values) {
        // Skip classes that never fired — keeps the table to what's actually live.
        if (t.TotalCalls == 0L) {
          continue;
        }
        if (!rowsByAsm.TryGetValue(t.Asm, out var asmRow)) {
          continue;
        }
        asmRow.Types.Add(new TypeRow {
          Type = t.Type.FullName ?? t.Type.Name,
          AvgMs = t.Stats.Average,
          P99Ms = t.Stats.P99,
          MaxMs = t.Stats.Max,
          TotalCalls = t.TotalCalls,
          MethodCount = t.MethodCount,
        });
      }

      var assemblies = new List<AssemblyRow>(rowsByAsm.Count);
      foreach (var row in rowsByAsm.Values) {
        if (row.Types.Count == 0) {
          continue; // assembly with no live class this run
        }
        row.TypeCount = row.Types.Count;
        row.Types.Sort((a, b) => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase));
        assemblies.Add(row);
      }
      assemblies.Sort((a, b) => string.Compare(a.Assembly, b.Assembly, StringComparison.OrdinalIgnoreCase));

      return new ProfilerSnapshot {
        Running = _running,
        PatchedMethods = _patchedMethodCount,
        PatchedAssemblies = _patchedAssemblyCount,
        FailedToPatch = _failedCount,
        Assemblies = assemblies,
      };
    }

    #endregion

    #region Helpers

    private TypeAgg GetOrAddType(Type declaring) {
      if (_types.TryGetValue(declaring, out var existing)) {
        return existing;
      }
      var asmName = declaring.Assembly.GetName().Name ?? "?";
      if (!_asms.TryGetValue(asmName, out var asm)) {
        asm = new AsmAgg(asmName);
        _asms[asmName] = asm;
      }
      var agg = new TypeAgg(declaring, asm);
      _types[declaring] = agg;
      return agg;
    }

    #endregion

  }

}
