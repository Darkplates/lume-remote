# Launch preparation checkpoint — 2026-09-28

This is a development handoff, not production acceptance or a comparative benchmark.

## Completed and checked

- Complete Windows SDK selection rebuilt both native capture/video bridges locally.
- Linux GBM prerequisite was added to the Ubuntu workflow and build preflight.
  [Linux CI at cafe638](https://github.com/Darkplates/lume-remote/actions/runs/36486680982)
  passed, including native build, core/C ABI/P2P and isolated X11 checks. Apple was skipped.
- A public Windows CI failure exposed a consecutive-file race. The receiver now
  sends its completion receipt before waking the next operation; the sender releases
  its slot on the terminal receipt. The wire format and authentication are unchanged.
- Nine focused file/clipboard checks and 94 safe Windows checks passed locally.
  Coverage includes the new receipt ordering fixture and a 32-file folder round trip.
- Fourteen benchmark report tests passed, including failures in the denominator,
  missing data, nonfinite values, duplicate trials, configuration separation and
  duration weighting. A real disposable helper and an absent process exercised the
  Windows PowerShell 5.1 collector and report generator end to end.
- The demo uses existing real Windows-form images with synthetic data. New physical
  WAN video, delivered FPS measurements and competitor benchmarks were not produced.
- Prepared launch copy is a draft; no personal messages or social posts were sent.

## CI follow-up

The [Windows run at cafe638](https://github.com/Darkplates/lume-remote/actions/runs/36486681079)
passed the native build, Windows regression and idle stages, then failed because
Windows PowerShell 5.1 treated Cargo's redirected `Updating crates.io index` progress
message as a terminating NativeCommandError. The portable and Android workflow
steps now use PowerShell 7 and retain explicit exit-code checks. Full hosted
Windows/Rust/Android success for this final checkpoint is **not yet confirmed**.
Check the [latest workflow runs](https://github.com/Darkplates/lume-remote/actions)
before announcing a stable or fully validated build.

The Windows preview package is built and verified locally. Existing 0.11/0.12
archives are preserved; the new launch package uses a separate preview name.
Use the release's commit and SHA-256 sidecar to identify it. A SHA-256 manifest is
integrity evidence, not publisher signing or a security certification.

## Still unverified

- Current-build physical two-PC WAN, installed-host upgrade/login/reboot/wake,
  60-minute idle/minimized sessions and recovery across different NAT combinations.
- Physical audio/microphones, SMB endpoints, printers and Android device execution.
- The new HTML walkthrough's visual acceptance in multiple browsers and small screens.
- Independent security review of the current checkpoint, signed distribution and
  managed TURN/update infrastructure.
- macOS and iOS compilation/execution: no Apple device is available; source only.

No existing installed host was upgraded by this work. Production gates in
`release-gates.json` remain open.

## Resume in a fresh Codex session

1. Read AGENTS.md, this file, docs/LAUNCH-PLAN.md and docs/BENCHMARKS.md.
2. Inspect current Git state and latest GitHub workflow results. Fix a failed stage
   using its actual logs; do not rerun every unrelated platform automatically.
3. Preserve existing source/artifact checkpoints. Verify any newly built archive and
   its producing commit before replacing a development download.
4. Complete the two-PC Windows beta checklist, retain raw measurements, and record
   the physical demo. Do not convert the rough two-second observation into a claim.
5. Broaden promotion only after repeatable physical evidence and critical bug fixes.
