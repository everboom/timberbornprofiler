using Timberborn.ModManagerScene;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Mod entry point. Timberborn invokes <see cref="StartMod"/> once at startup,
  /// after every mod's DLLs are loaded into the AppDomain but before any game
  /// scope spins up. There's no Harmony work to do here — the profiler patches
  /// on demand when the user clicks Start — so this only resolves the mod folder
  /// (for config) and applies the logging toggle. The window itself is
  /// registered into the Game scope by <see cref="ProfilerConfigurator"/>.
  /// </summary>
  public sealed class ProfilerModStarter : IModStarter {

    /// <inheritdoc />
    public void StartMod(IModEnvironment modEnvironment) {
      // Timberborn loads mod DLLs from memory, so Assembly.Location is empty;
      // the mod folder must come from the environment instead.
      ProfilerPaths.Initialize(modEnvironment.ModPath);

      var config = ProfilerConfig.Load();
      ProfilerLog.InfoEnabled = config.LogToPlayerLog;
      ProfilerLog.Info(config.ShowProfilerWindow
          ? "Loaded — profiler window enabled (toggle with Alt+Shift+P in-game)."
          : "Loaded — profiler window disabled via config (ShowProfilerWindow=false).");
    }

  }

}
