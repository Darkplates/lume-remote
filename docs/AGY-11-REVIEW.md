# AGY independent static review and disposition — 0.11

Date: 2026-09-28. Requested model: `gemini-3.8-flash-high`; requested effort: `high`.
Both AGY invocations pin that exact model and effort. The first completed review
used the built-in file-reading tool under unchanged request-review permissions,
inspecting 39 unique current/baseline files. It did not execute commands or edit
source. Nine file lookup errors were recoverable; the final report was nonempty.
An earlier, separate headless attempt was denied command approval and produced no
review; it is retained locally and is not counted as a completed review.

## Maintainer disposition

The reviewer reported two P3 findings and no identified P0/P1 finding within the
inspected changes. Both P3 findings were confirmed against source and corrected:

1. Android single-file export now sets the preparation guard, clears cancellation
   for the new operation, checks before opening streams, and uses the shared
   cancellable stream copy. The guard is released in `finally`; cancellation keeps
   the partial destination and the private source. Three executable JVM checks on
   the real helper cover byte preservation, pre-cancellation and cancellation after
   the first completed buffer. This does not execute Android storage providers.
2. Desktop upload now has separate file/folder actions. The folder action is gated
   by negotiated capability 16, matching folder downloads. It does not probe the
   filesystem during each UI repaint. Core type/path/permission validation remains
   authoritative when either action is submitted.

The same requested AGY model subsequently received the complete, numbered changed
source and regression script for a focused second review, included below. Current
Android and Linux builds and the final Linux native fixture are checked separately
from this static review. The original line references below describe the reviewed
pre-correction snapshot and may move after the corrections.

## Interpretation and remaining limitations

This is an independent model-assisted static review, not a security certification
or proof that defects are absent. The reviewer's stronger wording about absence of
vulnerabilities is an assessment of inspected source, not a verified global fact.
The full publication gate remains pending for broader security/device acceptance.
The historical Opus review has not run.

Hard-link publication remains a documented filesystem compatibility requirement;
Android storage-provider execution remains unverified. Apple source/build scripts
remain prepared but uncompiled and untested because no Apple device is available,
as explicitly accepted by the owner. No new requirement to obtain a Mac is imposed.

## Original AGY report (links and machine paths normalized)

# Independent Pre-Release Code Review: Lume Remote 0.11

This independent static code review evaluates the changes introduced for **Lume Remote 0.11** in the review target workspace `<repository>` against the immutable 0.10 baseline `<immutable-0.10-baseline>` and the governance principles defined in `AGENTS.md`.

---

## 1. Findings

### Confirmed Findings (P0 – P3)

No **P0** (critical security/RCE/unauthenticated access) or **P1** (data loss / remote protocol desynchronization / fatal denial of service) vulnerabilities were identified.

#### [P3] Android `SessionService.java` single-file export cancellation check omission
* **Location:** `ports/android/app/src/main/java/com/lume/remote/SessionService.java#L175-L178`
* **Triggering Sequence:**
  1. A user downloads a large single file to the private staging cache and confirms the Android storage destination via system picker, invoking `export(Session s, String path, Uri destination)`.
  2. While the 64 KiB buffer loop copies bytes from private staging to the `ContentResolver` destination stream, the user cancels the transfer (`s.fileCancelled = true`).
* **Concrete Impact:**
  `export()` checks `if (s.closed) throw new InterruptedIOException();`, but does **not** check `s.fileCancelled`. In contrast, `upload()` (line 145), `uploadFolder()` (line 158), and `exportFolder()` (line 170) explicitly check `s.closed || s.fileCancelled`. The copy continues writing to the user-selected file until EOF or until the session closes entirely. Because this is a local copy from private staging to target URI, there is no security exposure or remote state corruption, but user-initiated cancellation is ignored during single-file export.
* **Smallest Sound Fix:**
  In `SessionService.java:177`, change:
  ```java
  if(s.closed)throw new java.io.InterruptedIOException();
  ```
  to:
  ```java
  if(s.closed||s.fileCancelled)throw new java.io.InterruptedIOException();
  ```

---

#### [P3] Desktop UI client-side upload button remains enabled for directories when host lacks capability 16
* **Location:** `ports/desktop/src/file_ui.rs#L142-L167`
* **Triggering Sequence:**
  1. A desktop viewer connects to a legacy host that does not advertise capability bit 16 (`FOLDER_JOBS`).
  2. The viewer UI disables the individual remote directory "Download folder" button via `capabilities & 16 != 0` (`file_ui.rs#L78-L82`).
  3. However, for uploading, the UI only checks `!state.active && !state.path.is_empty() && !remote.upload.is_empty()`. If the user pastes or drops a local directory path into the upload box and clicks "Upload to this folder", `file_ui.rs` dispatches `FileCommand::UploadFolder`.
* **Concrete Impact:**
  `Viewer::command` immediately intercepts the command and returns an error (`"This host does not support folder transfers"`), preventing any packet from being queued or sent. The core state remains sound, but the desktop UI allows clicking an action that is guaranteed to fail immediately rather than proactively disabling the button as it does for folder downloads.
