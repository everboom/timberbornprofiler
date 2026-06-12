using System;
using System.IO;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Filesystem locations for the mod. <c>config.json</c> lives in the mod
  /// folder.
  ///
  /// <para>The folder is supplied by Timberborn via
  /// <c>IModEnvironment.ModPath</c> and set once at startup through
  /// <see cref="Initialize"/>. We must <em>not</em> derive it from
  /// <c>Assembly.Location</c>: Timberborn loads mod DLLs from a byte array, so
  /// <c>Location</c> is empty and <c>Path.GetDirectoryName("")</c> throws.</para>
  /// </summary>
  internal static class ProfilerPaths {

    private static string? _modDir;

    /// <summary>Record the mod directory (from <c>IModEnvironment.ModPath</c>).
    /// Call once at startup before any other member is used.</summary>
    public static void Initialize(string modDir) => _modDir = modDir;

    /// <summary>Directory containing the mod (DLL, manifest, config).</summary>
    public static string ModDir =>
        _modDir ?? throw new InvalidOperationException(
            "ProfilerPaths not initialized — call Initialize(modEnvironment.ModPath) at startup.");

    /// <summary>Path to <c>config.json</c> beside the mod DLL.</summary>
    public static string ConfigPath => Path.Combine(ModDir, "config.json");

  }

}
