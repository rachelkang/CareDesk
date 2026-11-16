# CareDesk

> 🏥 **Sample application** — CareDesk mimics a healthcare scheduling workflow for a fictional clinic, using invented patient and appointment data. It was built for, and presented at, **.NET Conf 2026**. 📺 [Watch the session video](https://www.youtube.com/watch?v=CnV8B-ER67k).

CareDesk is a local Windows scheduling workbench for a fictional "Visual Studio Community Clinic." It pairs a patient-messaging inbox with an appointment scheduler so staff can triage requests, propose or save changes, and track replies — all backed by a local SQLite database.

## Features

- **Messages inbox** — chronological conversations, linked appointments, request status/ownership, and a saved draft reply per request.
- **Schedule editor** — agenda + appointment editor, with suggested actions (reschedule / reply) generated from the patient's message and current booking.
- **Conflict-aware suggestions** — booked-time conflicts are detected and explained; suggestions never auto-save a schedule change.
- **Sample or Live AI mode** — deterministic sample suggestions out of the box, or live suggestions via the OpenAI .NET client when configured.
- **Local-only data** — SQLite store at `%LOCALAPPDATA%\CareDesk\caredesk.db`; no external messaging transport (replies are recorded locally, not delivered).

## Getting started

**Prerequisites:** Windows, .NET 10 SDK + Windows Desktop runtime, and Visual Studio with the .NET desktop development workload.

1. Open `CareDesk.slnx` in Visual Studio and restore NuGet packages.
2. Set **CareDesk.Desktop** as the startup project.
3. Build with **Build Solution**, then launch with **F5** (debug) or **Ctrl+F5** (without debugging).

Every build, launch, and test run must happen in that same Visual Studio instance — see [Development workflow](#development-workflow) below.

## Architecture

| Project | Responsibility |
|---|---|
| `CareDesk.Core` | Scheduling rules, messaging/work-request contracts, clinic time conversion, mutation boundaries |
| `CareDesk.Infrastructure` | Shared SQLite store, scheduling recommendation adapters |
| `CareDesk.Desktop` | WPF MVVM app composing Core + Infrastructure |
| `CareDesk.Tests` | Tests covering Core, Infrastructure, and desktop view models |

Dependencies point inward (Desktop → Infrastructure → Core).

## Messages and Schedule

- **Messages**: inbox, chronological conversation, linked appointment, request status/owner, and one saved draft reply. The synthetic care team is **Rachel Kang** and **Barbara Boyajian** (the example patient is now **Harshada Hole** and the example clinician is **Dr. Andy Sterland**); staff can **Assign to me / Unassign** or reassign through the roster.
- **Schedule**: agenda + appointment editor. Opening an appointment from Messages shares the same request/conversation identity. **Suggested actions** proposes an appointment change and/or reply without creating duplicate drafts.
- Appointment and communication outcomes are tracked separately: saving a request-linked appointment change moves the request to **Reply pending** without auto-recording a reply; a later appointment change flags an older draft for review instead of silently replacing staff edits.
- Draft edits autosave to SQLite and flush before navigation/shutdown; optimistic revisions prevent stale views from overwriting reassignment or newer text.
- There's no external messaging transport — **Record reply** actions store an outgoing item locally as **Recorded locally · not delivered**.

## Rescheduling and request assistance

- From **Schedule**, editing an existing appointment's **Change appointment** fields and choosing **Save changes** updates that booking (never creates a duplicate). Saving is disabled until something changes; a request-linked appointment must be assigned to the current operator first.
- A linked request shows the read-only **Patient request** plus **Suggested actions**: **Update** applies the proposed change in one step, **Update differently** focuses the manual fields for an explicit **Save changes**, and **Reject** dismisses the proposal. **Record message** / **Edit draft** handle the reply side.
- A fuchsia badge marks pending **Patient requests** in the agenda; acted-on suggestions collapse into compact headers tagged **UPDATED**, **REJECTED**, **RECORDED**, **EDITING**, or **DRAFT**. Committed outcomes persist across restarts; rejection/editing badges are session-only.
- **Conflict example:** a request from **Mads Kristensen** to move to a time already booked with the same clinician is detected automatically — the reschedule action is hidden and the reply explains the conflict, with no AI call needed in either mode. Overlaps with the same clinician count as conflicts; adjacent times, other clinicians, and cancelled bookings don't.
- Suggestions never auto-save a schedule change, and manual scheduling remains available throughout.

### Sample vs. Live AI mode

- **Sample mode** (default) generates clearly labeled deterministic appointment and reply suggestions — no external call.
- **Live mode** uses the OpenAI .NET client to generate both suggestions from the stored patient message and exact scheduling facts; replies are rejected if they omit the required time/clinician or misstate an unsaved change as completed. Configure it with:

  ```
  dotnet user-secrets set "CAREDESK_AI_API_KEY" "<key>" --project .\src\CareDesk.Desktop\CareDesk.Desktop.csproj
  dotnet user-secrets set "CAREDESK_AI_MODEL" "<model>" --project .\src\CareDesk.Desktop\CareDesk.Desktop.csproj
  ```

  (or set `CAREDESK_AI_API_KEY` / `CAREDESK_AI_MODEL` as process environment variables). Missing configuration or service errors are surfaced directly — Live mode does not silently fall back to Sample mode.

## Sample data

Existing appointments and messages persist across launches and schema updates. Use **Sample data → Reset sample data…** to restore the coherent synthetic schedule, conversations, ownership, and drafts (confirmation required before writing). Times are shown in **Eastern Time**.

## Development workflow

All builds, launches, and test runs must go through the **current Visual Studio instance** with `CareDesk.slnx` open — no command-line builds, launches, or test runners.

- **Build/launch:** Build Solution → set `CareDesk.Desktop` as startup → Start Debugging (F5) or Start Without Debugging (Ctrl+F5).
- **Test:** open **Test → Test Explorer** in that same instance and run tests there (xUnit + Visual Studio adapter); keep the results visible.
- **After C# changes:** stop the app, rebuild, and relaunch — XAML Hot Reload does not reload C# logic.
- To repeat a clean scenario: **Sample data → Reset sample appointments…**, then stop/rebuild/relaunch.

This policy is defined for Copilot in [.github/skills/caredesk-development-skill/SKILL.md](.github/skills/caredesk-development-skill/SKILL.md) and [.github/copilot-instructions.md](.github/copilot-instructions.md), and applies across checkouts/workspaces/sessions. Keep both files with the solution when moving or cloning it.

## Known limitations

CareDesk is a single-user, local demo:

- Manual schedule saves don't enforce clinician availability or prevent overlaps (only request-linked suggestions check for conflicts).
- No production, clinical-use, authenticated-identity, patient-delivery, privacy-compliance, distributed-locking, or multi-user guarantees.
- Not connected to an EHR; replies are recorded locally only and never delivered to patients.