* **Smallest Sound Fix:**
  In `ports/desktop/src/file_ui.rs#L143-L146`, check whether the chosen path is a directory and guard against missing capability 16:
  ```rust
  let local = std::path::PathBuf::from(&remote.upload);
  let allows_upload = !state.active 
      && !state.path.is_empty() 
      && !remote.upload.is_empty()
      && (!local.is_dir() || (remote.viewer.state.lock().unwrap().capabilities & 16 != 0));
  if ui.add_enabled(allows_upload, egui::Button::new("Upload to this folder")).clicked() {
      // ...
  }
  ```

---

### Uncertain Observations Requiring Runtime Verification

1. **Atomic publication via hard linking across non-supporting filesystems**
   * **Location:** `ports/core/src/files.rs#L552-L560` (`Incoming::complete`)
   * **Context:** In `Incoming::complete`, downloads are written into `.lume-<id>.partial` (permissions `0o600`) within the target directory. Upon SHA-256 verification, `fs::hard_link(&self.temporary, &destination)` atomically places the completed file without overwriting an existing destination.
   * **Observation:** While keeping the temporary file in the same directory avoids cross-filesystem links (`EXDEV`), filesystems lacking hard-link support (such as FAT32, exFAT, or certain CIFS/NFS mounts) will fail on `hard_link`. The transfer fails safely without data loss, but runtime validation is recommended on non-NTFS/non-ext4 removable drives to confirm user error messages clearly explain filesystem constraints.

2. **Android DocumentFile cursor semantics across third-party DocumentProviders**
   * **Location:** `ports/android/app/src/main/java/com/lume/remote/FolderDocuments.java#L47-L55`
   * **Context:** `stage()` enumerates SAF tree URIs using `DocumentsContract.buildChildDocumentsUriUsingTree()`.
   * **Observation:** Standard local and Google storage providers return valid cursor entries, but certain OEM cloud providers or external SD cards may return null or drop URI permissions during deep traversals. `FolderDocuments` enforces a strict 64-depth limit, sanitizes names, and uses canonical path checks in `removeOwned()`, mitigating traversal risks. Physical Android device testing across vendor SAF implementations remains necessary.

3. **Apple iOS/macOS uncompiled status and entitlement scopes**
   * **Location:** `ports/ios/Lume/FolderTransfer.swift`, `NativeSession.swift`
   * **Context:** The Apple port files correctly implement security-scoped resource access (`startAccessingSecurityScopedResource`), chunked FileHandle streaming, symlink rejection, and matching C ABI bridge signatures.
   * **Observation:** Per project agreement and scope definitions, Apple platforms are explicitly uncompiled and unexecuted due to lack of macOS/iOS hardware. Runtime verification with Xcode, App Store sandboxing, and physical devices is required when hardware becomes available.

---

## 2. Areas and Files Reviewed

### Core Protocol, Files, and Sessions
* `ports/core/src/files.rs`: Bounded channel backpressure (512 KiB buffer cap), retired transfer ID ring buffer (32 slots) preventing stale packet reuse, `resolve()` directory-traversal defense (`..`, control characters, device names, Windows reparse points `0x400`), atomic hard linking, and SHA-256 verification.
* `ports/core/src/file_jobs.rs`: Sequential DFS traversal, `MAX_DEPTH = 64` recursion limit, `created_path` receipt validation against parent escape, empty directory creation, and cancellation cleanup.
* `ports/core/src/session.rs`: Capability bit 16 negotiation, command queue validation, 1-slot non-blocking `record_wakeup.try_send(())` ensuring video recording does not block transport ACKs, and scoped folder serving.
* `ports/core/src/recording.rs`: Matroska muxer, monotonic frame timestamps, audio timestamp alignment, frame skipping counters, and teardown duration writeback.
* `ports/bridge/src/lib.rs` & `include/lume.h`: C ABI entry points (`UploadFolder`, `DownloadFolder`, `Record`, `CancelFile`), `LumeFrameInfo` alignment, zero-copy querying, and crash protection via `protect()`.

### Desktop GUI
* `ports/desktop/src/file_ui.rs`: Remote drive browsing, pagination, progress reporting (`items_done`, `bytes_done`), and command dispatch.
* `ports/desktop/src/main.rs`: Modal dialog input suppression, display link refresh, and viewer lifecycle management.

### Android Client Port
* `ports/android/app/src/main/java/com/lume/remote/FolderDocuments.java`: Depth-limited SAF traversal, cycle detection via ancestor ID sets, filename sanitization, and canonical path checks in `removeOwned()`.
* `ports/android/app/src/main/java/com/lume/remote/SessionService.java`: Threaded staging, CAS concurrency gates (`s.preparing`), staged upload cleanup on `operation > stagedOperation && !active`, and lifecycle shutdown.
* `ports/android/app/src/main/java/com/lume/remote/FilesDialog.java` & `MainActivity.java`: Activity contract handling, SAF picker routing, and permission isolation.
* `ports/android/app/build.gradle`: `minSdk 26`, `targetSdk 36`, Java 17 compatibility.

