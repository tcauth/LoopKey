---
name: loopkey
description: Install or use LoopKey to forward a browser-based OAuth localhost redirect from a Windows client to a configured authentication command on a remote Windows host. Use for LoopKey setup, interactive login, MFA, Conditional Access, or a loopback callback that must be completed in another machine's browser.
license: MIT
compatibility: Requires Windows, loopkey.exe, Microsoft Dev Tunnels CLI, and a trusted LoopKey profile on the host. The client needs loopkey.exe and Dev Tunnels CLI.
metadata:
  author: tcauth
  version: "0.3.0-alpha.1"
---

# LoopKey remote authentication

Use LoopKey to forward an OAuth localhost callback while keeping the
authentication command and its credential state on the host. Treat every
profile as trusted executable configuration.

## Setup requests

When the user explicitly asks to install or configure LoopKey:

1. Work from a trusted checkout of `https://github.com/tcauth/LoopKey`.
2. Read `docs/setup.md` and inspect `scripts/setup.ps1`.
3. Run setup for the current machine:

   ```powershell
   .\scripts\setup.ps1 -Role <Client|Host> `
     -Agent <github-copilot|codex|claude-code> `
     [-ProfileConfig <reviewed-json-path>]
   ```

4. A host requires a user-owned profile describing the authentication
   application. Never invent tenants, commands, hosts, success markers, or
   post-auth behavior.
5. Complete every postcondition in `docs/setup.md`.

## Security invariants

- Never enable anonymous Dev Tunnel access.
- Never print, copy, request, or log tokens, authorization codes, one-time
  credentials, or PKCE verifiers.
- Do not send the authorization URL to the user. LoopKey transfers it through
  the authenticated private tunnel.
- Treat the profile as trusted code. Review every configured command and
  environment variable before execution.
- Treat the client command as short-lived request metadata and send it only to
  the user who requested authentication.
- Do not silently replace a configured browser flow with another auth mode.
- Success requires the configured host verification and post-auth commands,
  not merely a browser success page.

## Host procedure

1. Determine the intended profile name from the user or existing environment.
   Do not infer application-specific values.
2. Resolve LoopKey and validate the complete configuration:

   ```powershell
   $loopkey = Get-Command loopkey -ErrorAction SilentlyContinue
   if (-not $loopkey) {
     $loopkey = Get-Item "$env:LOCALAPPDATA\LoopKey\loopkey.exe" -ErrorAction Stop
   }
   & $loopkey.Source config validate
   ```

3. Inspect the selected profile. Confirm:

   - the login and verification commands are expected local programs;
   - login arguments contain `{callbackPort}`;
   - authorization hosts and query rules match the application;
   - any post-auth command is intentional.

4. Confirm private tunnel prerequisites:

   ```powershell
   devtunnel user show
   ```

5. Start the host request as a managed background task:

   ```powershell
   & $loopkey.Source request --profile <name>
   ```

6. Wait for LoopKey to print:

   ```text
   Authentication request ready.

   Run this once on the client:
     loopkey connect <tunnel-id> <request-id> --metadata-port <port>
   ```

7. Return that exact client command. State that it must remain open until host
   verification finishes. Do not send a separate authorization URL.
8. Wait for the host process to finish. LoopKey will accept the forwarded
   callback, wait for the login command, run verification, run optional
   post-auth, report terminal status, and delete temporary resources.
9. Rerun the original target operation. A profile verification marker is not a
   substitute for validating the resource and scopes the user actually needs.

## User-facing response

```text
Authentication profile '<name>' is ready.

Run this once on your client:

<exact loopkey connect command>

The command will connect the private tunnel, open your default browser, wait
for host verification, and exit automatically.
```

## Failure handling

- **Missing LoopKey or Dev Tunnels CLI during authentication:** report the
  exact prerequisite and stop. Install only after an explicit setup request.
- **Missing or invalid profile:** stop and request a reviewed configuration;
  never synthesize application-specific values.
- **Dev Tunnel authentication failure:** report the exact error; never enable
  anonymous access.
- **Port in use:** retry only with two known-free ports using
  `--callback-port` and `--metadata-port`.
- **Expired request:** create a new request; never reuse its URL or request ID.
- **Browser succeeds but LoopKey fails:** trust configured host verification
  and post-auth results, not the browser page.
- **Cancellation:** ensure the request process exits. If automatic cleanup
  fails, identify and remove only the exact generated tunnel.
