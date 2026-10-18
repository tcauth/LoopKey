---
name: loopkey
description: Complete Corp or Green browser authentication for a remote Windows DevBox through a private Dev Tunnel. Use when an agent is blocked by expired azd credentials, interactive Entra login, MFA, Conditional Access, or a localhost OAuth callback that must be completed from the user's laptop.
license: MIT
compatibility: Requires Windows, loopkey.exe, Microsoft Dev Tunnels CLI, and the azd-corp or azd-green wrapper on the DevBox. The laptop needs loopkey.exe and Dev Tunnels CLI.
metadata:
  author: tcauth
  version: "0.1.0"
---

# LoopKey remote authentication

Use LoopKey to keep the OAuth client, PKCE verifier, and credential cache on
the DevBox while the user completes interactive Entra authentication in the
laptop browser.

## When to use

Use this skill when all of the following are true:

- work is running on a remote Windows DevBox;
- a Corp or Green `azd` profile requires interactive browser authentication;
- the login uses an HTTP loopback callback such as
  `http://localhost:<port>`;
- the user can run one command on a laptop with `loopkey` and `devtunnel`
  installed.

Do not use this skill for service principals, managed identities, device-code
flows, WAM-only flows, or authentication that does not use an HTTP loopback
callback.

## Security invariants

- Never enable anonymous Dev Tunnel access.
- Never print, copy, request, or log access tokens, refresh tokens, Temporary
  Access Pass values, authorization codes, or PKCE verifiers.
- Do not send the Entra authorization URL to the user. LoopKey transfers it
  through the authenticated private tunnel.
- Use the installed `azd-corp.cmd` or `azd-green.cmd` wrapper. Do not use bare
  `azd`, because the profiles must keep separate credential caches.
- Do not silently fall back to device-code authentication.
- Treat the laptop command as short-lived request metadata. Send it only to
  the user who requested authentication.
- Report exact failures. Do not return a success-shaped fallback.
- Success requires the DevBox-side profile verification, not merely a browser
  page that says authentication completed.

## DevBox procedure

1. Determine the required profile from the failed command, tenant, resource,
   or wrapper:

   - Corp tenant: `72f988bf-86f1-41af-91ab-2d7cd011db47`
   - Green: use the configured Green tenant ID; never guess it.

2. Confirm the prerequisites without exposing credentials:

   ```powershell
   loopkey --help
   devtunnel user show
   Test-Path "$env:USERPROFILE\.azd-profiles\azd-corp.cmd"
   Test-Path "$env:USERPROFILE\.azd-profiles\azd-green.cmd"
   ```

   Check only the wrapper needed for the selected profile.

3. Start the request on the DevBox. The command waits for the complete login,
   so run it as a managed background task when the agent host supports
   background commands.

   Corp:

   ```powershell
   loopkey request --profile corp
   ```

   Green:

   ```powershell
   loopkey request --profile green --tenant-id <green-tenant-id>
   ```

   `LOOPKEY_GREEN_TENANT_ID` may replace the explicit Green tenant argument
   when it is already configured.

4. Wait until LoopKey prints:

   ```text
   Authentication request ready.

   Run this once on the laptop:
     loopkey connect <tunnel-id> <request-id> --metadata-port <port>
   ```

5. Return that exact, single `loopkey connect` command to the user. State that
   it must run on the laptop and remain open while the browser authentication
   completes. Do not ask the user to run `devtunnel connect`, choose a port, or
   open a separate authorization URL.

6. Wait for the DevBox request process to finish. LoopKey will:

   - accept the forwarded localhost callback;
   - let `azd` exchange the code using its DevBox-held PKCE verifier;
   - verify the selected profile with `azd auth login --check-status`;
   - report terminal status to the laptop helper;
   - stop the callback and metadata listeners;
   - stop and delete the temporary private tunnel;
   - delete the temporary authorization request files.

7. After LoopKey exits successfully, rerun the exact token preflight or command
   that originally failed. A generic `azd` login check is not a substitute for
   validating the target resource and scopes.

8. For Green authentication, refresh Brix's
   `~/.brix/azure/access_tokens` cache using the environment's established
   refresh procedure, then rerun the Brix token check. A successful
   `azd-green` check alone does not refresh Brix.

9. Resume the blocked task only after the profile-specific and target-resource
   checks pass.

## User-facing response

Use this minimal format while the request is waiting:

```text
<Corp|Green> authentication is ready.

Run this once on your laptop:

<exact loopkey connect command>

The command will connect the private tunnel, open your default browser, wait
for DevBox verification, and exit automatically.
```

After completion, report whether the DevBox profile and original target token
check passed. Do not include token contents.

## Failure handling

- **`loopkey` or `devtunnel` missing:** report the missing prerequisite and
  stop. Do not install software or alter `PATH` without permission.
- **Dev Tunnel authentication failure:** report the exact error. Do not enable
  anonymous access.
- **Port in use:** retry only with two known-free ports by passing
  `--callback-port` and `--metadata-port`; include the metadata port in the
  laptop command printed by LoopKey.
- **Wrong account or tenant:** allow LoopKey to fail, then create a new request.
  Never reuse an earlier authorization URL or request ID.
- **Expired request:** create a new request. Do not extend or replay the old
  request.
- **Browser succeeds but LoopKey fails:** trust the DevBox verification result,
  inspect the exact `azd` error, and do not claim success.
- **Cancellation or agent interruption:** ensure the request process has
  exited. LoopKey normally removes its tunnel in `finally`; if cleanup itself
  failed, identify the exact generated tunnel before deleting only that
  tunnel.
