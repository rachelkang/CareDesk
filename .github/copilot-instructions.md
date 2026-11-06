# CareDesk execution requirements

These requirements apply to the CareDesk solution/repository in every checkout, workspace, and session, not only the workspace where they were authored. They cover all current and future CareDesk projects, tests, and supporting scripts, including validation and delegated work. They do not apply to unrelated solutions sharing a parent directory or workspace.

Locate the solution root using `CareDesk.slnx` and its project references; resolve repository paths relative to that root, independent of the working directory or machine-specific checkout path. Keep this guidance and the skill with the solution/repository when moving or cloning it.

Before every CareDesk build, rebuild, launch, debug session, or test run, load and follow `.github/skills/caredesk-development-skill/SKILL.md` from that solution/repository.

- Every build and launch must go through the Visual Studio instance hosting the active CareDesk conversation, with the target checkout's solution open. Do not use CLI builds, CLI launches, direct executable launches, or another Visual Studio instance.
- For an unqualified **run tests** or **rerun tests** request, select and visibly focus Test Explorer in the unique current CareDesk Visual Studio instance, run the full `CareDesk.Tests` project through integrated `get_tests`/`run_tests`, and leave Test Explorer selected with the results visible. Native integration is preferred; deterministic Windows UI Automation may select the Test Explorer tab only after identifying exactly one responsive CareDesk `devenv` instance. Never target a fixed PID or arbitrary Visual Studio process. If focus automation is unavailable, still use the integrated test tools and report that focus could not be verified rather than blocking the run.
- If the required current-instance build, launch, discovery, or test-run integration is unavailable, stop that operation, explain the limitation, and ask the user to perform it in the current instance. Never silently substitute terminal execution or report unverified success.
