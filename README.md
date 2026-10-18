# LoopKey

LoopKey forwards a browser-based OAuth loopback redirect from a client Windows
machine to an authentication command running on a remote Windows host.

LoopKey does not know about a specific identity, tenant, account type,
application, or credential cache. All application-specific behavior is defined
in a local JSON profile:

- the login command and `{callbackPort}` argument template;
- environment variables for the login command;
- allowed authorization hosts and required query parameters;
- the verification command and expected success output;
- an optional post-auth command.

```text
Host                                         Client
----                                         ------
configured login command listens on localhost
LoopKey creates a private Dev Tunnel
LoopKey publishes a one-time request    --->  loopkey connect <tunnel> <request>
                                              connects the private tunnel
                                              opens the default browser
login command receives localhost reply <---  provider redirects to client localhost
login command stores credentials
configured verification runs           --->  helper reports success and exits
optional post-auth command runs
```

The client command does not receive or store access tokens, refresh tokens, or
the PKCE verifier. The authorization URL is fetched through the authenticated
private tunnel rather than placed in shell history.

> [!IMPORTANT]
> LoopKey is an experimental Windows-first mechanism. A profile can execute
> local commands, so profile files must be reviewed and trusted before use.

## Quick start

An agent with terminal access can complete setup from the public repository.
Give it this prompt:

> Set up LoopKey from https://github.com/tcauth/LoopKey on this Windows
> machine. Follow `docs/setup.md`, install the user-scoped `loopkey-setup` and
> `loopkey` skills for your agent host, build a validated profile when this is
> the authentication host, complete Dev Tunnel login, and verify the documented
> postconditions.

Or run setup directly from a checkout.

Client:

```powershell
.\scripts\setup.ps1 -Role Client -Agent github-copilot
```

Host, with a reviewed profile configuration:

```powershell
.\scripts\setup.ps1 -Role Host -Agent github-copilot `
  -ProfileConfig C:\path\to\profiles.json
```

Use `codex`, `claude-code`, or `all` for `-Agent`. Setup installs the
checksum-verified release binary, configures user `PATH`, installs missing CLI
prerequisites, authenticates Dev Tunnels, installs both canonical skills at
user scope, and optionally installs the supplied profile configuration. It
installs `loopkey-setup` for onboarding and profile construction, and `loopkey`
for daily authentication.

See [`docs/setup.md`](docs/setup.md) for the versioned bootstrap command,
matching Dev Tunnel identity checks, profile schema, and verification steps.

## Profile configuration

The default configuration path is:

```text
%USERPROFILE%\.loopkey\profiles.json
```

Start from [`examples/profiles.example.json`](examples/profiles.example.json).
The example names no real provider or account and must be adapted to the
authentication CLI being forwarded.

The `loopkey-setup` skill can construct this file with the user. It discovers
local commands, reads trusted CLI help or documentation, optionally performs a
controlled local authorization-URL structure probe, merges the profile without
discarding existing entries, and validates the candidate before installation.
It must never guess provider-specific values or persist authorization-request
secrets.

Validate configuration without starting authentication:

```powershell
loopkey config validate
```

`LOOPKEY_CONFIG` or `--config <path>` selects a different file.

## Usage

Start a configured request on the host:

```powershell
loopkey request --profile work
```

LoopKey creates a private tunnel, starts the configured login command with the
selected callback port, validates the captured authorization URL against the
profile policy, and prints one client command:

```powershell
loopkey connect <tunnel-id> <request-id> --metadata-port 8401
```

Run that command on the client. It connects the tunnel, fetches the one-time
request, opens the default browser, waits for host verification, and exits.

Use `loopkey --help` for port, expiration, tunnel ID, and config overrides.

## Security model

- Tunnels are private by default; LoopKey never enables anonymous access.
- Requests expire after 10 minutes by default.
- A request uses a random 128-bit identifier and a dedicated temporary tunnel.
- The authorization URL is available only through the authenticated tunnel.
- OAuth tokens and local credential material remain in the configured host
  process.
- The profile explicitly allowlists authorization hosts, the redirect query
  parameter, required query parameters, and required query values.
- LoopKey requires the redirect URI to match the profile's exact loopback URI
  template after substituting the callback port.
- Success is reported only after the configured verification command exits
  successfully and emits the configured success marker.
- An optional post-auth command is fail-closed: nonzero exit means failure.
- The request server binds only to host loopback.
- The tunnel, request files, callback listener, and child processes are
  removed on success, failure, cancellation, or expiration.

## Agent skills

The two provider-neutral workflows are maintained independently:

```text
skills/loopkey-setup/SKILL.md  # installation and profile construction
skills/loopkey/SKILL.md        # daily authentication
```

Each agent host has thin discovery adapters for both skills:

```text
.github/skills/{loopkey-setup,loopkey}/SKILL.md  # GitHub Copilot
.agents/skills/{loopkey-setup,loopkey}/SKILL.md  # Codex
.claude/skills/{loopkey-setup,loopkey}/SKILL.md  # Claude Code
```

Normal installation uses `gh skill install` through `scripts/setup.ps1`, which
copies both canonical skills into each selected agent's user-scoped directory.
The use skill does not install software or modify profiles; it hands setup and
repair requests to the setup skill.

## Build and test

```powershell
dotnet test .\LoopKey.slnx
dotnet publish .\src\LoopKey\LoopKey.csproj -c Release -o .\artifacts\win-x64
```

The project targets .NET 10 and publishes a self-contained Windows x64
executable.

## Status

LoopKey `v0.3.0-alpha.2` is an experimental alpha release. The tunnel and
loopback mechanism has been validated end to end; authentication applications
are supplied entirely through user-owned profile configuration.
