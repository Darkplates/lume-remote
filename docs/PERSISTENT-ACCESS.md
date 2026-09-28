# Saved access and wake

## Pairing

On the target PC, Enable access starts normal administrator setup. Pair another PC creates an expiring one-time bootstrap code. The controlling PC uses Add a computer to enroll a new per-computer key. The bootstrap code is consumed; saved access uses a different protected key.

Host configuration is DPAPI-protected with ACL access limited to the granting Windows owner, SYSTEM and Administrators. Installed program files require administrator write access. Guest and wake-only permissions are distinct from permanent desktop access.

Disable access terminates paired access. Settings can revoke a selected controller or remove the host service. Closing the dashboard hides it in the tray and does not change these permissions.

## Sessions

Double-click a saved PC. Each remote PC has its own viewer window. Several outgoing sessions can remain connected at once, while each target accepts one incoming viewer. Clicking an already open saved PC returns to its window.

Saved viewers recover automatically from transient transport loss until explicitly disconnected. Fresh host authorization and session credentials are required on every attempt. Guest approval does not become permanent access.

## Updating

Open an extracted new release and use Settings > Update installed host. The normal Windows administrator prompt is required. The update closes the old installed dashboard, restarts the service and keeps the pairings and current access setting. Replaced files are backed up. A hidden old dashboard may need to be exited from its tray menu first.

The privileged update path must be checked separately from the portable viewer. Local synthetic tests do not establish reboot, sign-in, secure-desktop or remote-PC installation acceptance.

## Power

The opt-in host can keep the PC awake while plugged in. This does not prevent a deliberate sleep, shutdown, network outage or power loss.

Wake-only helper pairings authorize a magic packet for one configured Ethernet MAC. An Internet wake needs an always-on helper in the target network or suitable router support. The target requires compatible network hardware, firmware settings and standby power. The application cannot run on a fully unpowered computer. Physical wake is not validated by constructing a packet or by a passing networking test.