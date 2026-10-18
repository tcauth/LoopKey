---
name: loopkey-setup
description: Install or repair LoopKey and build a validated provider-neutral authentication profile from evidence on a Windows client or host. Use for first-time setup, machine onboarding, profile creation, or configuration repair.
license: MIT
compatibility: Requires Windows and terminal access. Building a host profile requires an authentication CLI that supports a configurable localhost callback port and honors the BROWSER environment variable.
metadata:
  author: tcauth
  version: "0.3.0-alpha.2"
---

# Set up LoopKey

Install LoopKey and create trusted local profile configuration. Keep all
provider, tenant, account, command, authorization-host, verification, and
post-auth details in the user-owned profile. LoopKey itself is only the
redirect mechanism.

## Scope

Use this skill only when the user explicitly asks to install, configure,
onboard, repair, or create a profile. After setup succeeds, use the `loopkey`
skill for authentication requests.

## Security invariants

- Treat every profile as trusted executable configuration.
- Never guess commands, tenants, accounts, authorization hosts, redirect
  parameters, verification markers, or post-auth behavior.
- Accept profile values only from the user, installed CLI help, reviewed local
  wrappers, trusted product documentation, or a controlled local probe.
- Never print or return a complete authorization URL. Never retain or expose
  tokens, codes, state, nonce, code challenges, PKCE verifiers, or cookies.
- A controlled probe may emit only structural data needed for the profile:
  authorization host, redirect query-key name, redirect URI shape, query-key
  names, and documented fixed values.
- Never put an observed ephemeral query value in `requiredQueryValues`.
- Never enable anonymous Dev Tunnel access.
- Preserve existing profiles unless the user explicitly asks to replace one.
- Validate a candidate file before installing it. Do not modify the active
  profile when candidate validation fails.

## Install or repair the machine

1. Work from a trusted checkout of `https://github.com/tcauth/LoopKey`.
2. Read `docs/setup.md` and inspect `scripts/setup.ps1`.
3. Determine whether the machine is the browser **Client** or authentication
   **Host**. A machine can be configured for both roles.
4. Run setup:

   ```powershell
   .\scripts\setup.ps1 -Role <Client|Host> `
     -Agent <github-copilot|codex|claude-code|all> `
     [-ProfileConfig <reviewed-json-path>]
   ```

5. Complete every postcondition in `docs/setup.md`. Confirm the client and
   host use the same Dev Tunnel identity.

## Build a host profile

### 1. Establish the target

Ask for only information that cannot be discovered safely:

- a short profile name;
- the authentication application or local command the user intends to run;
- any required account, tenant, or environment selection;
- whether a post-auth refresh is actually required.

Do not ask for passwords, tokens, authorization URLs, or one-time credentials.

### 2. Discover commands

1. Resolve the executable or reviewed wrapper with `Get-Command` or an exact
   path.
2. Inspect local `--help` output and trusted documentation.
3. Identify:
   - an interactive login command with a configurable localhost callback port;
   - confirmation that the login honors `BROWSER`;
   - a non-interactive verification command;
   - a stable, non-secret success marker in successful verification output;
   - an optional post-auth command only when the target workflow requires it.
4. Use `{callbackPort}` only in the login argument that controls the callback
   port. If the application cannot configure its callback port or does not
   honor `BROWSER`, report that it is incompatible and stop.

Prefer an existing user-owned wrapper when it already selects the intended
account or environment. Review it before use. Do not copy its provider-specific
behavior into LoopKey code or shared skills.

### 3. Derive authorization policy

Prefer trusted product documentation. If documentation does not establish the
authorization URL structure, use a controlled local probe:

1. Choose a known-free loopback port.
2. Create a temporary `BROWSER` capture helper in a private temporary
   directory.
3. Start the exact login command with that helper and the chosen port.
4. Parse the captured URL locally without printing its raw value.
5. Retain only:
   - the exact HTTPS host;
   - the query key containing the loopback redirect URI;
   - the loopback URI shape with the port replaced by `{callbackPort}`;
   - names of security-relevant, nonempty query parameters such as `state`,
     `nonce`, or `code_challenge`;
   - fixed query values only when trusted documentation proves they are
     invariant.
6. Stop only the exact probe process and delete the helper, captured URL, and
   temporary directory.

The authorization host must be exact, not a wildcard. The redirect template
must resolve to HTTP loopback. Do not infer a fixed value from one observed
authorization request.

### 4. Generate and merge

1. Start from `examples/profiles.example.json`.
2. Build the candidate with a JSON-aware parser or serializer, never string
   concatenation.
3. Expand no secrets into the file. Environment entries may select an account
   or environment, but must not contain tokens or one-time credentials.
4. When `%USERPROFILE%\.loopkey\profiles.json` exists, parse it and merge the
   new profile while preserving every unrelated profile. Profile names are
   case-insensitive; require explicit approval before replacing an existing
   name.
5. Write the merged candidate to a separate temporary file.

### 5. Validate and install

Run:

```powershell
loopkey config validate --config <candidate-path>
```

Review the resulting non-secret configuration summary:

- profile name;
- command paths and arguments;
- environment variable names, with secret-looking values redacted;
- allowed authorization hosts;
- redirect URI template and required query-key names;
- verification marker;
- whether post-auth is enabled.

If the user's original setup request already authorizes creating a new profile
and there is no existing-name conflict or unresolved field, continue without
another confirmation. Require explicit approval before replacing an existing
profile. Install through setup:

```powershell
.\scripts\setup.ps1 -Role Host `
  -Agent <agent> `
  -ProfileConfig <candidate-path>
```

Then rerun:

```powershell
loopkey config validate
```

Delete the temporary candidate after successful installation.

### 6. End-to-end verification

With the user's approval, use the `loopkey` skill to run one request with the
new profile. Success requires:

- the client connects through the private tunnel;
- the browser completes the configured flow;
- host verification finds the configured stable marker;
- optional post-auth succeeds;
- the original target operation succeeds;
- generated tunnel and temporary request resources are removed.

## Failure handling

- **Insufficient evidence for a field:** stop and ask for trusted
  documentation or the minimum missing value. Never guess.
- **Probe exposes raw authorization data in output:** stop, remove the exact
  temporary artifacts, and do not reuse that request.
- **Candidate validation fails:** keep the active configuration unchanged and
  report the exact error.
- **Existing profile conflict:** require explicit replacement approval.
- **Verification marker is user-specific or unstable:** choose a stable
  non-secret marker from documented or observed successful output.
- **Unsupported login application:** report the missing configurable callback
  port or `BROWSER` support and stop.
