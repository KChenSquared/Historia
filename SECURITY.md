# Security Policy

## Supported versions

Historia is maintained by one person, so security fixes go into the latest release only.
Please update to the newest release before reporting.

| Version        | Supported |
| -------------- | --------- |
| Latest release | ✅        |
| Older releases | ❌        |

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately through GitHub instead:

1. Go to the [Security tab](https://github.com/KChenSquared/Historia/security) of this
   repository.
2. Click **Report a vulnerability**.
3. Describe the problem, the Historia and Git versions, your OS, and the steps to reproduce
   it. A proof of concept helps, but is not required.

You should get an acknowledgement within 7 days. Once the problem is confirmed, I'll work on
a fix, keep you updated in the advisory, and credit you in the release notes unless you'd
rather stay anonymous. Please give me a reasonable chance to ship a fix before disclosing
publicly.

## What counts

Historia runs Git on your machine against repositories you open, so the issues that matter
most are ones where a repository, a remote, or another local process can make Historia do
something you didn't ask for. For example:

- A crafted repository, branch name, file path or commit message that injects arguments or
  commands into a Git invocation.
- Reading or writing files outside the repository Historia was asked to work on.
- Credentials, tokens or other secrets written to logs, settings or crash reports.
- Network requests Historia makes without you asking for them.
- A destructive action (discarding changes, deleting a branch, force-pushing and so on) that
  runs without a confirmation.
- The `historia-cmdr` agent pipe being reachable by another user, or letting a caller stage,
  commit or change anything.

## Out of scope

- Vulnerabilities in Git, .NET, Avalonia or other dependencies. Please report those to the
  upstream project; if Historia needs to respond (for example by raising its minimum Git
  version), a regular issue is fine once the upstream advisory is public.
- Problems that need an attacker who already controls your user account or machine.
- Hooks, aliases or configuration in a repository you chose to trust: Git runs those by
  design.
