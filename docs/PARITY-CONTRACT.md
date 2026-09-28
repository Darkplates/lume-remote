# Release acceptance contract

This is a working contract, not a statement that feature parity has been achieved.
The owner requested a complete remote-access product before public release, including
Windows, Linux and macOS. The owner explicitly accepts preparing macOS/iOS source
and build scripts without compilation or execution because no Apple device is
available. Report this limitation in the README and release notes. It is an
exception, not evidence of working Apple applications. Source files, cross-compilation
and mocks do not establish platform support. Android/iOS and browser access are
tracked separately from desktop support.

The immutable 0.4-0.9 development archives are rollback/reference checkpoints. Work in
this checkout must not change the installed 0.3 service or interrupt its sessions.

| Area | Required behaviour | Acceptance evidence |
| --- | --- | --- |
| Connection | Guest consent, pairing, reconnect, direct P2P, relay fallback | Two physical PCs on different networks; loss/recovery; revocation |
| Lifetime | No time limits; background host; independent viewer windows | Idle soak; close dashboard; multiple destinations |
| Desktop | Quality/FPS choices, live monitor selection, scale/fullscreen | Negative-origin and mixed-resolution displays; coordinate isolation |
| Clipboard | Explicit send and receive; optional consented text sync | STA operation; busy/error handling; no reconnect leakage |
| Files | Files and folders both ways; progress/cancel/integrity; resume | Empty/nested folders; interruption; collisions; owner ACLs |
| Collaboration | Session chat and visible annotations | Bounded history; no input injection while drawing; disconnect cleanup |
| Media | Optional system audio; explicit microphone permission; recording | Synthetic codecs plus real hardware; bounded latency; valid recordings |
| Administration | Lock/restart/shutdown with explicit action; reconnect | Dry-run tests then dedicated machine; no authentication bypass |
| Printing | Print a remote document locally, with clear format support | Actual spool/print; no untrusted auto-execution |
| Hosts | Windows, Linux and macOS implementations | Native builds and OS-specific tests, with the explicitly untested Apple preparation exception above |
| Mobile/web | Scoped host/viewer roles and platform restrictions | Native device/browser validation before support claims |
| Distribution | Reproducible source/package, updates, signing | Fresh extraction; tamper rejection; publisher credentials |
| Public release | Documentation, licence/provenance, security review | Independent review; release gate report; owner approval |

Protocol additions are negotiated as version 4. Versions 1-3 retain their existing
wire layouts. Features are advertised only when their implementation is available
and the current session is authorized to use them. Unsupported operations must give
a useful error without terminating the desktop connection.

There is no promise that every feature ever shipped by another product is included.
The table defines the acceptance scope; newly discovered gaps are recorded before
publication, with their implementation and validation status kept separate.

## External validation gates

- macOS requires a Mac and OS screen-recording/accessibility approvals.
- Linux requires native X11 and Wayland/portal testing, including permission denial.
- Mobile platforms impose OS capture/input/background-execution restrictions.
- Wake from sleep/off requires supported firmware/NIC/router configuration and a
  reachable wake path. A stopped process cannot wake a powered-off computer alone.
- A public relay, signing identity and store accounts are real infrastructure;
  an undocumented public third-party service is not an unlimited availability guarantee.
- Review by Opus is requested for later. No Opus review has been run.

## Public source checkpoint decision (2026-09-28)

The owner subsequently requested finishing the next portable collaboration increment,
closing documentation/license provenance work, and pushing the repository. This
explicitly authorizes a public development source preview. It does not mark the
expanded feature/production acceptance table above as complete. Apple retains the
stated untested-source exception. See RELEASE-PLAN.md and release-gates.json.
