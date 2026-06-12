using System;
using UnityEngine;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// A persistent, hidden <see cref="MonoBehaviour"/> whose only job is to fire
  /// <see cref="FrameEnded"/> once per Unity frame from <c>LateUpdate</c>. The
  /// profiler session subscribes so it can flush its per-frame accumulators
  /// after that frame's simulation ticks have all run.
  ///
  /// <para>Why LateUpdate: Timberborn's tick system runs during the normal
  /// Update phase, so by LateUpdate every <c>TickableComponent.Tick</c> for the
  /// frame has executed and the accumulators hold a complete single-frame total.
  /// The flush then resets them, so there is exactly one flush boundary between
  /// any two frames' ticks.</para>
  /// </summary>
  internal sealed class FrameDriver : MonoBehaviour {

    /// <summary>Raised once per frame from <c>LateUpdate</c>.</summary>
    public static event Action? FrameEnded;

    /// <summary>The live driver, if one exists. Compared with Unity's null
    /// semantics so a destroyed driver re-creates on the next
    /// <see cref="EnsureExists"/>.</summary>
    private static FrameDriver? _instance;

    private void LateUpdate() {
      try {
        FrameEnded?.Invoke();
      } catch (Exception ex) {
        // A throwing subscriber must not take down the driver; log and keep
        // the heartbeat alive.
        ProfilerLog.Error($"FrameEnded subscriber threw: {ex}");
      }
    }

    /// <summary>
    /// Create the singleton driver GameObject and keep it across scene loads,
    /// if one isn't already live. Idempotent — safe to call on every profiler
    /// Start.
    /// </summary>
    public static void EnsureExists() {
      if (_instance != null) {
        return; // Unity-null aware: a destroyed driver compares == null and re-creates.
      }
      var go = new GameObject("TimberbornProfiler.FrameDriver") {
        hideFlags = HideFlags.HideAndDontSave
      };
      DontDestroyOnLoad(go);
      _instance = go.AddComponent<FrameDriver>();
    }

  }

}
