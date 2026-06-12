using System;
using System.IO;
using UnityEngine;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Runtime configuration loaded from <c>config.json</c> in the mod folder.
  /// Deserialized with <see cref="JsonUtility"/>, so fields must be public.
  /// A missing or malformed file falls back to defaults (window on).
  /// </summary>
  [Serializable]
  internal sealed class ProfilerConfig {

    /// <summary>When true, register the in-game profiler overlay (toggled with
    /// Alt+Shift+P). When false, the mod loads but binds nothing.</summary>
    public bool ShowProfilerWindow = true;

    /// <summary>When true, install/start/stop lines are written to Player.log.
    /// Warnings and errors always emit regardless.</summary>
    public bool LogToPlayerLog = true;

    /// <summary>
    /// Load <c>config.json</c> from the mod directory, or return defaults if
    /// it's absent or unreadable. Never throws.
    /// </summary>
    public static ProfilerConfig Load() {
      try {
        var path = ProfilerPaths.ConfigPath;
        if (File.Exists(path)) {
          var cfg = JsonUtility.FromJson<ProfilerConfig>(File.ReadAllText(path));
          if (cfg != null) {
            return cfg;
          }
          ProfilerLog.Warn("config.json parsed to null; using defaults.");
        }
      } catch (Exception ex) {
        ProfilerLog.Warn($"config.json load failed ({ex.Message}); using defaults.");
      }
      return new ProfilerConfig();
    }

  }

}
