# Setup

LoopKey runs on two Windows machines:

- the **DevBox** keeps the `azd` process, PKCE verifier, and credential cache;
- the **laptop** connects the private Dev Tunnel and opens the browser.

Run setup once on each machine. The script is idempotent and verifies the
downloaded release digest before installing it.

## Agent-assisted setup

Give an agent with terminal access this prompt:

> Set up LoopKey from https://github.com/tcauth/LoopKey on this Windows
> machine. Follow `docs/setup.md`, install the user-scoped skill for your agent
> host, complete Dev Tunnel login, and verify the documented postconditions.

The agent should clone the public repository, inspect `scripts/setup.ps1`, and
run the command for its current machine and agent host.

For a machine without Git or GitHub CLI, download the versioned setup script
first:

```powershell
$setup = Join-Path $env:TEMP "loopkey-setup.ps1"
Invoke-WebRequest `
  https://raw.githubusercontent.com/tcauth/LoopKey/v0.2.0-alpha.2/scripts/setup.ps1 `
  -OutFile $setup
Get-Content $setup
& $setup -Role Laptop -Agent github-copilot
```

Review the downloaded script before executing it.

## Laptop

Run from a LoopKey checkout:

```powershell
.\scripts\setup.ps1 -Role Laptop -Agent github-copilot
```

Use `codex`, `claude-code`, or `all` for `-Agent` as needed. The default is
`all`. Setup:

1. installs Dev Tunnels CLI and GitHub CLI with `winget` when missing;
2. downloads the newest published `loopkey.exe`;
3. verifies its GitHub release SHA-256 digest;
4. installs it under `%LOCALAPPDATA%\LoopKey`;
5. adds that directory to the user `PATH`;
6. installs the canonical LoopKey skill at user scope;
7. opens Dev Tunnel browser authentication when needed.

## DevBox

Run from a LoopKey checkout:

```powershell
.\scripts\setup.ps1 -Role DevBox -Agent github-copilot
```

For Green, persist the tenant ID at setup time:

```powershell
.\scripts\setup.ps1 -Role DevBox -Agent github-copilot `
  -GreenTenantId <green-tenant-id>
```

If the environment uses Brix, also provide its established cache-refresh
helper:

```powershell
.\scripts\setup.ps1 -Role DevBox -Agent github-copilot `
  -GreenTenantId <green-tenant-id> `
  -GreenPostAuthScript C:\path\to\refresh-green-brix-cache.py
```

LoopKey runs this hook only after Green `azd` verification. A hook failure
makes the overall request fail rather than reporting stale Brix credentials as
success.

In addition to the common setup, the DevBox role installs Azure Developer CLI
when needed and creates isolated wrappers:

```text
%USERPROFILE%\.azd-profiles\azd-corp.cmd
%USERPROFILE%\.azd-profiles\azd-green.cmd
```

DevBox setup uses Dev Tunnel device-code authentication so no browser must open
inside the remote desktop. This device code authenticates Dev Tunnels only; it
is not used for Corp or Green `azd` authentication.

The laptop and DevBox must use the same Dev Tunnel identity. Setup writes a
non-secret identity summary to
`%LOCALAPPDATA%\LoopKey\devtunnel-identity.json`. Pass
`-ExpectedDevTunnelUser <user>` on the second machine to fail setup if the
accounts differ.

## Postconditions

Laptop:

```powershell
& "$env:LOCALAPPDATA\LoopKey\loopkey.exe" --help
devtunnel user show
gh skill list
```

DevBox:

```powershell
& "$env:LOCALAPPDATA\LoopKey\loopkey.exe" --help
devtunnel user show
Test-Path "$env:USERPROFILE\.azd-profiles\azd-corp.cmd"
Test-Path "$env:USERPROFILE\.azd-profiles\azd-green.cmd"
```

Start a new terminal after the first setup if `loopkey` is not yet resolved
from `PATH`.

## Non-default options

- `-Version <tag>` selects a release other than the script's pinned default.
  Setup downloads both `loopkey.exe` and its release checksum without using
  the rate-limited GitHub API.
- `-Agent none` skips agent skill installation.
- `-SkipDevTunnelLogin` installs files without starting interactive Dev Tunnel
  authentication.
- `-InstallDirectory` and `-WrapperDirectory` support controlled test or
  non-default locations.
- `-ExpectedDevTunnelUser <user>` requires the expected private-tunnel owner.
- `-GreenPostAuthScript <path>` configures a fail-closed Green integration
  hook such as the established Brix cache refresh helper.
- `-SkipPathUpdate` leaves the user `PATH` unchanged.

Do not use `-SkipDevTunnelLogin` for a production setup unless
`devtunnel user show` already succeeds.
