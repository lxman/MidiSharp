# AU Cocoa Editor (Plan C) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** open an **Audio Unit's native Cocoa editor** through the existing macOS `EditorHost`, by implementing
`IPluginGui` over `kAudioUnitProperty_CocoaUI` on the AU adapter. Verified against a real AU with a custom Cocoa
view (Surge XT's installed `.component`), with a generic-view fallback for AUs that ship no custom UI.

**Architecture:** the Cocoa editor backend (`MidiSharp.Hosting.EditorHost/MacArm/`) and `EditorSession` already
exist and embed a child `NSView` for CLAP/VST3/VST2 — **neither changes here**. The only new surface is AU-side
view vending: `AudioUnitGetProperty(kAudioUnitProperty_CocoaUI)` yields an `AudioUnitCocoaViewInfo { CFURLRef
bundleLocation; CFStringRef viewClassName[] }`; load the bundle, instantiate the factory class (conforms to
`AUCocoaUIBase`: `-(NSView*)uiViewForAudioUnit:withSize:`), get the `NSView`, and add it under the host content
view in `IPluginGui.SetParent`. AUs without a custom Cocoa UI (Apple built-ins) fall back to CoreAudioKit's
generic `AUGenericView`. Editor calls are **main-thread-only** (AppKit), so this is verified through the existing
main-thread `MacEditorHarness`, not xUnit — exactly as VST3/CLAP were.

**Spec:** `docs/superpowers/specs/2026-06-22-au-hosting-design.md` (§5.6, §6, §7, §10, §12). **Prerequisite:
Plan A** (the AU adapter & a loadable `AudioUnitPlugin`) merged. The Cocoa `EditorHost` backend is already
shipped.

**Tech Stack:** C# (net10.0), AudioToolbox/CoreAudioKit/CoreFoundation + `objc_msgSend`/AppKit P/Invoke (reusing
`MacArm/Cocoa.cs`), xUnit v3 for the AppKit-free pieces. **arm64, macOS-only.**

## Global Constraints

- Same as Plan A: `dotnet`, net10.0, **0/0**, dependency-free (system frameworks only), additive. **No change to
  `EditorSession` or the Cocoa backend** — the AU editor must ride them unchanged, proving the seam.
- **Main thread for all editor calls** (the harness `Main` is thread 0; the worker is too). The audio path
  (Plan A) is untouched.
- **Surge-dependent checks self-skip** when Surge's `.component` is absent (like `ClapLiveTests`).

> **Key finding (probe, 2026-06-22):** no third-party AU is needed. **Apple's own built-in AUs ship custom Cocoa
> views** via `kAudioUnitProperty_CocoaUI` — `AULowpass` and 22 others on this Mac — so the *custom-view* path is
> verifiable against a built-in. (Surge XT is also registered as an AU, AUv3-style with no `.component` file, but
> it isn't needed.) New interop lives in the AU adapter (`AuAppKit.cs` objc slice + `CoreFoundation` CFBundle
> calls), **not** by reusing EditorHost's internal `MacArm/Cocoa.cs` — keeps the adapter assembly-independent,
> consistent with `ClapAbi`/`Vst3Abi`.

## Task 1 — Resolve the AU's Cocoa view  ✅ 2026-06-22

- [x] `CoreFoundation` gained `CFBundleCreate`/`CFBundleLoadExecutable`; the `CocoaUI` property is read as raw
      bytes (`CFURLRef` bundle + `CFStringRef[]` class names), avoiding a fixed struct for the variable tail.
- [x] `TryCustomCocoaView()`: `AudioUnitGetProperty(PropCocoaUI)` → `CFBundleCreate`+`CFBundleLoadExecutable` →
      `objc_getClass(className)` → `[[cls alloc] init]` → `[factory uiViewForAudioUnit:au withSize:0]` (via the
      new `AuAppKit` objc slice). All the property's CF refs (`CFURLRef` + every `CFStringRef`) are released.

## Task 2 — Generic-view fallback  ✅ 2026-06-22

- [x] When no custom view, `GenericView()` builds CoreAudioKit's `[[AUGenericView alloc] initWithAudioUnit:au]`.
      CoreAudioKit is loaded lazily (only when the generic path runs).

## Task 3 — `IPluginGui` on the AU adapter  ✅ 2026-06-22

- [x] `AudioUnitPlugin : IHostedPlugin, IPluginGui`; `Gui => this`. `HasEditor => true` (every AU can present at
      least a generic view); `IsApiSupported` true only for `("cocoa", embedded)`; `Create` obtains the view
      (custom→generic) and retains it without parenting; `SetParent("cocoa", contentView)` adds it as a subview
      (the host view then retains it); `TryGetSize` from the view's `frame`; `Show`/`Hide` toggle hidden;
      `Destroy` removes + releases the view and releases the bundle; `BindRunLoop`/`SetScale` are no-ops.

## Task 4 — Verify embedding & acceptance  ✅ 2026-06-22

- [x] `MacEditorHarness` gained an `EmbedAu` arm that finds `AULowpass` via `Scan` (registry — AU has no file
      path) and reuses `DoEmbed`. Plus an AppKit-free xUnit test `Reports_a_cocoa_editor` (Gui/HasEditor/
      IsApiSupported surface).
- [x] **Acceptance gate (spec §10, Plan C):** `MacEditorHarness` prints **`PASS AU: 'AULowpass' embedded 1 child
      NSView(s)`** through the **unchanged** `EditorSession`/Cocoa backend. Solution **0/0**; hosting suite 34→… ;
      CLAP/VST2/VST3 editor paths and the Cocoa backend behaviorally unchanged.

> **Benign artifact:** hosting Apple's built-in AU views logs `objc[]: Class … implemented in both CoreAudioKit
> and CoreAudioAUUI` to stderr — Apple's own framework/bundle overlap (their AU view bundle pulls in
> CoreAudioKit), not from our code; the embed passes. Not silenceable from the host side.

**Plan C (AU Cocoa editor) is complete — the AU adapter (effects + instruments + editor) is done.**

> **Follow-up (2026-06-22, user-verified visually):** the shared Cocoa backend paints the editor window black
> (good for opaque dark plugin UIs), but many AU views are transparent and draw dark controls that then vanish
> on black. `AudioUnitPlugin.SetParent` now sets the AU editor window's background to the appearance-aware
> `[NSColor windowBackgroundColor]` (`AuAppKit.SetNeutralWindowBackground`). AU-only — CLAP/VST3 keep the black
> surround. Confirmed by eye against `AULowpass`. (Forced light/Aqua appearance is the fallback if dark mode
> still looks muddy; not needed.)

## Notes

- This slice touches the AU adapter and the harness **only**; if it needs any change to `EditorSession` or the
  Cocoa backend, that is a signal the format-agnostic seam is leaking and should be reconsidered, not patched
  around.
- AU v3 view hosting (`requestViewControllerWithCompletionHandler`) remains out of scope (spec §11).
