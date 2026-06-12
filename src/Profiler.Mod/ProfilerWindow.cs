using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Timberborn.RootProviders;
using Timberborn.SingletonSystem;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace SylvanGames.TimberbornProfiler {

  /// <summary>
  /// Floating, non-modal profiler overlay. Toggled with <b>Alt+Shift+P</b>.
  /// A single <b>Start / Stop</b> button drives an
  /// <see cref="AssemblyProfilerSession"/>: Start scans every loaded mod
  /// assembly, patches its tick/update methods, and begins timing; the table
  /// then shows, per assembly (rolled up) and per class within it, the
  /// avg / p99 / max CPU cost per active frame. The <b>Vanilla</b> toggle opts
  /// vanilla <c>Timberborn.*</c> code in too. <b>Clear</b> re-baselines,
  /// <b>Copy</b> puts the table on the clipboard.
  ///
  /// <para>Mounted on its own high-sort-order <see cref="UIDocument"/> via
  /// <see cref="RootVisualElementProvider.CreateEmpty"/> (not the dialog stack),
  /// so it overlays the UI without gating game input. Repaints at ~2 Hz while
  /// visible; hidden windows do no work. Draggable by its header. The overlay
  /// pattern follows Keystone's perf window.</para>
  /// </summary>
  public sealed class ProfilerWindow : ILoadableSingleton, IUpdatableSingleton {

    #region Constants

    private const float WindowWidth = 1000f;
    private const float WindowHeight = 620f;
    private const float HeaderHeight = 24f;
    private const float InitialLeft = 60f;
    private const float InitialTop = 60f;

    /// <summary>Refresh cadence in frames. 30 @ 60 fps ≈ 2 Hz.</summary>
    private const int RefreshFrames = 30;

    /// <summary>How long the "Copied!" confirmation stays up, in frames.</summary>
    private const int CopyFlashFrames = 60;

    // Monospace column widths.
    private const int NameWidth = 46;
    private const int NumWidth = 9;
    private const int CallsWidth = 11;

    private static readonly string[] MonoFontCandidates =
        { "Consolas", "Courier New", "Liberation Mono", "DejaVu Sans Mono", "Menlo" };

    #endregion

    #region Fields

    private readonly RootVisualElementProvider _rootProvider;
    private readonly AssemblyProfilerSession _session = new();
    private readonly StringBuilder _buffer = new();

    private VisualElement? _root;
    private VisualElement? _header;
    private Label? _content;
    private Label? _startStopButton;
    private Label? _vanillaButton;
    private Label? _copyButton;
    private Label? _statusLabel;

    /// <summary>Whether the next Start also patches vanilla <c>Timberborn.*</c>
    /// code. Off by default (large patch surface + overhead).</summary>
    private bool _includeVanilla;

    private int _framesSinceRefresh;
    private int _copyFlashFramesLeft;

    // Drag state.
    private bool _dragging;
    private Vector2 _grabOffset;

    #endregion

    #region Construction

    public ProfilerWindow(RootVisualElementProvider rootProvider) {
      _rootProvider = rootProvider;
    }

    #endregion

    #region ILoadableSingleton

    /// <inheritdoc />
    public void Load() {
      var document = _rootProvider.CreateEmpty("TimberbornProfilerWindow", 100);
      var canvas = document.rootVisualElement;

      _root = new VisualElement { name = "TimberbornProfilerRoot" };
      _root.style.position = Position.Absolute;
      _root.style.left = InitialLeft;
      _root.style.top = InitialTop;
      _root.style.width = WindowWidth;
      _root.style.height = WindowHeight;
      _root.style.backgroundColor = new Color(0f, 0f, 0f, 0.85f);
      SetUniformBorder(_root, 1, new Color(0.4f, 0.4f, 0.4f, 1f));
      _root.style.display = DisplayStyle.None; // start hidden

      BuildHeader();
      BuildContent();

      canvas.Add(_root);
    }

    private void BuildHeader() {
      _header = new VisualElement { name = "Header" };
      _header.style.height = HeaderHeight;
      _header.style.flexDirection = FlexDirection.Row;
      _header.style.alignItems = Align.Center;
      _header.style.justifyContent = Justify.SpaceBetween;
      _header.style.paddingLeft = 8;
      _header.style.paddingRight = 4;
      _header.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
      _header.RegisterCallback<PointerDownEvent>(OnHeaderPointerDown);
      _header.RegisterCallback<PointerMoveEvent>(OnHeaderPointerMove);
      _header.RegisterCallback<PointerUpEvent>(OnHeaderPointerUp);

      var title = new Label("Timberborn Profiler  (Alt+Shift+P)");
      title.style.color = Color.white;
      title.style.unityFontStyleAndWeight = FontStyle.Bold;
      _header.Add(title);

      var buttons = new VisualElement();
      buttons.style.flexDirection = FlexDirection.Row;
      buttons.style.alignItems = Align.Center;

      _vanillaButton = MakeHeaderButton("Vanilla: off", OnVanillaToggleClicked);
      _vanillaButton.tooltip = "Include vanilla Timberborn.* code (large patch surface — adds overhead).";
      buttons.Add(_vanillaButton);

      _startStopButton = MakeHeaderButton("Start", OnStartStopClicked);
      _startStopButton.style.color = new Color(0.55f, 0.95f, 0.55f, 1f);
      buttons.Add(_startStopButton);
      buttons.Add(MakeHeaderButton("Clear", OnClearClicked));
      _copyButton = MakeHeaderButton("Copy", OnCopyClicked);
      buttons.Add(_copyButton);

      var close = MakeHeaderButton("×", () => SetVisible(false));
      close.style.fontSize = 18;
      buttons.Add(close);

      _header.Add(buttons);
      _root!.Add(_header);
    }

    private static Label MakeHeaderButton(string text, System.Action onClick) {
      var label = new Label(text);
      label.style.color = Color.white;
      label.style.fontSize = 12;
      label.style.paddingLeft = 6;
      label.style.paddingRight = 8;
      label.RegisterCallback<PointerDownEvent>(ev => {
        onClick();
        ev.StopPropagation();
      });
      return label;
    }

    private void BuildContent() {
      var monoFont = ResolveMonoFont();
      if (monoFont == null) {
        ProfilerLog.Warn("Profiler window: no monospace OS font found; columns will not align. "
                         + "Tried: " + string.Join(", ", MonoFontCandidates) + ".");
      }

      _statusLabel = new Label("Stopped — click Start to begin profiling.");
      _statusLabel.style.color = new Color(0.8f, 0.8f, 0.8f, 1f);
      _statusLabel.style.paddingLeft = 8;
      _statusLabel.style.paddingTop = 4;
      _statusLabel.style.paddingBottom = 2;
      _statusLabel.style.fontSize = 12;
      _root!.Add(_statusLabel);

      var scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
      scroll.style.flexGrow = 1;
      scroll.style.paddingLeft = 8;
      scroll.style.paddingRight = 8;
      scroll.style.paddingBottom = 8;

      _content = new Label("(no samples yet)");
      _content.style.color = Color.white;
      _content.style.whiteSpace = WhiteSpace.Pre;
      _content.style.fontSize = 12;
      ApplyMonoFont(_content, monoFont);
      scroll.Add(_content);
      _root!.Add(scroll);
    }

    #endregion

    #region IUpdatableSingleton

    /// <inheritdoc />
    public void UpdateSingleton() {
      var keyboard = Keyboard.current;
      if (keyboard != null
          && keyboard.pKey.wasPressedThisFrame
          && (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed)
          && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed)) {
        SetVisible(!IsVisible);
      }

      if (!IsVisible) {
        return;
      }

      if (_copyFlashFramesLeft > 0 && --_copyFlashFramesLeft == 0 && _copyButton != null) {
        _copyButton.text = "Copy";
      }

      if (++_framesSinceRefresh < RefreshFrames) {
        return;
      }
      _framesSinceRefresh = 0;
      Render();
    }

    #endregion

    #region Button handlers

    private void OnStartStopClicked() {
      if (_session.IsRunning) {
        _session.Stop();
      } else {
        _session.Start(_includeVanilla);
      }
      UpdateStartStopButton();
      _framesSinceRefresh = RefreshFrames; // force immediate repaint
    }

    private void OnVanillaToggleClicked() {
      _includeVanilla = !_includeVanilla;
      UpdateVanillaButton();
      // The patch surface is fixed at Start, so if we're already running,
      // re-apply with the new scope (this resets the stats — unavoidable, the
      // patched method set changed).
      if (_session.IsRunning) {
        _session.Stop();
        _session.Start(_includeVanilla);
        UpdateStartStopButton();
      }
      _framesSinceRefresh = RefreshFrames;
    }

    private void UpdateVanillaButton() {
      if (_vanillaButton == null) {
        return;
      }
      _vanillaButton.text = _includeVanilla ? "Vanilla: on" : "Vanilla: off";
      _vanillaButton.style.color = _includeVanilla
          ? new Color(0.95f, 0.80f, 0.35f, 1f) // amber: heavier mode is engaged
          : new Color(0.7f, 0.7f, 0.7f, 1f);
    }

    private void OnClearClicked() {
      _session.Clear();
      _framesSinceRefresh = RefreshFrames;
    }

    private void OnCopyClicked() {
      GUIUtility.systemCopyBuffer = _content?.text ?? string.Empty;
      if (_copyButton != null) {
        _copyButton.text = "Copied!";
        _copyFlashFramesLeft = CopyFlashFrames;
      }
    }

    private void UpdateStartStopButton() {
      if (_startStopButton == null) {
        return;
      }
      var running = _session.IsRunning;
      _startStopButton.text = running ? "Stop" : "Start";
      _startStopButton.style.color = running
          ? new Color(0.95f, 0.55f, 0.55f, 1f)
          : new Color(0.55f, 0.95f, 0.55f, 1f);
    }

    #endregion

    #region Render

    private void Render() {
      if (_content == null) {
        return;
      }
      var snapshot = _session.Snapshot();

      if (_statusLabel != null) {
        _statusLabel.text = snapshot.Running
            ? $"Profiling: {snapshot.PatchedMethods} method(s) across {snapshot.PatchedAssemblies} assembly(ies)"
              + (snapshot.FailedToPatch > 0 ? $"  ({snapshot.FailedToPatch} unpatchable)" : "")
              + "   —   columns are ms per active frame"
            : (snapshot.PatchedMethods > 0
                ? "Stopped (showing last run). Click Start to re-profile."
                : "Stopped — click Start to begin profiling.");
      }

      _buffer.Clear();
      if (snapshot.Assemblies.Count == 0) {
        _buffer.Append(snapshot.Running
            ? "(running — no tick/update activity sampled yet)"
            : "(no samples)");
        _content.text = _buffer.ToString();
        return;
      }

      AppendHeaderRow();
      foreach (var asm in snapshot.Assemblies) {
        AppendAssemblyRow(asm);
        foreach (var type in asm.Types) {
          AppendTypeRow(type);
        }
        _buffer.Append('\n');
      }
      if (_buffer.Length > 0 && _buffer[_buffer.Length - 1] == '\n') {
        _buffer.Length--;
      }
      _content.text = _buffer.ToString();
    }

    private void AppendHeaderRow() {
      _buffer.Append("Assembly / class".PadRight(NameWidth))
             .Append("avg".PadLeft(NumWidth))
             .Append("p99".PadLeft(NumWidth))
             .Append("max".PadLeft(NumWidth))
             .Append("calls".PadLeft(CallsWidth))
             .Append('\n');
    }

    private void AppendAssemblyRow(AssemblyProfilerSession.AssemblyRow asm) {
      _buffer.Append(Truncate(asm.Assembly, NameWidth, keepRight: false).PadRight(NameWidth))
             .Append(Ms(asm.AvgMs))
             .Append(Ms(asm.P99Ms))
             .Append(Ms(asm.MaxMs))
             .Append(asm.TotalCalls.ToString("N0", CultureInfo.InvariantCulture).PadLeft(CallsWidth))
             .Append("   [").Append(asm.TypeCount).Append(" class(es)]")
             .Append('\n');
    }

    private void AppendTypeRow(AssemblyProfilerSession.TypeRow type) {
      var name = "  " + Truncate(type.Type, NameWidth - 2, keepRight: true);
      _buffer.Append(name.PadRight(NameWidth))
             .Append(Ms(type.AvgMs))
             .Append(Ms(type.P99Ms))
             .Append(Ms(type.MaxMs))
             .Append(type.TotalCalls.ToString("N0", CultureInfo.InvariantCulture).PadLeft(CallsWidth))
             .Append('\n');
    }

    private static string Ms(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture).PadLeft(NumWidth);

    /// <summary>Truncate to <paramref name="width"/>, marking the cut with '…'.
    /// Type names keep their right end (the class name is the useful part);
    /// assembly names keep their left end.</summary>
    private static string Truncate(string s, int width, bool keepRight) {
      if (s.Length <= width) {
        return s;
      }
      return keepRight
          ? "…" + s.Substring(s.Length - (width - 1))
          : s.Substring(0, width - 1) + "…";
    }

    #endregion

    #region Visibility

    private bool IsVisible => _root != null && _root.resolvedStyle.display == DisplayStyle.Flex;

    private void SetVisible(bool visible) {
      if (_root == null) {
        return;
      }
      _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
      if (visible) {
        UpdateStartStopButton();
        UpdateVanillaButton();
        _framesSinceRefresh = RefreshFrames; // force immediate paint
      }
    }

    #endregion

    #region Drag

    private void OnHeaderPointerDown(PointerDownEvent ev) {
      if (ev.button != 0 || _root == null || _header == null) {
        return;
      }
      _dragging = true;
      _grabOffset = new Vector2(
          ev.position.x - _root.resolvedStyle.left,
          ev.position.y - _root.resolvedStyle.top);
      _header.CapturePointer(ev.pointerId);
      ev.StopPropagation();
    }

    private void OnHeaderPointerMove(PointerMoveEvent ev) {
      if (!_dragging || _root == null) {
        return;
      }
      _root.style.left = ev.position.x - _grabOffset.x;
      _root.style.top = ev.position.y - _grabOffset.y;
      ev.StopPropagation();
    }

    private void OnHeaderPointerUp(PointerUpEvent ev) {
      if (!_dragging || _header == null) {
        return;
      }
      _dragging = false;
      _header.ReleasePointer(ev.pointerId);
      ev.StopPropagation();
    }

    #endregion

    #region Style helpers

    private static void SetUniformBorder(VisualElement element, float width, Color color) {
      element.style.borderLeftWidth = width;
      element.style.borderRightWidth = width;
      element.style.borderTopWidth = width;
      element.style.borderBottomWidth = width;
      element.style.borderLeftColor = color;
      element.style.borderRightColor = color;
      element.style.borderTopColor = color;
      element.style.borderBottomColor = color;
    }

    /// <summary>Resolve an installed OS monospace font (or null). Only names
    /// confirmed installed are used: handing
    /// <see cref="Font.CreateDynamicFontFromOSFont(string, int)"/> a missing
    /// name yields a font that renders blank under UI Toolkit's SDF path, which
    /// is worse than the variable-width fallback.</summary>
    private static Font? ResolveMonoFont() {
      var installed = new HashSet<string>(
          Font.GetOSInstalledFontNames(), System.StringComparer.OrdinalIgnoreCase);
      foreach (var name in MonoFontCandidates) {
        if (installed.Contains(name)) {
          return Font.CreateDynamicFontFromOSFont(name, 12);
        }
      }
      return null;
    }

    private static void ApplyMonoFont(Label label, Font? font) {
      if (font == null) {
        return;
      }
      label.style.unityFont = font;
      label.style.unityFontDefinition = new StyleFontDefinition(font);
    }

    #endregion

  }

}
