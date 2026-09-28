# Portable parity and Apple follow-up

The owner selected the Apple build and remaining-functionality work after 0.9.
The four verified 0.9 artifacts are immutable rollback/reference checkpoints.
Do not update the installed Windows host, publish the repository, contact anyone,
or replace physical/device acceptance with synthetic evidence.

## Current implementation work

1. Negotiate the existing Windows display-list/selection protocol in portable
   hosts and viewers. Release held input, clear the previous image, and require
   a new display generation before allowing input after selection. Expose this
   through the desktop UI and mobile bridge/UI without a new wire version.
2. Remove fixed 16/33 ms presentation limits. Desktop rendering should wake for
   changed content; mobile presentation should follow display callbacks. Keep
   only one pending image/presentation task and keep status/audio work separate.
   Hardware/network frame rates remain measurements, not implied guarantees.
3. Audit Apple packaging and strengthen preflight, native/simulator checks and
   artifact verification. The owner explicitly confirmed there is no Apple device
   and accepts source/build preparation without Apple compilation or execution.
   State this limitation in the repository and future release notes. Do not keep
   requesting a Mac or treat source-only checks as an Apple build.

Affected systems: shared session state, wire tool negotiation, monitor capture
and input bounds, C ABI state/actions, Android Activity/service presentation,
iOS SwiftUI presentation, desktop viewport wakeups and Apple packaging.

Acceptance requires authenticated synthetic selection with negative origins and
different sizes/refresh rates, stale input rejection, held-key release, unknown
display failure without session loss, recovery-state isolation, bounded queues,
Windows interoperability and available native/platform compilation. Device-only
checks remain separate. Preserve prior failed evidence when correcting a test.

## Remaining parity ledger

Portable folder jobs/resume, collaboration/annotation, printing, high-refresh
recording, mobile host roles, Wayland control and distribution infrastructure
remain tracked in FEATURES.md and RELEASE-GATES.json. They are not silently
removed from the owner's scope or marked complete by this follow-up.

The requested personal message is a draft only. No message has been sent.

## Presentation references

- Android [Choreographer](https://developer.android.com/reference/android/view/Choreographer):
  request the next display callback explicitly and remove callbacks while paused.
- Apple [ProMotion guidance](https://developer.apple.com/documentation/quartzcore/optimizing-iphone-and-ipad-apps-to-support-promotion-displays):
  use CADisplayLink and opt into supported high refresh through the application
  property. The OS and display hardware still determine available presentation rates.
- The pinned eframe 0.33.3 immediate-viewport implementation routes child repaint
  requests through the parent. A child frame wakeup is not limited by the separate
  100 ms status fallback in the dashboard. This is source evidence, not a physical
  multi-window/high-refresh benchmark.
