# Branded loading

The owner is `Windows/ui/loading-ui.js`; `app.js` starts it on the fixed startup mark and disposes it immediately after the existing readiness transition. `Windows/StartupScreen.cs` draws the same contours before WebView2 exists. No connection, RPC or update result depends on an animation.

## Geometry and timing

Manrope 800, 2000 units/em: the k outline is copied from the bundled font; the period is a rigid 272-unit square. The compact wordmark's original tracking sets its x coordinate to 1141.1764705882354. At the baseline the existing contours share a subpixel overlap of 2.82353 glyph units; collision geometry allows only that original tolerance. No new squash/stretch, glow, gradients or limbs are added.

`Tools/loading_motion.py` is the canonical choreography. Generated JSON and classic JS contain identical samples at 120 Hz. Both clients linearly interpolate the same pose at elapsed milliseconds. The 120 Hz sampling is an asset resolution, not a requested rendering frequency or a timestep tied to display refresh.

Horizontal flight speed is constant between the takeoff and landing impulses. Vertical flight follows y = -v*t + g*t*t/2, with g = 24 square sides/s². A rebound retains 4.5% of height (about 21% vertical speed). Grounded rocking keeps its supporting corner fixed. Rollover integrates the corner inertia I = 2/3*m*s², a short driving torque, then gravity; a face impact dissipates remaining rotation. Translation between grounded stops uses minimum jerk. A nudge begins the letter's critically damped response at contact; the heavier letter moves much less than the square.

Anticipation precedes a full jump. Spacing follows gravity rather than a generic easing curve. Contact couples the two bodies. Follow-through is confined to small dissipating rebounds. Still intervals make gestures readable and prevent continuous visual noise. These are applications of the character-animation principles described by [Walt Disney Animation Studios](https://www.disneyanimation.com/process/animation/), not a claim of physically simulated living material. A self-propelled square is intentional artistic license.

Existing startup entrance 420 ms / cubic-bezier(.22,1,.36,1), label 360 ms with 80 ms delay, and the established readiness fade remain unchanged. Control motion tokens and modal behavior are untouched. New gesture periods are local choreography, including rest: 2.1-2.7 s; the exclusive scene is 3.5 s. Requested gesture changes wait for the current zero-pose/zero-speed boundary. Real operation completion has no such wait.

## Selection and lifecycle

Each operation begins with quiet motion. Remaining choices use calm 75%, lively 20%, joke 4.5%, rare 0.5%. Those are category draw probabilities, not wall-time proportions. When a joke category is exhausted it falls back to calm. A given joke cannot repeat in one operation; the 64-entry sequence then continues breathing. Compact owners choose only calm gestures.

Reduced motion shows the original static mark, with the existing loading label. Visibility, native host state and IntersectionObserver stop frame requests; clocks freeze while paused. Resume preserves the pose. `stop()` ends a loading session; the caller hides its slot as part of completion. `dispose()` also removes observers/listeners. Consumers should use a status label/aria-busy on the operation, keep the drawing aria-hidden, and reserve slot dimensions before starting.

The 47th large-logo click triggers the exclusive `protest` scene without changing the 443-click counter or hit target. It is never in the loading pool. No user-facing hint is shipped. The original meow is unchanged. If an entire new 443-click cycle is entered during the 3.5 s scene, the next 47-click scene queues at rest without snapping the visible pose or resetting the counter.

## Review and integration

`python Tools/loading_demo.py` produces `artifacts/loading-demo.html` with all gestures, compact samples, themes, slow playback and the exclusive scene. It embeds the production owner, generated poses and the bundled font. No network is needed. Copying the generated demo is not a second implementation.

Use `KotLoading.create(element, {compact: true})` for future compact slots; `start()`, `stop()` and `dispose()` belong to that operation. Only the startup cover is replaced here; other workflows are not redesigned.

Run `python Tests/loading_ui.py` after generating the demo, plus the full browser matrix. Native handoff, rasterization, window/tray visibility and Windows reduced motion require real WindowTests/WebView2 execution. Linux cross-compilation cannot establish those results.
