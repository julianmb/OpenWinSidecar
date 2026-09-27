# Security Policy

## Supported versions

Only the latest released version receives fixes. Check the
[releases page](https://github.com/julianmb/OpenWinSidecar/releases) for the current version.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.

Email the maintainer directly, or use GitHub's private
[advisory reporting](https://github.com/julianmb/OpenWinSidecar/security/advisories/new).

Include:

- what an attacker can do, and what they need (LAN access, same Wi-Fi, a browser tab)
- steps to reproduce
- the app version (shown in the dashboard title bar) and your Windows build

You can expect an acknowledgement within a few days. Please give reasonable time for a
fix to be released before disclosing publicly.

## Threat model — please read before reporting

OpenWinSidecar is designed to stream a screen across a **trusted private network** to a
device you control. That assumption drives several deliberate decisions:

- **The server is unauthenticated unless you set an access password.** With no password
  configured, anyone who can reach the host's port on the LAN can view the screen and
  inject mouse and keyboard input. Set a password before using the app on any network you
  do not fully control (café Wi-Fi, hotel, shared office, conference).
- **Traffic is plain HTTP/WS, not TLS.** There is no certificate, so a host on the path
  can observe the video and relay the authentication exchange. Do not use it across an
  untrusted or adversarial network.
- **The host may run elevated.** Driver installation and enabling require administrator
  rights, so injected input can land in a high-integrity process.
- **The Windows firewall rule is scoped to the private profile** and allows the app from
  any address on that network. It does not restrict traffic to a particular subnet.

If your report is "the app is not authenticated by default" or "traffic is not encrypted",
that behaviour is known and documented above — please check whether a password is set
and whether the network is trusted before filing it as a new vulnerability.

## Hardening checklist

1. Set an access password in the dashboard before using untrusted Wi-Fi.
2. Keep Windows on the **Private** network profile so the firewall rule applies.
3. Prefer a wired or password-protected Wi-Fi network.
4. Do not port-forward the OpenWinSidecar ports (80, 8080, 28252) to the internet.
5. Update to the latest release before reporting a fixed issue.
