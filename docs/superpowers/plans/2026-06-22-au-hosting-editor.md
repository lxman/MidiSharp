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

## Task 1 — Resolve the AU's Cocoa view

- [ ] Add to `AudioUnitAbi`: `AudioUnitCocoaViewInfo`, `kAudioUnitProperty_CocoaUI=31`, and the CFBundle/CFURL/
      CFString calls needed to load a bundle and look up an Obj-C class by name.
- [ ] Helper `TryCreateCocoaView(au) → NSView*`: `AudioUnitGetProperty(CocoaUI)`; if present, `CFBundleCreate`
      from the URL, get the named view-factory class, `[[cls alloc] init]`, `[factory uiViewForAudioUnit:au
      withSize:NSMakeSize(w,h)]` (via `objc_msgSend` from `MacArm/Cocoa.cs`); else return null. `CFRelease` the
      info's CF members.

## Task 2 — Generic-view fallback

- [ ] When `TryCreateCocoaView` returns null (no custom UI — Apple built-ins), create CoreAudioKit's generic
      `AUGenericView` bound to the AU. Confirm the exact init selector during implementation (§12 open item).
- [ ] The editor path always yields *some* `NSView` for an AU that exists.

## Task 3 — `IPluginGui` on the AU adapter

- [ ] Implement `IPluginGui` on `AudioUnitPlugin` (and expose it via `IHostedPlugin.Gui`): `HasEditor` true when
      an AU view is obtainable; `IsApiSupported("cocoa")` true (AU editors are Cocoa-only); `Create` obtains the
      view (Task 1/2) but does **not** parent it; `SetParent("cocoa", contentView)` adds the AU view as a subview
      of the host content `NSView`; `TryGetSize` from the view's `frame`; `Show`/`Hide`/`Destroy` toggle hidden /
      `removeFromSuperview` / release; `BindRunLoop`/`SetScale` are no-ops (AU self-drives the AppKit loop).
- [ ] Make `Gui` non-null only when a view is available, so non-GUI AUs report no editor.

## Task 4 — Verify embedding & acceptance

- [ ] Extend `tests/MidiSharp.Hosting.MacEditorHarness/Program.cs` with an `EmbedReal("AU", new AudioUnitFormat(),
      "Surge XT", "AU cocoa editor")` arm (reusing the existing `EmbedReal` shape): load the AU, open an
      `EditorSession` on the main thread, pump, assert `EmbeddedChildCount ≥ 1`. SKIP when no AU with a Cocoa view
      is installed.
- [ ] **Acceptance gate (spec §10, Plan C):** an AU with a Cocoa view embeds a child `NSView` through the
      unchanged `EditorSession` (`EmbeddedChildCount ≥ 1`); the generic-view fallback covers a built-in AU; the
      `MacEditorHarness` prints `PASS AU`. Solution builds **0/0**; CLAP/VST2/VST3 editor paths and the Cocoa
      backend are behaviorally unchanged.
- [ ] Update `docs/plugin-hosting-plan.md` (AU editor done; AU adapter complete) and `CHANGELOG.md`. Commit.
      **Do not merge/push unless asked.**

## Notes

- This slice touches the AU adapter and the harness **only**; if it needs any change to `EditorSession` or the
  Cocoa backend, that is a signal the format-agnostic seam is leaking and should be reconsidered, not patched
  around.
- AU v3 view hosting (`requestViewControllerWithCompletionHandler`) remains out of scope (spec §11).
