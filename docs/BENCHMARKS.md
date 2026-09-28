# Reproducible comparisons

**Status: no controlled physical two-PC comparison has been published.** The owner's
roughly two-second reconnect is a useful observation, not a timed series or evidence
that Lume beats another product. No competitor timings, delivered FPS or resource
savings are invented here. Automated regression checks are not performance results.

The repository includes a local Windows collector, a blank connection-trial CSV and
a report generator. They require no account, backend or upload. Raw output stays in
`verification/benchmarks`, which Git ignores. Review metadata before sharing it.

## Freeze one case before measuring

Record exact versions/commits on both endpoints; CPU, GPU, RAM and OS; resolution,
scaling and refresh; quality/codec/bitrate settings; network type, actual direct or
relay route, and any other foreground workload. Use anonymous device labels rather
than computer names, addresses or account names. Use the same endpoint pair and
content for all products. Run only one product's session at a time and retain the
background service configuration in the record. Use each product within its terms.

Keep these cases separate:

| Case | Start | Stop / workload |
| --- | --- | --- |
| Cold viewer launch | Launch an exited viewer; host already available | First complete visible desktop image |
| Warm reconnect | Click a saved PC in an open viewer; host already available | First complete visible desktop image |
| Recovery | Restore a deliberately interrupted test network | Usable image and a verified input action |
| Idle resources | Connected with an unchanged test desktop | 60 seconds, then a separate 60-minute stability check |
| Moving resources | Same controlled scrolling or local test video | 60 seconds after a 15-second warm-up |
| File transfer | Start the same owned file | Completion plus destination SHA-256 equality |

A warm connection is not a cold launch. Pairing/consent time is a separate task.
Define a fixed timeout before running (for example 30 seconds) and record every
failure. Do not drop slow attempts. Alternate product order to reduce cache and
temperature bias. Use at least 10 attempts per case, preferably 20 or more. A p95
from a very small sample is imprecise; it is not a service guarantee.

Use a video or an instrumented timestamp at both defined events for latency.
Record the method, frame rate and uncertainty. A manually estimated time cannot
support millisecond claims. Test input responsiveness separately from the first
image: receiving one frame does not prove usable control.

## Collect CPU and memory

On each Windows endpoint, open the remote session, select the scenario, then run:

```powershell
.\BENCHMARK.bat
```

The default is **Lume / viewer / moving content / 60 seconds**. It asks for version,
anonymous case, hardware, network and quality labels. To measure a host or another
product, pass the options explicitly, for example:

```powershell
.\BENCHMARK.bat -Product AnyDesk -Role Host -Scenario Idle -DurationSeconds 60
```

Defaults look for `LumeRemote`; `TeamViewer`, `TeamViewer_Service`,
`TeamViewer_Desktop`; `AnyDesk`; or `rustdesk`, respectively. These names are starting
points, not a claim of complete process coverage for every product/version. Inspect
Task Manager first. Add missing helper/service names with `-ProcessName` by calling
the PowerShell script directly. No elevation is requested automatically. An
inaccessible service makes a run incomplete. A helper using a different unlisted
name cannot be discovered by this collector. Do not compare UI-only measurements
with another product's full service/process set.

The collector reads process CPU time, working set and private bytes only. It records
all matching processes, not just the smallest process. Identity includes PID plus
start time internally, so replacements do not create negative CPU deltas; PIDs and
start times are not exported. Changed/missing/unreadable sets produce gaps and a
non-success exit. It does not read the screen, clipboard, commands, file names,
network traffic, hostnames or pairing data, and does not change app/service settings.

CPU is normalized over logical processors. Summed working set may double-count
shared pages. Private bytes are committed memory, not resident private RAM. Results
exclude kernel/driver/GPU work and cannot establish whole-system energy use. A still
desktop sending fewer frames is expected; measure motion before interpreting FPS.

## Record connection attempts and generate a report

Copy `benchmarks/trials-template.csv` to a local working file. It has headers only.
Fill every field. Keep `case_id`, hardware, network, quality, role, scenario and
measurement method identical only when they really are comparable. Use `success`
with positive seconds, or `failure` with blank seconds and a short failure code.
Give each attempt a unique trial number within its group and an anonymous evidence
ID referring to a retained recording/log. Never publish pairing codes or private
screen content in that evidence.

With Python 3 installed:

```powershell
python scripts/benchmark-report.py --trials verification/trials.csv --runs verification/benchmarks/RUN/run.json --output verification/comparison.md
```

Multiple `run.json` paths are accepted after `--runs`. Either input type can be
omitted. The output must be a new file. Missing inputs do not become zero or invented
measurements. Reports separate differing configurations and keep failures in the
denominator. Median and nearest-rank p95 are explicitly conditional on successful
attempts; no overall latency percentile is inferred if attempts fail. Resource means
are interval weighted; CPU p95 is the nearest rank of valid interval samples.

Publish the protocol, raw CSV/JSON, failed attempts and report together. Do not
publish only the fastest attempt or a selected FPS target. This collector does not
measure FPS, bandwidth, image fidelity, relay cost or latency: those require their
own evidence and method. An incomplete run remains labelled incomplete.

## What would support a public claim

- **Faster reconnect:** comparable physical cases, repeated timings, both endpoints'
  versions, failures and raw evidence. Limit the wording to those tested cases.
- **Lower resource use:** matched delivered quality and motion, host and viewer
  measurements, verified process coverage and an idle baseline on the same machine.
- **Stable sessions:** a recorded long session with idle/minimized periods, network
  interruption and recovery, followed by working input and verified file transfer.
- **Exact source pixels:** a captured test pattern compared at source resolution;
  codec settings alone do not establish the complete rendering path's fidelity.

The test-suite smoke measurements validate the collector using a disposable sleeping
helper. They are not Lume or competitor resource benchmarks. Apple compilation and
execution remain unverified because no Apple device is available.
