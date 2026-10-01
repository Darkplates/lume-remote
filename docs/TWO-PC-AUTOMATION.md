# Synthetic two-PC guest test automation

The [2026-09-30 results](TWO-PC-TEST-RESULTS-2026-09-30.md) include a real LAN
failure when a manual answer was held for 150 seconds after creation. Immediate
automatic/manual exchange and a 45-second delay passed. Waiting 150 seconds
before creating a fresh answer also passed. The mechanism remains unresolved;
this is test evidence, not a production fix or WAN acceptance.

`tests/two-pc/TwoPcScenario.cs` is the separate executable used for these tests.
It compiles the production protocol code without `Program.cs`. Only this fixture
supplies automatic test approval, synthetic 640x360 frames, injected keyboard
events, an in-memory clipboard and owned 64 KiB transfer files. The application,
installed host and production consent flow are unchanged. The ordinary test
builder is non-recursive and does not include this executable's entry point.

## Prerequisites

- Two owned Windows PCs, .NET Framework 4.8, signed-in desktop users and working
  WinRM using the existing Windows identity. Exactly one literal peer must
  already be configured in the caller's TrustedHosts; the script does not change it.
- Windows SDK metadata and native libraries already built on PC A through the
  repository's integrated builder. No extra runtime or NuGet dependency.
- Permission to create one-shot scheduled tasks for the signed-in users. Child
  processes launched directly through a short-lived WinRM session do not survive
  session teardown reliably; the helper launches hidden test processes through
  interactive, limited-user tasks and removes each task after PID publication.
- Fresh test roots. Deploy refuses to overwrite an existing fixture folder.
  Optional `-LocalRoot` and `-RemoteRoot` select other dedicated local-drive paths;
  pass the same values on every action. Existing hosts must remain untouched.

## Build and deploy

From the repository root on PC A:

```powershell
.\scripts\build-two-pc-fixture.ps1
.\scripts\run-two-pc-fixture.ps1 -Action Deploy
```

The default build/evidence directory is ignored `verification/two-pc-runtime/`.
Deployment defaults to `C:\AI\lume-two-pc-fixture-a\autonomous-fixture` on PC A
and the corresponding `-b` directory on PC B. All five transferred files are
SHA-256 checked. `Refresh` updates only these owned fixture copies after checking
that neither is running and preserving the first executable as a backup.

## Run without copying private codes

```powershell
$run = 'automatic-control'
.\scripts\run-two-pc-fixture.ps1 -Action Start -RunName $run -Mode automatic
try {
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    do {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Fixture orchestration deadline expired.' }
        $result = .\scripts\run-two-pc-fixture.ps1 -Action Poll -RunName $run | ConvertFrom-Json
        Start-Sleep -Seconds 2
    } while ($result.Host.Running -or $result.Viewer.Running)
    if (-not $result.Host.State.Passed -or -not $result.Viewer.State.Passed) {
        throw 'Inspect the secret-free fixture JSON for the failing phase.'
    }
} finally {
    .\scripts\run-two-pc-fixture.ps1 -Action Cleanup -RunName $run
}
```

`Poll` must continue during manual cases: it transfers the private answer from
PC B to PC A. A `Start` action alone does not complete manual signaling. Each
case needs a fresh `RunName`. To reproduce other cases, change the `Start` action:

```powershell
# Immediate manual exchange.
.\scripts\run-two-pc-fixture.ps1 -Action Start -RunName manual-control -Mode manual
# Hold an already-created manual answer (45 passed; 150 failed on these PCs).
.\scripts\run-two-pc-fixture.ps1 -Action Start -RunName manual-held150 -Mode manual -ReplyDelay 150
# Age the offer, then create a fresh answer and return it promptly.
.\scripts\run-two-pc-fixture.ps1 -Action Start -RunName manual-fresh150 -Mode manual -ReplyDelay 150 -DelayBeforeAnswer
```

Use the same polling/cleanup loop for each case. It keeps private invitations and
answers out of arguments and console output. Temporary signaling files are
deleted after consumption; explicit cleanup removes leftovers and stops only
the recorded PID whose executable path matches this test copy. It never stops
Lume by process name, installs/enables a host, or changes network/firewall settings.

## Interpretation and limits

Successful cases assert decoded/acknowledged frames, authorization before source
creation/capture, two injected key events, clipboard read/write, bidirectional
file content hashes and receiver cleanup. Phase traces use existing peer flags;
they do not enable raw native logging or record SDP, keys or candidate addresses.
The loaded native module path is checked internally and only its hash is recorded.

The fixture waits 90 seconds for readiness after answer delivery on both sides.
The GUI host also waits 90 seconds after reply application; the GUI viewer has a
longer setup budget. A readiness timeout while flags still report route checking
does not establish a terminal native failure or a firewall/NAT diagnosis.

Published receipts are [under docs/evidence](evidence/two-pc-2026-09-30/).
They are sanitized historical snapshots from base commit `3b22f3e`, not current
machine state. Earlier cases predated the phase probe. The committed C# source is
the final diagnostic version; publication changed wrapper paths, not its logic.

These helpers require continuing WinRM reachability and do not automate a network
transition or the real WAN run. Follow [the two-PC plan](TWO-PC-TEST-PLAN.md) for
physical GUI/WAN acceptance. LAN candidate labels and public broker traffic do
not prove a different-network data path; short fixture observations do not meet
the 35-minute WAN idle requirement.
