# CareDesk

A local Windows scheduling workbench for Visual Studio Community Clinic, using invented patient and appointment data.

## Prerequisites and commands

- Windows with the .NET 10 SDK and Windows Desktop runtime.
- Visual Studio with the .NET desktop development workload to edit and debug the WPF app.

Open `CareDesk.slnx` in the current Visual Studio instance and restore NuGet packages there. Every build and launch must use that instance: build with **Build Solution**, set **CareDesk.Desktop** as the startup project, and launch with **Start Debugging** (F5) or **Start Without Debugging** (Ctrl+F5).

Before every test run, open or activate **Test > Test Explorer** in that same instance, run the selected tests there, and leave Test Explorer visible with the results. Do not substitute command-line builds, launches, or test runners.

The repository skill in [.github/skills/caredesk-development-skill/SKILL.md](.github/skills/caredesk-development-skill/SKILL.md) defines this workflow for Copilot, with automatic guidance in [.github/copilot-instructions.md](.github/copilot-instructions.md). This policy applies to the entire CareDesk solution/repository, including future projects, across checkouts, workspaces, and sessions; it is not tied to this workspace or machine path. Keep both guidance files with the solution/repository when moving or cloning it. Unrelated solutions sharing a parent directory are outside its scope. If current-instance integration is unavailable, Copilot must ask for the action to be performed in that instance rather than silently falling back to terminal execution.

After C# changes, stop the running app, rebuild, and start it again through the current instance; XAML Hot Reload does not reload C# logic.

## Design

The four projects are `CareDesk.Core`, `CareDesk.Infrastructure`, `CareDesk.Desktop`, and `CareDesk.Tests`. Core owns scheduling rules, typed messaging/work-request contracts, clinic time conversion, and the scheduling and communication mutation boundaries. Infrastructure implements the shared SQLite store and scheduling recommendation adapters. The WPF MVVM desktop project composes dependencies with the .NET host; Windows-targeted tests cover Core, Infrastructure, and the desktop view models. Dependencies point inward.

The SQLite database is stored at `%LOCALAPPDATA%\CareDesk\caredesk.db`. Existing appointments and messaging work are retained during schema updates and on launch. Use **Sample data → Reset sample data…** to restore the coherent synthetic schedule, conversations, ownership, drafts, and recorded replies; the reset is confirmed before writing. Dates and times are shown in the Windows **Eastern Time** clinic zone.

## Messages and Schedule

The compact application shell has two connected sections:

- **Messages** shows the scheduling inbox, chronological conversation, linked appointment, request status and owner, and one saved unsent reply draft. The local synthetic care team is **Rachel Kang** and **Barbara Boyajian**; the current operator can use the single **Assign to me / Unassign** action or explicitly reassign work through the roster.
- **Schedule** contains the existing agenda and appointment editor. Opening an appointment from Messages carries the same request and conversation identity. The patient message remains a compact read-only context, and the consolidated **Suggested actions** section offers appointment and reply suggestions without creating duplicate drafts.

Appointment outcome and communication outcome are separate. A successful request-linked appointment change is committed atomically with the request moving to **Reply pending**; it does not record a reply. Confirmation wording is generated from the saved appointment date, time, and clinician. A later appointment change marks an older confirmation draft for review, and staff edits are not replaced without the explicit **Update draft** action.

Draft edits autosave to the shared SQLite store and are flushed before section navigation and shutdown. Ownership and optimistic request/draft revisions prevent stale views from silently overwriting reassignment or newer text. Direct Schedule use for appointments without a linked request retains the original behavior.

There is no external messaging transport. **Record reply**, **Record reply · waiting**, and **Record reply and complete** store an outgoing history item locally with the status **Recorded locally · not delivered**. Reading a conversation affects only the current synthetic operator's unread state; it does not claim or complete the work.

## Rescheduling and request assistance

Open **Schedule**, select an existing appointment, edit its details under **Change appointment**, review **Saved booking → After saving**, and choose **Save changes**. This updates the selected booking rather than creating another appointment. The section stays visible and its fields remain editable before and after suggested actions; **Update differently** is not required to enable them. Saving stays disabled until details change. A request-linked appointment must be assigned to the current operator before its outcome can be changed; unlinked appointments continue to work directly.

