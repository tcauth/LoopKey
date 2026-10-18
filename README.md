# LoopKey

LoopKey completes browser-based Corp or Green authentication on a laptop while
the OAuth client and credential cache remain on a remote Windows DevBox.

It preserves the native authorization-code and PKCE flow used by `azd`:

```text
DevBox                                      Laptop
------                                      ------
azd listens on localhost:<callback-port>
LoopKey creates a private Dev Tunnel
LoopKey publishes a one-time request  --->  loopkey connect <tunnel> <request>
                                            connects the private tunnel
                                            opens the default browser
azd receives the localhost callback   <---  Entra redirects to laptop localhost
azd validates and stores credentials
LoopKey verifies the profile           --->  helper reports success and exits
```

The laptop command does not receive or store access tokens, refresh tokens, or
the PKCE verifier. The authorization URL is fetched through the authenticated
private tunnel rather than placed in shell history.

> [!IMPORTANT]
> LoopKey is an experimental Windows-first prototype. It creates temporary
> Microsoft Dev Tunnels and starts an interactive `azd auth login`. Review the
> security model before using it outside a development environment.

## Prerequisites

Both the DevBox and laptop need:

- Windows;
- Windows Package Manager (`winget`).

The setup script installs and configures the remaining prerequisites.

## Quick start

An agent with terminal access can complete setup from the public repository.
Give it this prompt:

> Set up LoopKey from https://github.com/tcauth/LoopKey on this Windows
> machine. Follow `docs/setup.md`, install the user-scoped skill for your agent
> host, complete Dev Tunnel login, and verify the documented postconditions.

Or run setup directly from a LoopKey checkout.

Laptop:

```powershell
.\scripts\setup.ps1 -Role Laptop -Agent github-copilot
```

DevBox:

```powershell
.\scripts\setup.ps1 -Role DevBox -Agent github-copilot
```

Use `codex`, `claude-code`, or `all` for `-Agent`. For Green, add
`-GreenTenantId <green-tenant-id>` to the DevBox command. Setup installs the
verified release binary, configures user `PATH`, installs missing CLI
prerequisites, authenticates Dev Tunnels, installs the canonical skill at user
scope, and creates the DevBox profile wrappers:

```text
%USERPROFILE%\.azd-profiles\azd-corp.cmd
%USERPROFILE%\.azd-profiles\azd-green.cmd
```

See [`docs/setup.md`](docs/setup.md) for exact behavior, verification, pinning,
the no-Git bootstrap command, matching Dev Tunnel identity checks, Green
post-auth hooks, and non-default options.

## Usage

Start a request on the DevBox:

```powershell
loopkey request --profile corp
```

For Green, provide the tenant explicitly or set `LOOPKEY_GREEN_TENANT_ID`:

```powershell
loopkey request --profile green --tenant-id <green-tenant-id>
```

LoopKey creates a private tunnel, starts `azd` with a fixed loopback callback,
and prints one laptop command:

```powershell
loopkey connect <tunnel-id> <request-id> --metadata-port 8401
```

Run that command on the laptop. It connects the tunnel, fetches the one-time
request, opens the default browser, waits for DevBox verification, and exits.

Use `loopkey --help` for port, expiration, tunnel ID, and wrapper overrides.

## Security model

- Tunnels are private by default; LoopKey never enables anonymous access.
- Requests expire after 10 minutes by default.
- A request uses random 128-bit request identifiers and a dedicated temporary
  tunnel.
- The Entra authorization URL is available only through the authenticated
  tunnel and includes a PKCE challenge, not the verifier.
- OAuth tokens and the PKCE verifier remain inside the DevBox `azd` process.
- LoopKey validates the HTTPS Entra host, exact localhost callback port,
  `state`, and S256 PKCE parameters before exposing a request.
- Success is reported only after `azd auth login --check-status` verifies the
  selected profile.
- The request server binds only to DevBox loopback.
- The tunnel, request files, callback listener, and helper processes are
  removed on success, failure, cancellation, or expiration.

The current prototype does not yet refresh Brix's Green token cache. A Green
integration must perform that separate refresh after `azd` verification.

## Agent skill

The complete agent workflow is maintained once in:

```text
skills/loopkey/SKILL.md
```

Thin discovery adapters point GitHub Copilot, Codex, and Claude Code to that
canonical file:

```text
.github/skills/loopkey/SKILL.md  # GitHub Copilot
.agents/skills/loopkey/SKILL.md  # Codex
.claude/skills/loopkey/SKILL.md  # Claude Code
```

The adapters contain only standard Agent Skills metadata and a canonical-file
reference. Authentication instructions and security invariants are not copied
between agent hosts. Normal installation uses `gh skill install` through
`scripts/setup.ps1`, which copies the canonical skill into each selected
agent's user-scoped skill directory so it is available from any repository.

## Build and test

```powershell
dotnet test .\LoopKey.slnx
dotnet publish .\src\LoopKey\LoopKey.csproj -c Release -o .\artifacts\win-x64
```

The project targets .NET 10 and publishes a self-contained Windows x64
executable.

## Status

LoopKey `v0.2.0-alpha.2` has been validated end to end with fresh Corp and
Green logins between a remote Windows DevBox and a laptop. It remains an
experimental alpha release.
