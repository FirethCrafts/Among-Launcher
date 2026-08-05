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