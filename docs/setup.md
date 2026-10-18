# Setup

LoopKey runs on two Windows machines:

- the **host** runs the configured authentication command and owns its local
  credential state;
- the **client** connects the private Dev Tunnel and opens the browser.

Run setup once on each machine. The script is idempotent and verifies the
downloaded executable against a release SHA-256 asset.

## Agent-assisted setup

Give an agent with terminal access this prompt:

> Set up LoopKey from https://github.com/tcauth/LoopKey on this Windows
> machine. Follow `docs/setup.md`, install the user-scoped skill for your agent
> host, complete Dev Tunnel login, and verify the documented postconditions.

The agent should inspect `scripts/setup.ps1` before executing it.

For a machine without Git or GitHub CLI, download the versioned setup script:

```powershell
$setup = Join-Path $env:TEMP "loopkey-setup.ps1"
Invoke-WebRequest `
  https://raw.githubusercontent.com/tcauth/LoopKey/v0.3.0-alpha.1/scripts/setup.ps1 `
  -OutFile $setup
Get-Content $setup
& $setup -Role Client -Agent github-copilot
```

Review the downloaded script before executing it.

## Client

Run from a LoopKey checkout:

```powershell
.\scripts\setup.ps1 -Role Client -Agent github-copilot
```

Use `codex`, `claude-code`, or `all` for `-Agent`. The default is `all`.

## Host

Create and review a profile configuration for the authentication application.
Start from `examples/profiles.example.json`, then run:

```powershell
.\scripts\setup.ps1 -Role Host -Agent github-copilot `
  -ProfileConfig C:\path\to\profiles.json
```

The configuration is copied to
`%USERPROFILE%\.loopkey\profiles.json` and validated by the installed binary.
LoopKey does not install or configure the authentication application itself.

Host setup uses Dev Tunnel device-code authentication so no browser must open
on the remote host. This authenticates Dev Tunnels only; it is unrelated to
the authentication command later launched by a profile.

## Dev Tunnel identity

The client and host must use the same Dev Tunnel identity. Setup writes a
non-secret identity summary to:

```text
%LOCALAPPDATA%\LoopKey\devtunnel-identity.json
```

Pass `-ExpectedDevTunnelUser <user>` on the second machine to fail setup when
the identities differ.

## Profile schema

The root object contains a `profiles` map. Each profile has:

| Field | Required | Purpose |
|---|---:|---|
| `login.command` | yes | Executable, `.cmd`, or `.bat` launched on the host |
| `login.arguments` | yes | Arguments containing `{callbackPort}` |
| `login.environment` | no | Environment supplied only to the login command |
| `verification.command` | yes | Non-interactive command used to prove success |
| `verification.arguments` | yes | Verification arguments |
| `verification.successOutputContains` | yes | Exact case-sensitive success marker |
| `authorization.allowedHosts` | yes | Exact HTTPS authorization host allowlist |
| `authorization.redirectUriParameter` | yes | Query key containing the redirect URI |
| `authorization.redirectUriTemplate` | yes | Exact loopback URI containing `{callbackPort}` |
| `authorization.requiredQueryParameters` | no | Query keys that must be nonempty |
| `authorization.requiredQueryValues` | no | Query keys that must equal fixed values |
| `postAuth` | no | Generic command object run after verification |

Command strings, arguments, and environment values support Windows environment
expansion such as `%USERPROFILE%`.

The login command must honor the `BROWSER` environment variable and invoke it
with the complete HTTPS authorization URL. The URL must use the profile's
configured loopback redirect URI template.

Validate a file before installing or using it:

```powershell
loopkey config validate --config C:\path\to\profiles.json
```

Profiles are trusted code because their commands execute locally. Do not use a
profile from an untrusted source.

## Postconditions

Client:

```powershell
& "$env:LOCALAPPDATA\LoopKey\loopkey.exe" --version
devtunnel user show
gh skill list
```

Host:

```powershell
& "$env:LOCALAPPDATA\LoopKey\loopkey.exe" --version
& "$env:LOCALAPPDATA\LoopKey\loopkey.exe" config validate
devtunnel user show
```

Start a new terminal after first setup if `loopkey` is not yet resolved from
`PATH`.

## Non-default options

- `-Version <tag>` selects a release other than the script's pinned default.
- `-Agent none` skips agent skill installation.
- `-SkillRef <ref>` installs the skill from a different tag or commit.
- `-ProfileConfig <path>` installs a reviewed host profile file.
- `-ConfigurationPath <path>` changes the installed profile path.
- `-SkipDevTunnelLogin` installs files without starting Dev Tunnel login.
- `-InstallDirectory <path>` supports controlled tests or non-default layouts.
- `-ExpectedDevTunnelUser <user>` requires the expected tunnel owner.
- `-SkipPathUpdate` leaves user `PATH` unchanged.

Do not use `-SkipDevTunnelLogin` for a production setup unless
`devtunnel user show` already succeeds.
