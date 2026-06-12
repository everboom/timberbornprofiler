using UnityEngine;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Thin wrapper over <see cref="UnityEngine.Debug"/> so every line carries a
  /// consistent prefix and informational logging can be silenced via config
  /// without affecting warnings/errors.
  /// </summary>
  internal static class ProfilerLog {

    private const string Prefix = "[TimberbornProfiler] ";

    /// <summary>When false, <see cref="Info"/> is suppressed. Warnings and
    /// errors always emit. Set from <c>config.json</c> at startup.</summary>
    public static bool InfoEnabled = true;

    /// <summary>Routine informational line (gated by <see cref="InfoEnabled"/>).</summary>
    public static void Info(string message) {
      if (InfoEnabled) {
        Debug.Log(Prefix + message);
      }
    }

    /// <summary>Recoverable problem (e.g. a contract type not found).</summary>
    public static void Warn(string message) => Debug.LogWarning(Prefix + message);

    /// <summary>Unexpected failure.</summary>
    public static void Error(string message) => Debug.LogError(Prefix + message);

  }

}