Appointments can have linked scheduling messages. Selecting one shows **Patient request** with the unmodified, read-only message, followed by **Suggested actions**. **Update** applies and saves the proposed appointment change in one action with no second save step; **Update differently** focuses the already-editable manual appointment fields, where **Save changes** is required; **Reject** dismisses the proposal. The reply suggestion is regenerated from the patient message and the current proposed or committed appointment facts whenever the schedule context changes. **Record message** stores the suggestion locally using the correct Waiting on patient or Completed outcome, while **Edit draft** opens the same text in the shared composer. Recorded messages are still not delivered to patients. A failed save keeps the request and edit fields intact. Requests survive restarts; reset restores the synthetic relationships.

A fuchsia message-icon badge marks pending **Patient requests** in the agenda and selected appointment. Badges update when switching appointments, saving, or resetting. **Refresh agenda** reloads saved appointments and requests while retaining the selected booking; it asks you to save or discard any unsaved edits first.

Acted-on suggestions stay in context as compact gray headers with an upper-right outcome badge; their details and action buttons collapse independently so an unfinished reply remains available after an appointment update. Saved appointments show **UPDATED**, dismissed suggestions show **REJECTED**, and locally recorded replies show **RECORDED** (not delivered). **Update differently** shows **EDITING** until the booking is saved; **Edit draft** shows **DRAFT** and keeps the shared composer, **Record draft**, and **Update draft** controls available. Failed actions remain available for retry. Outcomes stay with their appointment during navigation and agenda refresh; committed appointment/reply outcomes are restored from stored workflow data on restart, while rejection and editing/draft handoff badges are session-only. A new appointment proposal regenerates the reply suggestion, and resetting sample data clears the badges.

The focused example is a request from **Mads Kristensen** to move to 10:00 AM on the same clinic day. That time is already booked with the same clinician: the reschedule recommendation and its actions are hidden, and the reply suggestion explains that the requested time is unavailable and the current appointment remains unchanged. This availability reply uses exact scheduling facts without an AI call in either mode, and can still be edited or recorded locally. Overlaps with another scheduled booking for the same clinician count as conflicts; adjacent intervals, other clinicians, and cancelled bookings do not. Suggestions never change the saved appointment automatically, and manual editing remains available. The former Rachel Kang patient is now **Harshada Hole**, and clinician Barbara Boyajian is now **Dr. Andy Sterland**. For non-conflicting requests, Sample mode generates clearly labeled deterministic appointment and reply suggestions without an external call. Live mode uses the configured OpenAI model to generate both suggestions from the stored patient message and exact scheduling facts; the reply is rejected if it omits the required time or clinician or falsely claims an unsaved change was completed. Changing the recommendation source or proposed schedule regenerates the reply suggestion without overwriting a saved staff draft. Unsupported requests and service errors are shown in **Suggested actions**; staff can still reschedule or write manually.

Live mode uses the official OpenAI .NET client. Configure `CAREDESK_AI_API_KEY` and `CAREDESK_AI_MODEL` in the process environment or with `dotnet user-secrets set "CAREDESK_AI_API_KEY" "<key>" --project .\src\CareDesk.Desktop\CareDesk.Desktop.csproj` and `dotnet user-secrets set "CAREDESK_AI_MODEL" "<model>" --project .\src\CareDesk.Desktop\CareDesk.Desktop.csproj` before selecting Live mode. Missing configuration and service errors are surfaced without falling back to sample mode. No live endpoint was configured for this workspace.

The application supports local, single-user synthetic scheduling and administrative appointment communication with explicit review before saving or recording. Conflicting requested times suppress reschedule recommendations, but manual saves do not enforce clinician availability or prevent overlapping appointments. It has no production, clinical-use, authenticated-identity, patient-delivery, privacy-compliance, distributed-locking, or multi-user guarantees, and is not connected to an EHR.

## Build and test in Visual Studio

Use the current-instance workflow above for every build, launch, and test run. Tests use xUnit and its Visual Studio adapter; activate Test Explorer before each run and keep its results visible. To repeat a clean scenario, choose **Sample data → Reset sample appointments…**, then stop the app, rebuild, and launch again in the same instance after C# changes. Committed reschedules remain in the local database across restarts until reset.
