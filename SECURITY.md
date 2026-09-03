# Security Policy

## Reporting a vulnerability

Please report security issues **privately** through
[GitHub Security Advisories](https://github.com/okieselbach/AutopilotMonitor/security/advisories/new).
Private vulnerability reporting is enabled, so a report reaches the maintainers without ever being public.

Please do not open a public issue, post on social media, or send email for security findings.

## What to expect

- An acknowledgement and an initial severity assessment.
- A fix or a mitigation plan, with a shared view of timing before anything is disclosed.
- Credit in the advisory if you want it, and none if you prefer to stay anonymous.

Reports are read by the maintainers directly. There is no guaranteed response time and no bug bounty.

## Scope and rules of engagement

Security research against the hosted service is welcome and is not a violation of the
[Terms of Use](https://www.autopilotmonitor.com/terms). While testing, please:

- do not access data of tenants you do not own,
- do not degrade the service for others,
- do not run automated scanners against production.

## Supported versions

The hosted service always runs the current `main`. For the on-device agent, only the
[latest release](https://github.com/okieselbach/AutopilotMonitor/releases/latest) receives fixes.

## More

The [Security FAQ](https://docs.autopilotmonitor.com/trust-and-security/security-faq) and
[Data Flows](https://docs.autopilotmonitor.com/trust-and-security/data-flows) pages describe the security model,
data handling, and isolation in detail.
