# Native peer source patches

Lume's native peer library is built from exact upstream revisions plus three local,
versioned timing patches. It is not an unmodified upstream libdatachannel build.
All upstream copyright notices and licenses remain in place.

| Component | Upstream revision | Local modification |
| --- | --- | --- |
| libdatachannel 0.24.6 | `6b1e2e620f1e37f0eafeee702eaea0043cb305fd` | MbedTLS backend in `src/impl/dtlstransport.cpp`: call `mbedtls_ssl_conf_handshake_timeout(&mConf, 1000, 600000)` before SSL setup. |
| libjuice 1.7.4 | `b89c792e3612faf2f12cf35bcc56857313a06be3` | `src/agent.h`: change only `ICE_PAC_TIMEOUT` from 39500 to 600000 milliseconds. `src/agent.c`: while the agent is still connecting and the PAC timer runs, restart a pending pair's exhausted check (fresh transaction, normal backoff) instead of failing it. |
| Mbed TLS 3.6.7 | `068ff080b369adfac81509f9b57b2afabaf82dc5` | No source modification. |

The three modified files retain MPL-2.0 terms. The patches are distributed in
`native/peer/patches/manual-signaling-ice-v1.patch`,
`native/peer/patches/manual-signaling-ice-checks-v1.patch` and
`native/peer/patches/manual-signaling-dtls-v1.patch`. Their manifest,
`native/peer/patches/manual-signaling-v1.json`, records upstream pins plus
SHA-256 values for each original file, patch, and patched file. Text hashes use
UTF-8 without a BOM and normalize CRLF to LF, so Windows and Unix checkouts
produce the same verified patch inputs and outputs.

## Purpose and limits

Stateful firewalls and NAT routers admit a peer's UDP only shortly after sending to
it. Stock libjuice stops a pair's checks after about 40 seconds, so a reply applied
minutes later met closed paths on both sides even with a longer PAC timer. The
`agent.c` patch keeps checks running until the PAC timer expires (at most 10
minutes, then the normal failure path). The delay fixture models this with
30-second stateful filters on both sides.

The reply must travel back to the sharing PC manually. A native connectivity
timer previously expired about 40 seconds after the viewer finished gathering,
before the application's longer wait expired. On a route that connects early,
the viewer also starts DTLS before the sharing PC receives the reply; the stock
MbedTLS handshake expired around 124 seconds in the controlled fixture.

The native changes preserve the public C ABI, DTLS roles, fingerprint checks,
STUN server/configuration, the per-cycle check backoff and consent freshness;
only the end of a check cycle before connection is changed, as described above.
The MbedTLS maximum is a retransmission interval, not a ten-minute total
connection deadline. Existing application waits and local cancellation still
provide the outer bounds; the Windows viewer waits at most 10 minutes for a
guest connection and the host's post-reply wait remains 90 seconds.

Owned loopback UDP proxy tests passed with a stock host and patched viewer:
traffic held until answer delivery at 2, 45 and 90 seconds; an early ICE route
with reply delivery at 150 seconds; exact byte transfer; bounded cleanup. The
same stock viewer failed the controlled 45-second and early-route 150-second
cases. These tests establish the repaired manual-signaling timing defects and
stock-host compatibility, not real-WAN reachability or the cause of a particular
Internet connection failure.

## Reproducible source overlay

`scripts/prepare-peer-source.py` uses only Python's standard library and Git.
It validates exact upstream commits, clean tracked state and recursive submodule
pins before copying tracked source files, including submodules, without Git
metadata. It verifies both patch inputs and outputs in the isolated copy. The
upstream checkouts are never patched. The completed overlay contains
`source-provenance.json` with original revisions, submodule evidence, the patch
manifest hash, and every overlay file's SHA-256.

Repeated preparation verifies all existing overlay files and provenance instead
of overwriting them. Unexpected contents, incomplete overlays and corrupted
patches cause a failure that preserves existing files. A failed new preparation
retains its owned temporary staging directory for diagnosis. Use a new build
directory when deliberately changing the pins or patch version.

All peer builders use this helper before CMake: Windows and Android call the
local Python executable; Linux and Apple call `python3`. Their CMake source root
is `<build-root>/patched-source`. The complete source is reproducible from the
upstream links/revisions in `THIRD-PARTY-NOTICES.txt`, the packaged patches, and
the packaged helper; no private build-time source or extra package is required.

To prepare or verify an overlay directly:

```text
python scripts/prepare-peer-source.py --source-root <clean-pinned-sources> --output <build-root>/patched-source
python scripts/prepare-peer-source.py --source-root <clean-pinned-sources> --output <build-root>/patched-source --verify-only
```

Windows builds retain `-SourceDirectory` and `-BuildDirectory`; optional
`-OutputPath` selects a new DLL destination for isolated validation. Omitting it
retains the existing build command's repository-DLL output behaviour. Android
retains its SDK/CMake/Ninja/PeerSource and ABI parameters. Linux and Apple retain
their existing source/build-root environment variables and platform options.
The source and Windows packages include all three patches (ICE timeout, ICE
checks and DTLS), the manifest and helper.

The integrated Linux/macOS/iOS build paths are source-reviewed here; actual
execution of those patched native builds and physical-device acceptance are
separate platform checks. See the current validation report for measured gates.
