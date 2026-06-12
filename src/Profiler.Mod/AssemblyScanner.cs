using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Discovers, across every profilable assembly currently loaded, the per-tick
  /// and per-frame methods the profiler should time. By default "profilable"
  /// means mod code — everything that isn't the engine, the BCL, the DI
  /// framework, Harmony, this mod, or the vanilla game (see
  /// <see cref="IsAlwaysExcluded"/> and <c>VanillaPrefix</c>). The
  /// <c>includeVanilla</c> argument to <see cref="Discover"/> additionally opts
  /// <c>Timberborn.*</c> code in.
  ///
  /// <para>Two discovery shapes are handled:
  /// <list type="bullet">
  ///   <item><b>Interface contracts</b> (<c>ITickableSingleton</c>,
  ///   <c>IUpdatableSingleton</c>, …) — resolved through the type's interface
  ///   map, which is correct even for explicit implementations.</item>
  ///   <item><b>The <c>TickableComponent</c> base class</b> — per-entity ticks
  ///   are a virtual <c>Tick()</c> override, not an interface, so concrete
  ///   subclasses are found by assignability and their <c>Tick</c> resolved
  ///   directly.</item>
  /// </list></para>
  ///
  /// <para><b>What "mod code" means here.</b> A target is kept only when the
  /// <em>resolved method's declaring type</em> lives in a non-engine assembly.
  /// That excludes a mod type that subclasses a vanilla tickable without
  /// overriding the hot method (the body is vanilla — timing it would measure
  /// the engine, not the mod, and would patch a method shared with vanilla
  /// instances).</para>
  ///
  /// <para><b>Why name-prefix filtering.</b> Timberborn loads mod DLLs from
  /// memory, so <see cref="Assembly.Location"/> is empty and can't distinguish
  /// a mod from the engine. The assembly <em>name</em> is the only reliable
  /// signal, hence the prefix blocklist below. A mod whose assembly name
  /// happens to start with one of these prefixes would be missed — an accepted
  /// limitation of a name-based heuristic.</para>
  /// </summary>
  internal static class AssemblyScanner {

    #region Contracts

    /// <summary>One discoverable hot method: the declaring contract (interface
    /// or base class) resolved by name, the method to time, a short phase
    /// label for display, and whether the contract is an interface.</summary>
    private readonly struct Contract {
      public readonly string TypeName;
      public readonly string Method;
      public readonly string Phase;
      public readonly bool IsInterface;

      public Contract(string typeName, string method, string phase, bool isInterface) {
        TypeName = typeName;
        Method = method;
        Phase = phase;
        IsInterface = isInterface;
      }
    }

    /// <summary>The tick + update family, singletons and components. Tick is the
    /// simulation step (paused with the game); Update/LateUpdate are the Unity
    /// frame phases (run even while paused).</summary>
    private static readonly Contract[] Contracts = {
      new("Timberborn.TickSystem.ITickableSingleton", "Tick", "Tick/sgl", true),
      new("Timberborn.SingletonSystem.IUpdatableSingleton", "UpdateSingleton", "Update/sgl", true),
      new("Timberborn.SingletonSystem.ILateUpdatableSingleton", "LateUpdateSingleton", "LateUpdate/sgl", true),
      new("Timberborn.TickSystem.TickableComponent", "Tick", "Tick/cmp", false),
      new("Timberborn.BaseComponentSystem.IUpdatableComponent", "Update", "Update/cmp", true),
      new("Timberborn.BaseComponentSystem.ILateUpdatableComponent", "LateUpdate", "LateUpdate/cmp", true),
    };

    #endregion

    #region Mod-code filter

    /// <summary>Assembly-name prefixes that are <em>never</em> profilable —
    /// the engine, the BCL, the DI framework, Harmony, and this mod itself.
    /// Matched case-insensitively against the simple assembly name.</summary>
    private static readonly string[] AlwaysExcludedPrefixes = {
      "UnityEngine",
      "Unity.",
      "System",
      "mscorlib",
      "netstandard",
      "Mono.",
      "Bindito.",
      "0Harmony",
      "Newtonsoft",
      "SylvanGames.TimberbornProfiler", // never profile ourselves
    };

    /// <summary>Vanilla game code. Excluded by default — its tick/update
    /// surface is huge (hundreds of types, thousands of component instances per
    /// tick) so timing all of it adds real overhead and can distort the
    /// numbers. Included only when the window's "Vanilla" toggle is on.</summary>
    private const string VanillaPrefix = "Timberborn.";

    /// <summary>True if <paramref name="assembly"/> is engine/BCL/DI/Harmony/
    /// this mod — not profilable under any setting.</summary>
    public static bool IsAlwaysExcluded(Assembly assembly) {
      var name = assembly.GetName().Name ?? string.Empty;
      foreach (var prefix in AlwaysExcludedPrefixes) {
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
      }
      return false;
    }

    /// <summary>True if <paramref name="assembly"/> should be skipped given the
    /// vanilla setting: always-excluded sets, plus <c>Timberborn.*</c> unless
    /// <paramref name="includeVanilla"/> is set.</summary>
    private static bool IsExcluded(Assembly assembly, bool includeVanilla) {
      if (IsAlwaysExcluded(assembly)) {
        return true;
      }
      if (!includeVanilla) {
        var name = assembly.GetName().Name ?? string.Empty;
        if (name.StartsWith(VanillaPrefix, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }
      }
      return false;
    }

    #endregion

    #region Discovery

    /// <summary>One method to patch: the method itself plus its phase label.</summary>
    internal readonly struct PatchTarget {
      public readonly MethodInfo Method;
      public readonly string Phase;

      public PatchTarget(MethodInfo method, string phase) {
        Method = method;
        Phase = phase;
      }
    }

    /// <summary>
    /// Every distinct, patchable hot method declared by profilable code,
    /// de-duplicated across contracts (a method shared by several subtypes is
    /// returned once). With <paramref name="includeVanilla"/> set, vanilla
    /// <c>Timberborn.*</c> code is profiled too; otherwise only mod code.
    /// </summary>
    public static List<PatchTarget> Discover(bool includeVanilla) {
      var modAssemblies = AppDomain.CurrentDomain.GetAssemblies()
          .Where(a => !IsExcluded(a, includeVanilla))
          .ToArray();

      var seen = new HashSet<MethodBase>();
      var targets = new List<PatchTarget>();

      foreach (var contract in Contracts) {
        var contractType = AccessTools.TypeByName(contract.TypeName);
        if (contractType == null) {
          ProfilerLog.Warn($"Contract '{contract.TypeName}' not found — {contract.Phase} skipped.");
          continue;
        }
        foreach (var assembly in modAssemblies) {
          foreach (var type in SafeGetTypes(assembly)) {
            if (!IsConcreteCandidate(type, contractType)) {
              continue;
            }
            var method = contract.IsInterface
                ? ResolveInterfaceImpl(type, contractType, contract.Method)
                : AccessTools.Method(type, contract.Method);
            if (method is not { IsAbstract: false }) {
              continue;
            }
            // Keep only methods whose body is profilable. Catches a mod type
            // that inherits a vanilla Tick without overriding it (declaring
            // type is vanilla → skipped unless includeVanilla).
            if (method.DeclaringType == null || IsExcluded(method.DeclaringType.Assembly, includeVanilla)) {
              continue;
            }
            if (seen.Add(method)) {
              targets.Add(new PatchTarget(method, contract.Phase));
            }
          }
        }
      }

      return targets;
    }

    /// <summary>True if <paramref name="type"/> is a concrete, non-generic type
    /// that satisfies <paramref name="contractType"/> (implements the interface
    /// or extends the base class).</summary>
    private static bool IsConcreteCandidate(Type? type, Type contractType) {
      return type is { IsInterface: false, IsAbstract: false, IsGenericTypeDefinition: false }
          && !type.ContainsGenericParameters
          && contractType.IsAssignableFrom(type)
          && type != contractType;
    }

    /// <summary>The actual method <paramref name="type"/> uses to satisfy
    /// <paramref name="contract"/>.<paramref name="methodName"/> — via the
    /// interface map (correct for explicit implementations), falling back to a
    /// name lookup.</summary>
    private static MethodInfo? ResolveInterfaceImpl(Type type, Type contract, string methodName) {
      try {
        var map = type.GetInterfaceMap(contract);
        for (var i = 0; i < map.InterfaceMethods.Length; i++) {
          if (map.InterfaceMethods[i].Name == methodName) {
            return map.TargetMethods[i];
          }
        }
      } catch {
        // Some constructed / odd types reject GetInterfaceMap; fall through.
      }
      return AccessTools.Method(type, methodName);
    }

    /// <summary>Types in <paramref name="assembly"/>, tolerating a partially
    /// loaded assembly (returns the types that did load).</summary>
    private static IEnumerable<Type> SafeGetTypes(Assembly assembly) {
      try {
        return assembly.GetTypes();
      } catch (ReflectionTypeLoadException ex) {
        return ex.Types.Where(t => t != null)!;
      } catch {
        return Array.Empty<Type>();
      }
    }

    #endregion

  }

}
