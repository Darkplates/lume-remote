# Portable resume, annotations and printing

## Single-file resume

A saved, paired connection negotiates protocol capability 256 before using
GetResume, PutResume, OfferResume, ResumeReady and Preparing. Guest and legacy
connections keep their original non-resumable operations. Portable permanent hosts
derive the resume context from the authenticated controller; an invitation alone
cannot enable it.

Retry the same file and destination after an interruption. The sender hashes the
whole file before the offer, verifies the retained prefix before sending a suffix,
and the receiver verifies both the final length and original full-file SHA-256.
Preparation sends liveness receipts from a separate file worker. The paired key,
canonical destination, name, length and digest authenticate the checkpoint. Partial
files are locked against simultaneous reuse; keys are not stored in their journals.
Explicit transfer cancellation and failed verification discard the active partial.
Existing final files are never replaced.

Folder jobs still create a new unique root on retry and use ordinary per-file
operations. They are not persistent/resumable batches. Mobile downloads live in
the session's private cache: resume is available during automatic reconnection,
but explicitly closing the mobile session removes that cache. Uploading the same
selected file again can reuse a retained partial on the host. The UI reports the
number of verified bytes reused. Host/download storage must support hard links.

## Visible annotations

Portable desktop, Android and iOS viewer interfaces provide **Draw**, **Finish
drawing** and **Clear marks** when the host advertises tool 7 / capability 64.
The current Windows host renders the visible overlay. Linux/macOS hosts do not yet
implement that overlay and therefore do not advertise annotation support.

Drawing releases held input and suppresses keyboard/mouse injection. Strokes have
2–128 normalized points and carry the generation of the displayed image. Clear is
an empty stroke. Stale generations are rejected; at most four annotation requests
await receipts. Windows removes marks after 30 seconds of inactivity or teardown.
Actual cross-device pen/touch and overlay placement still need physical acceptance.

## Print a downloaded PDF

Download a PDF using **Files**, wait for SHA-256 completion, then choose **Print
PDF…**. The print operation takes an independent private snapshot so disconnecting
the session cannot redirect a print request or replace the document being printed.
Printing is always initiated and confirmed locally. No virtual printer driver,
remote shell, automatic file launch or arbitrary-document conversion is added.

- Linux: choose an accepting CUPS destination and **Print one copy**. `lpstat` and
  `lp` are called as separate arguments without a shell. The local CUPS client and
  a configured printer are required. Queue acceptance does not prove paper output.
- macOS: the packaged `lume-print` helper uses PDFKit and the system print panel.
  This source and its universal build recipe are uncompiled and untested.
- Android: the system print panel selects a printer and page range. PdfRenderer
  rasterizes selected pages at up to 144 DPI / 2048 pixels per side. A device memory
  budget and 64-page batch bound require smaller selections for large documents.
  This is a bounded raster printing path, not vector/lossless PDF forwarding.
- iOS/iPadOS: a private PDF snapshot is passed to UIPrintInteractionController,
  including an iPad popover anchor. This source is uncompiled and untested.

The portable print preparation limit is 128 MiB per PDF. This safety/resource bound
does not limit ordinary file transfers or session duration. Physical printers and
mobile print-service execution remain unverified.

Implementation references: [CUPS command-line printing](https://www.cups.org/doc/options.html),
[Android custom printing](https://developer.android.com/training/printing/custom-docs),
[Apple PDFKit print operation](https://developer.apple.com/documentation/pdfkit/pdfdocument/printoperation(for:scalingmode:autorotate:)).
