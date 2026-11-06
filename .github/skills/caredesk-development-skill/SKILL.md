---
name: caredesk-development-skill
description: Required for every build, rebuild, launch, debug session, or test run for the CareDesk solution or repository, in any checkout or workspace. Use the current Visual Studio instance and its integrated test tools.
---

# CareDesk development skill

## Scope and required behavior

This is a persistent CareDesk solution/repository policy, not a rule limited to the workspace or conversation in which it was created. Apply it whenever working on the CareDesk solution or repository, regardless of checkout location, workspace name, working directory, or which solution project is open. It covers every CareDesk project, test project, and supporting script, including projects added later, validation after edits, and delegated work. It does not apply to unrelated solutions merely because they share a parent directory or workspace.

Locate the CareDesk solution root by `CareDesk.slnx` and its project references. Resolve paths in this skill relative to that root, not a fixed machine path or the shell's working directory. Keep this skill and `.github\copilot-instructions.md` with the CareDesk solution/repository so the policy travels with it when moved or cloned.

For each operation, use the Visual Studio instance hosting the active CareDesk conversation, not a fixed process from an earlier session, another running instance, or a newly started instance.

Before execution, confirm the current instance has the target checkout's `CareDesk.slnx` open using the available Visual Studio integration. If its identity or solution cannot be confirmed, stop and ask the user to confirm or open the solution in that same instance. Do not select an arbitrary Visual Studio process.

## Build and rebuild

- Use the current instance's integrated build capability (such as `run_build`) or its Build/Rebuild commands. Prefer the affected project when appropriate.
- Read and report the resulting build status and relevant errors. Do not launch after a failed build or treat stale binaries as validation.
- Never substitute terminal builds, `dotnet build`, `dotnet msbuild`, standalone MSBuild, or `devenv` command-line builds, including indirectly through scripts or agents.

## Launch and debug

- Set `CareDesk.Desktop` as the startup project in the current instance and launch through its Start Debugging (F5) or Start Without Debugging (Ctrl+F5) capability.
- After C# changes, stop the existing CareDesk debug/run session through that instance, build successfully there, and launch again there. XAML Hot Reload does not reload C# logic.
- Verify the launch succeeded using the instance's run/debug state and, when available, the application window. Report launch failures explicitly.
- Do not use `dotnet run`, `dotnet watch`, direct executable launches, shell launch scripts, or a second Visual Studio instance.

## Every test run

When the user says **run tests**, **rerun tests**, or an equivalent request without a narrower selection, run the full `CareDesk.Tests` project.

1. Select and visibly focus **Test Explorer** in the current CareDesk Visual Studio instance before the run. Prefer native Visual Studio integration. When it does not expose tool-window focus, Windows UI Automation may be used only after identifying exactly one responsive `devenv` instance whose window title belongs to CareDesk; select the visible `Test Explorer` tab item and verify it is selected and not offscreen. Never target a fixed PID or an arbitrary Visual Studio process.
2. Discover/select the requested tests through the current instance's test integration (`get_tests`) and run them through that integration (`run_tests`) or Test Explorer. Prefer the smallest relevant selection unless the user requested the complete suite.
3. After the run, verify Test Explorer remains selected and visible when focus automation is available, so the user can see the results.
4. Report passed, failed, skipped, or undiscovered tests accurately; never equate a zero-test run with success.

If Test Explorer focus automation is unavailable but the current instance's integrated `get_tests` and `run_tests` tools are available, do not block the test run. Run the tests and explicitly report that Test Explorer focus could not be verified.

Never substitute `dotnet test`, `vstest.console`, command-line test runners, or scripts that bypass Test Explorer. If tests require a build, that build must also run through the current instance.

## Missing capabilities and delegation

If the available tools cannot build, launch, discover tests, or run tests in the current instance, stop that operation and explain the missing capability. Ask the user to perform the corresponding action there and confirm its result. Do not silently fall back to the terminal or claim an unverified action succeeded.

Pass these requirements to any agent performing builds, launches, or test runs. An agent without the necessary current-instance integration must return the operation to the caller rather than bypass this workflow.
