# Agent Instructions & Cross-Service Protocol

## Role & Context
You are working on this project as part of a multi-service ecosystem consisting of a C# Launcher (`Among Launcher`), a Python Backend (`Among-Backend`), and a Web/Bot Frontend (`Among Lobbies`).

## Absolute Paths to Architecture Specs
1. **Launcher Spec (C# / Rider):**
   `C:\Users\meowfire\RiderProjects\Among Launcher\launcher-spec.md`

2. **Backend Spec (Python / PyCharm):**
   `C:\Users\meowfire\PycharmProjects\Among-Backend\backend-spec.md`

3. **Frontend & Bot Spec (Web/Bot / WebStorm):**
   `C:\Users\meowfire\WebstormProjects\Among Lobbies\frontend-spec.md`

---

## Mandatory Execution Rules

### 1. READ Rule (Before Implementing)
Before writing code that touches networking, serialization, API calls, deep-links, or IPC communication, you **must read the target service's spec file** using the absolute paths above to ensure compatibility.

### 2. UPDATE Rule (After Implementing / Modifying)
**CRITICAL:** Whenever you implement a new feature, modify an API endpoint, alter a JSON payload, update mod-sync behavior, or change URI/IPC structures:
* You **MUST immediately update your own project's `...-spec.md` file** to reflect the exact changes made.
* Ensure the spec file documents:
  - Any new or modified endpoints/routes.
  - Expected request/response JSON models (including field types, nullability, and hashes).
  - Protocol changes (e.g., IPC ports, handshake keys, deep-link formats).
  - File management behaviors (e.g., mod manifest rules, directory structures).

Keeping your spec file up to date is required so the AI agents operating in the other IDEs always have an accurate contract to build against.

---

## Subagent Workflow for Bug Fixes and Features

When fixing bugs or implementing features, follow this exact workflow:

### Phase 1: Debugging (Parallel)
Dispatch multiple debugger subagents simultaneously. The number depends on the number of independent problems:
- 1-2 problems → 2-3 debugger agents
- 3-5 problems → 5-7 debugger agents
- 6+ problems → 8-15 debugger agents

Each debugger agent:
- Is READ-ONLY (must NOT edit files)
- Investigates one specific problem
- Reports root cause and potential fix
- Returns findings

Wait for ALL debugger agents to complete before proceeding.

### Phase 2: Building Fixes (Parallel)
Dispatch builder agents to implement fixes based on debugger findings:
- Each builder handles one fix
- Builders can edit files in their assigned project only
- Builders should implement the fix as described by the debugger

### Phase 3: Reviewing (Parallel)
Dispatch reviewer agents to verify each fix:
- Each reviewer is READ-ONLY
- Reviews one specific fix
- Reports if the fix is correct and complete
- Reports any issues found

### Phase 4: Fix Issues (If Needed)
If reviewers find problems:
1. Dispatch a builder agent to fix the issue
2. Dispatch a reviewer agent to verify the fix
3. Repeat until no problems remain

### Phase 5: Verification
- Run build commands to verify compilation
- Run tests if available
- Push changes to GitHub

### Example Flow

```
Problem: Version checker doesn't show update button

1. Dispatch 3 debugger agents:
   - Debug 1: Trace VersionChecker.CheckForUpdateAsync
   - Debug 2: Check GitHub API response format
   - Debug 3: Verify version comparison logic

2. Wait for all debuggers to complete

3. Dispatch 1 builder agent:
   - Fix: Handle non-semver tags in VersionChecker

4. Dispatch 1 reviewer agent:
   - Review: Verify the fix is correct

5. If issues found:
   - Dispatch builder to fix
   - Dispatch reviewer to verify
   - Repeat until clean

6. Build and push
```

### Important Rules
- Never skip the debugging phase
- Always wait for all debuggers before building
- Always review after building
- Always fix issues found by reviewers
- Never assume a fix is correct without review