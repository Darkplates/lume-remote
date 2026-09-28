# Portable 0.10 verification

Date: 2026-09-28. Development checkpoint; full product parity and public-release
acceptance remain incomplete. All four 0.9 archives retain their recorded hashes.

## Changes

- Portable desktop hosts expose the existing Windows monitor tools. Desktop,
  Android and iOS viewers can enumerate/select displays. Negative origins and
  different dimensions/refresh rates are preserved. Selection releases held input,
  invalidates the old image and requires the matching display generation before
  input resumes. An unavailable display returns an error without ending the session.
- Desktop frame arrival signals a separate bounded presentation worker. A slow UI
  callback cannot block frame acknowledgements or network liveness. Android uses
  Choreographer and iOS source uses CADisplayLink, with at most one pending mobile
  copy/presentation. Status updates are independent. Mobile input carries the
  generation of the displayed image rather than a newer metadata snapshot.
- Apple preparation includes read-only prerequisite checks, selectable macOS/iOS
  build modes, universal simulator output, bundle/dependency/signature checks,
  per-run evidence folders and a Finder launcher. The owner explicitly accepts
  preparation without Apple compilation/testing because no Apple device is available.
  README, platform guide, acceptance contract and release gates state that exception.

## Observed checks

| Layer | Result | Boundary |
| --- | --- | --- |
| Windows application | 93 safe checks passed | Synthetic/local protocol, relay, native P2P and regression checks; installed service untouched |
| Shared Rust on Windows | 37 core + 2 C ABI tests passed; 0 ignored | Native P2P, consent, recovery, monitor selection and presentation isolation |
| Monitor interoperability | Both Windows/Rust directions passed | Authenticated synthetic displays, exact source pixels, negative origins, epochs and missing-display recovery |
| Shared Rust on Alpine | 38 core + 2 C ABI tests passed; 0 ignored | Owned QEMU VM; includes Unix filesystem checks |
| Linux X11 | 2 native checks passed | Owned Xvfb capture, actual input release on monitor selection, clipboard and rendered dashboard |
| Linux host lifecycle | 1 native check passed | Protected keyring, sign-in configuration, actual daemon, singleton and disable in the owned VM |
| Linux audio | 1 native check passed | Actual PulseAudio client processes with an isolated null sink |
| Android build | ARM64/x86-64 native libraries, app and instrumentation APK compiled | Instrumentation APK was built, not executed |
| Android lint | 0 errors, 13 warnings | Update suggestion, extraction-rules and text-localization warnings remain visible |
| Android package | APK signature v2, CRCs, 16 KiB ELF/ZIP alignment and all 8 native library identities passed | Container/static evidence; not device execution |
| Apple preparation | Shell/Python syntax and non-Mac prerequisite rejection passed | No Apple SDK compilation, application launch or Apple binary supplied |
| Notices | 869 crate notice files match inventory hashes; 491/496 packages covered | Five explicit upstream gaps and independent review remain open |

The Linux core and C ABI checks took 199.18 and 2.68 seconds under QEMU TCG.
These are test durations, not remote-desktop FPS or hardware benchmarks. The
new blocked-presentation test receives additional frames while its UI callback
is deliberately stalled; it does not infer throughput from a configured target.

## Corrections during validation

The first new presentation test accidentally requested a height below the protocol's
minimum. The fixture was corrected to vary valid JPEG settings while holding the UI
callback blocked, then the complete Windows and Linux core suites passed. An initial
Linux command started before its file upload completed; the completed upload was
verified before rerunning. Visual inspection caught a stale 0.9 dashboard label;
the label was corrected and the final optimized executable was rechecked. Earlier
failure/candidate evidence remains in the local verification directory.

## Unverified and remaining work

No Apple device is available. macOS and iOS/iPadOS have not been compiled or tested.
This accepted exception must remain visible in any distribution; it is not a pass.
Android has no working emulator/physical-device execution evidence. Physical desktop,
multi-monitor, high-refresh GPU, audio hardware, WAN, secure desktop, SMB, printer and
wake/reboot acceptance remain separate. The Linux package targets Alpine/musl x86-64.

Portable viewer folder jobs/resume, annotations, printing, source-FPS recording,
mobile host roles and Wayland control remain incomplete. No CPU/RAM savings or
competitive performance superiority is claimed. A Source target is not measured FPS.
No commercial-use detector, subscription, telemetry or duration quota is introduced.

Public release still has 9 required open gates. Independent/Opus review has not run;
publisher signing, update/distribution infrastructure and five notice gaps remain.
The installed Windows 0.3 host was not updated. No repository was published and no
personal message was sent. The requested message is a separate draft outside the
source package. See the outer 0.10 verification file for final package sizes/hashes
and `docs/evidence/v10-*.txt` for versioned check output.