### Apple iOS / macOS Port
* `ports/ios/Lume/FolderTransfer.swift`: Recursive staging, symlink prevention, depth bounding (64), and FileHandle streaming.
* `ports/ios/Lume/NativeSession.swift`: Bridge interop, security-scoped URL handling, upload staging cleanup, and CADisplayLink presentation.
* `ports/ios/Lume/FilesView.swift` & `LumeApp.swift`: SwiftUI navigation, DocumentPicker representables, and recording FPS configuration.

### Packaging, Baseline Diffs, and Governance
* `scripts/package.ps1`, `scripts/verify-package.ps1`, and `scripts/verify.ps1`: Source manifest inclusion, regex filters, and build artifact boundaries.
* Baseline comparison: The legacy Windows host (`src/Program.cs`, service installation, UAC elevation, TLS certificate handling) is unchanged from 0.10 except for the version bump.
* Governance: No telemetry, third-party runtime dependencies, cloud subscriptions, commercial gating, or automatic firewall modifications were added.

### Material Coverage Limitations
1. **No live execution/commands:** In accordance with execution constraints, review was strictly static source inspection. No compilers, unit tests, or hosts were run.
2. **Apple platforms:** Apple source files were checked for syntax, ABI consistency, and logic, but remain uncompiled and unverified against Apple toolchains or hardware.
3. **Android physical hardware:** Android SAF and storage logic were reviewed statically; runtime performance and provider behavior across physical Android devices/OEMs have not been measured.

---

## 3. Release-Blocking Assessment

* **Verdict:** **No unresolved release-blocking findings.**
* There are no P0 or P1 security, data-loss, or protocol-breaking defects in the 0.11 portable additions.
* All folder transfer mechanisms enforce strict depth bounds (64), atomic non-destructive file placement, parent validation on receipts, symlink rejection, and isolated in-flight packet ring buffers.
* The findings identified are **P3** usability/cancellation improvements that do not compromise session integrity or host boundaries.


## Focused AGY re-review after corrections

### Static Re-Review: Lume Remote 0.11 (P3 Corrections)

#### Finding A: Android Single-File Export Ignored `fileCancelled`
- **Disposition**: **Resolved**.
- **Analysis**:
  - SessionService.java now guards single-file export with `s.preparing.compareAndSet(false, true)` (line 176) and releases it in `finally` (line 184), matching `exportFolder` and `upload`.
  - It clears stale cancellation state (`s.fileCancelled = false`, line 177) before queueing the task.
  - Pre-stream validation via `FolderDocuments.check` (line 179) aborts immediately if cancelled while queued, avoiding destination stream allocation.
  - Streaming uses `FolderDocuments.copy` (line 181), which checks cancellation on entry and before every 64 KiB write chunk.
  - `InterruptedIOException` is caught cleanly (line 182), leaving private cache files intact and setting an accurate non-fatal status without leaking streams.
  - `Session.action` sets `fileCancelled = true` on `cancel_file`, properly coordinating UI cancellation with the background executor.

#### Finding B: Desktop Upload Folder Action Without Capability 16
- **Disposition**: **Resolved**.
- **Analysis**:
  - file_ui.rs inspects host capability bit 16 (`folders = view.capabilities & 16 != 0`, line 6) upfront while reading viewer state.
  - The UI splits upload into distinct "Upload file" and "Upload folder" controls (lines 138–151). "Upload folder" is disabled whenever `!folders` (line 146), aligning directly with `Download folder` (line 73).
  - Repaint-frequency filesystem metadata probes (`std::fs::metadata` / `is_dir`) are eliminated from the rendering pass. Path normalization and name extraction (lines 156–160) occur only upon button actuation.
  - Host-restricted capability feedback is displayed when unsupported (lines 152–154), and backend core command validation remains intact upon submission (lines 203–215).

---

#### New Actionable Source Issues
- **None confirmed**: No regressions in lock ordering, stream lifetime, cancellation state, or UI thread responsiveness were found in the reviewed source lines.

---

#### Untested Android Provider/Runtime Behaviour vs. Confirmed Source
- **Confirmed Source**: JVM copy chunking, boundary handling, pre-cancellation, and mid-stream cancellation write halts are verified by the standalone harness in scripts/test-android-copy.ps1 (`CopyCheck.java`).
- **Untested Provider Behaviour**: Physical Android Storage Access Framework (SAF) URI resolution, external provider write blocking/interruption, and whether third-party document providers retain or discard partial writes upon unclosed stream cancellation cannot be verified purely via JVM unit tests.

---

#### Limitations
- Static review is confined strictly to the provided diffs for Findings A and B.
- No dynamic execution on physical Android devices or emulators was conducted.
- Apple platform code remains uncompiled and untested per explicit owner agreement.
- No claims of absolute system security or public-release readiness are made.
