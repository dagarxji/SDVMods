# Auto-Eat + Eidee Easy Fishing Bridge

A tiny SMAPI compatibility mod for:

- **Eidee Easy Fishing** 1.4.0+ (`net.eidee.stardew_valley.easy_fishing`)
- **Auto-Eat** 2.3.3+ (`Permamiss.AutoEat`)

## What it fixes

Eidee Easy Fishing's Auto Recast can dispatch a cast and set its internal
`_autoRecastDispatched` flag. Auto-Eat can then decide to eat during that cast's
charge phase and call `FishingRod.resetState()` before Eidee sees the rod reach
its normal in-use state. Eidee is then left thinking a cast is still pending, so
it never dispatches the next one.

This bridge detects Auto-Eat's `EatFood` call while Eidee Auto Recast is armed.
After the eating animation/state has completely finished, it repairs Eidee's
recast state and lets **Eidee itself** issue the next cast. It does not implement
its own slower autocast loop.

## Behavior

1. Start fishing normally.
2. Eidee Auto Recast takes over as usual.
3. Auto-Eat reaches your configured energy/health threshold and eats.
4. The bridge waits for eating to finish.
5. Eidee's Auto Recast session is re-armed.
6. Eidee performs the next full-power autocast at its normal speed.

If you manually switch away from the fishing rod during the interruption, the
bridge cancels the pending resume.

During an Eidee Auto Recast session, the mod displays a tracker on the right
side of the screen with the current fish per in-game hour, fish caught in the
current session, the last completed autocast session's rate, and the highest
rate recorded this game session. It remains visible for five seconds after
autocasting stops. Only fish items count; fishing garbage is ignored.

The tracker can be shown or hidden with `F8` by default, or with a keybinding
configured through Generic Mod Config Menu. Hold `Shift` and left-click-drag
the tracker to move it; its position is saved automatically.

The daily fish catch cap is the maximum at fishing level 10. The active cap is
one tenth of that maximum per fishing level (for example, the default maximum
of 500 gives a cap of 50 at level 1, 100 at level 2, and 500 at level 10).
Setting the maximum to 0 disables the cap.

At base fishing level 10, every fish caught has a 0.1% chance to drop an
**Angler's Seal**. It cannot drop below level 10, and temporary fishing buffs
do not bypass that requirement. Hold the seal and use it with the action or
tool button to permanently remove the daily catch cap for that farmer. The
unlock is stored in the save, and the seal stops dropping once it is used.

## Important stamina setting

Eidee has its own safety check that stops Auto Recast *before* a cast that would
reduce stamina to zero. Auto-Eat can't eat while Eidee's stop menu is open.
Therefore, set Auto-Eat's stamina threshold **above the energy cost of one cast**
(e.g. 10-16 rather than 0/8) so Auto-Eat gets a chance to eat before Eidee's
zero-stamina safeguard fires.

## Build

This project targets `net6.0`, matching the current Eidee Easy Fishing project.

From this folder on Windows:

```powershell
dotnet build -c Release
```

To build and deploy directly into Vortex's Stardew Valley staging directory:

```powershell
.\build.ps1 -DeployToVortex
```

The default staging path is `%APPDATA%\Vortex\stardewvalley\mods`. Override it
when using a different Vortex setup:

```powershell
.\build.ps1 -DeployToVortex -VortexModsPath "D:\Vortex\stardewvalley\mods"
```

If Stardew Valley isn't auto-detected, set `SDV_GAME_DIR` first, for example:

```powershell
$env:SDV_GAME_DIR = "C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley"
dotnet build -c Release
```

Then install the built mod folder (containing `manifest.json` and
`AutoEatEideeBridge.dll`) under Stardew Valley's `Mods` directory.

## Diagnostics

Normal startup log:

```text
[Auto-Eat + Eidee Easy Fishing Bridge] Auto-Eat/Eidee bridge initialized.
```

When the compatibility path actually triggers, SMAPI's Debug/Trace log can show:

```text
Auto-Eat started eating during Eidee Auto Recast; preserving the recast session.
Re-armed Eidee Auto Recast after Auto-Eat finished eating.
```

If either upstream mod changes the private methods/fields this bridge relies on,
it fails closed and prints a clear error instead of blindly altering state.
