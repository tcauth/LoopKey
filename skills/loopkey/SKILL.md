---
name: loopkey
description: Use an already configured LoopKey profile to complete browser-based OAuth, MFA, or Conditional Access on a Windows client while the authentication command runs on a remote Windows host.
license: MIT
compatibility: Requires Windows, loopkey.exe, Microsoft Dev Tunnels CLI, matching private-tunnel identities, and a validated LoopKey profile on the host.
metadata:
  author: tcauth
  version: "0.3.0-alpha.2"
---

# Use LoopKey

Use LoopKey to forward an OAuth localhost callback while keeping the
authentication command and credential state on the host. This skill is only
for daily use of an existing, validated profile. For installation, profile
creation, profile repair, or machine onboarding, use the `loopkey-setup` skill.

## Security invariants

- Never install software, create a profile, or edit profile configuration in
  this skill.
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

1. Determine the intended existing profile name from the user or configured
   environment. Do not infer application-specific values.
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
  exact prerequisite and stop. Use `loopkey-setup` only after an explicit
  setup request.
- **Missing or invalid profile:** stop and use `loopkey-setup`; never
  synthesize or repair application-specific values in this skill.
- **Dev Tunnel authentication failure:** report the exact error; never enable
  anonymous access.
- **Port in use:** retry only with two known-free ports using
  `--callback-port` and `--metadata-port`.
- **Expired request:** create a new request; never reuse its URL or request ID.
- **Browser succeeds but LoopKey fails:** trust configured host verification
  and post-auth results, not the browser page.
- **Cancellation:** ensure the request process exits. If automatic cleanup
  fails, identify and remove only the exact generated tunnel.
