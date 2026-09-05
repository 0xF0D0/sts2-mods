# ReplayLab isolated launcher

`Run-Isolated.ps1` prepares and optionally runs a disposable Slay the Spire 2 export for `UndoReplayLab`. The launcher creates a new run directory under `%LOCALAPPDATA%\Temp\Sts2UndoReplayLab\<guid>`; a supplied `-RunRoot` is treated as the parent for that unique folder. It copies only the game export files needed to start the game and installs only `UndoReplayLab` under the isolated `mods` directory.

Game export files are hard-linked when Windows permits it and copied otherwise. The source game directory, its mods, profiles, and saves are never used as writable test locations. The isolated export receives an `override.cfg` that enables Godot's custom user directory at `Sts2UndoReplayLab/<run id>`. The launcher also passes the expected user-data path to the harness for its guard. Every run includes `--force-steam=off`, which skips Steam initialization, cloud saves, and workshop loading.

For the release NullPlatform path, the launcher creates a new `default/1/settings.save` under `%APPDATA%\Sts2UndoReplayLab\<run id>`. It contains only the mod consent and enabled `UndoReplayLab` entry needed for the test, plus `skip_intro_logo`; no original settings are read or copied. A result is accepted only when it is valid JSON with `HasFailure` false, matching process and user-data identity, all required phase labels reporting `PASS`, and exactly two completed replay trials. Each trial must match the recorded event/action/choice counts, finish four actions and cancel none; combined counters must equal both trials' totals. The `post-replay-singleplayer-action` phase is explicitly `UNSUPPORTED` and is not a passing live-input test.

The bounded test passed in the actual game engine on 2026-09-06 KST. See [results and remaining coverage](../../docs/NATIVE_REPLAY_VALIDATION.md).

The parent process must build `UndoReplayLab` first. From this repository, run:

```powershell
dotnet build .\UndoReplayLab -c Release
.\tools\ReplayLab\Run-Isolated.ps1
```

Useful options:

```powershell
.\tools\ReplayLab\Run-Isolated.ps1 -PrepareOnly
.\tools\ReplayLab\Run-Isolated.ps1 -GameDir 'D:\SteamLibrary\steamapps\common\Slay the Spire 2' -TimeoutSeconds 300
.\tools\ReplayLab\Run-Isolated.ps1 -RunRoot 'D:\ReplayRuns\undo-001'
```

`-PrepareOnly` performs setup and writes `metadata.json` without starting the game. A normal run writes distinct `stdout.log`, `stderr.log`, `godot.log`, `result.json`, and `metadata.json` files below the run root. The launcher reports the process id immediately, waits in one-second intervals, stops only that known pid on timeout, and retains the run directory for inspection. A process exit code of zero without `result.json` is a failure.

Do not launch while another `SlayTheSpire2.exe` process is running. The launcher refuses to start in that case. It does not deploy, publish, delete run directories, or produce the runtime report; those remain the responsibility of the parent test workflow.
