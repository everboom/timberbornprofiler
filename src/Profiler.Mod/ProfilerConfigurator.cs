using Bindito.Core;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Registers the interactive <see cref="ProfilerWindow"/> into Timberborn's
  /// "Game" DI scope (active while a settlement is loaded), so the overlay has
  /// the UI services it needs and is driven as an <c>ILoadableSingleton</c> /
  /// <c>IUpdatableSingleton</c>. Bindito auto-discovers <c>[Context]</c>
  /// configurators in loaded mod assemblies, so no manual hook-up is needed.
  ///
  /// <para>Gated by <see cref="ProfilerConfig.ShowProfilerWindow"/>: when off,
  /// the window isn't bound and nothing happens at runtime. The window manages
  /// its own Harmony patches on demand (Start/Stop), so nothing is patched until
  /// the user asks for it.</para>
  /// </summary>
  [Context("Game")]
  public sealed class ProfilerConfigurator : Configurator {

    /// <inheritdoc />
    protected override void Configure() {
      if (!ProfilerConfig.Load().ShowProfilerWindow) {
        return;
      }
      Bind<ProfilerWindow>().AsSingleton();
    }

  }

}
