# 09. Render, Camera, Frame Pacing

Scope for this pass: camera control, frame pacing, and render-side limits that are visible from the mod source and from `Assembly-CSharp.dll` without Unity source.

## Evidence reviewed

- `src/FramePacing.cs`
- `src/AutoLabBalancerPlugin.cs`
- Decompiled `C:\Program Files (x86)\Steam\steamapps\common\Project Hospital\ProjectHospital_Data\Managed\Assembly-CSharp.dll`

## What exists in the game

### Camera-side types

Only one gameplay camera controller showed up in the DLL:

- `CameraController`

Related helper:

- `IsometricCameraUtils`

Render-side classes that depend on the camera but are not pacing controls:

- `MapRenderer`
- `CharacterRenderer`
- `FullscreenImageEffect`

### What `CameraController` actually does

The controller is a direct `MonoBehaviour` that drives the main camera every frame:

- moves `Camera.main.transform` from keyboard, edge scrolling, and middle-mouse drag
- clamps movement through hospital bounds
- updates follow / panel state when the camera is manually moved
- supports zoom in/out, max zoom, and rotation clockwise/counter-clockwise
- uses `Camera.main.orthographicSize` as the visible scale
- uses `IsometricCameraUtils` for world rotation / inverse rotation

The important detail is that the camera is not abstracted behind a mod API. It is direct `Camera.main` manipulation in game code.

### What the render path depends on

`MapRenderer` and `CharacterRenderer` both consume camera state every frame:

- `MapRenderer.RenderMap()` reads `CameraController.sm_instance.GetZoomLevel()`
- `CharacterRenderer` uses `Camera.main.WorldToViewportPoint(...)` for culling
- `FullscreenImageEffect.OnRenderImage(...)` is a post-process blit path

That means camera state affects how much render work the game does, but the mod does not have a public switch to rewrite the renderer itself.

## What is реально possible

### 1) FPS cap / pacing

The current `FramePacingService` can safely control only public Unity properties:

- `Application.targetFrameRate`
- `QualitySettings.vSyncCount`
- `Time.maximumDeltaTime`

It also optionally uses the monitor refresh rate from `Screen.currentResolution.refreshRate`, clamped to `30..240`, with fallback to the manual cap when Unity reports `0`.

This is the practical pacing lever. Lower target FPS reduces how often camera update, render setup, and UI work run.

### 2) Stable restore on disable

The service captures the original values once and restores them when pacing is disabled. That is a real kill switch, not just a toggle.

### 3) Manual vs refresh-driven target

Both modes are viable:

- refresh-driven cap: matches the display when the reported refresh rate is sane
- manual cap: safer on odd displays, remote desktop, broken EDID, or when you want a hard test value

## What is not реально possible from this mod alone

- rewriting Unity's player loop or render loop
- changing the engine's camera render order
- forcing per-camera render throttling without adding new game hooks
- skipping `MapRenderer` / `CharacterRenderer` work by a pacing property alone
- changing `FullscreenImageEffect` cost without directly disabling or patching that component
- making `targetFrameRate` change the amount of per-frame work; it only changes how often the work runs

In short: pacing can reduce cadence, but it cannot make the internal render and camera code cheaper per frame.

## Recommended toggles

Keep the current config surface, but treat these as the meaningful controls:

- `Enabled`
- `EnableFramePacing`
- `FramePacingUseMonitorRefreshRate`
- `FramePacingTargetFrameRate`
- `FramePacingDisableVSync`
- `FramePacingMaximumDeltaTime`

Recommended defaults for broad compatibility:

- `EnableFramePacing = true`
- `FramePacingUseMonitorRefreshRate = true`
- `FramePacingDisableVSync = true`
- `FramePacingMaximumDeltaTime = 0.05`

Recommended fallback profile for unreliable refresh reporting:

- `FramePacingUseMonitorRefreshRate = false`
- `FramePacingTargetFrameRate = 60`

Recommended higher-refresh profile when the machine is stable:

- `FramePacingTargetFrameRate = 120` or `144`
- keep `FramePacingMaximumDeltaTime` conservative
- disable monitor-refresh mode if the refresh rate is unstable between modes

## Safety gates and kill switches

### Hard kill switch

Use `Enabled = false` or `EnableFramePacing = false`.

Current behavior: the service restores the previously captured `Application.targetFrameRate`, `QualitySettings.vSyncCount`, and `Time.maximumDeltaTime`.

### Safety gate: bad refresh reporting

If the monitor reports `0`, or if refresh mode behaves inconsistently across fullscreen/windowed transitions, turn off `FramePacingUseMonitorRefreshRate` and use the manual cap.

### Safety gate: vSync mismatch

If the game or driver stack ignores the FPS cap, or if tearing is unacceptable, set `FramePacingDisableVSync = false`. That lets vSync win instead of the cap.

### Safety gate: catch-up spikes

Keep `Time.maximumDeltaTime` clamped. Too high allows large post-stutter catch-up bursts; too low can cause time simulation to feel artificially slow after a hitch.

## Bottom line

For this project, the only robust pacing controls are the public Unity properties already used by `FramePacingService`. Camera/render internals in `Assembly-CSharp.dll` are real and visible, but they are not exposed as safe pacing toggles. The practical win is stable frame cadence, not renderer surgery.
