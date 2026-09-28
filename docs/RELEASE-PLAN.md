# Development publication and stable release plan

The owner authorized a public development source push on 2026-09-28 after the 0.12
portable collaboration increment and documentation/license provenance work. Source
publication and stable binary readiness are separate. Preserve the installed 0.3
host and existing immutable archives.

The machine-readable release-gates.json continues to report production readiness
as false while physical, infrastructure and security work is open. Do not claim
feature parity, performance superiority or Apple validation from a public source
push. The known license provenance exception is described in LICENSE-PROVENANCE.md.

Windows resume, configured shares, voice, AAC/FPS recording and PDF forwarding
have been implemented. Native regression and independent media/spool checks precede
the immutable Windows checkpoint. Physical hardware and installed-host acceptance
remain open; local synthetic tests cannot replace them.

Next tracks, in the owner's selected order:
1. Freeze and verify the Windows package, preserving the installed host.
2. Implement Linux/macOS/mobile roles with platform-native permissions and explicit
   build/test evidence. The owner accepts Apple source/build preparation with an
   explicit README statement that macOS/iOS were not compiled or tested because
   no Apple device is available. This exception does not waive implementation
   or imply Apple acceptance. Keep other device/platform evidence separate.
3. Installed-host/hardware/WAN commissioning, failure recovery and power/wake tests.
4. Independent review (currently AGY CLI / Gemini 3.8 Flash High as requested), distribution
   trust, relay availability and release documentation.

The historical Opus review handoff is an instruction document, not a completed review.
The public repository is a development source checkpoint. Stable-release approval
requires the remaining evidence; no background publishing job is scheduled.
