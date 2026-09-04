# Auto-Eat + Eidee Easy Fishing Bridge

A tiny SMAPI compatibility mod for:

- **Eidee Easy Fishing** 1.4.0+ (`net.eidee.stardew_valley.easy_fishing`)
- **Auto-Eat** 2.3.3+ (`Permamiss.AutoEat`)

It also optionally syncs fishing/mastery progression with two other mods if
they're installed:

- **Fast Animations** (`Pathoschild.FastAnimations`)
- **TimeSpeed** (`cantorsdust.TimeSpeed`)

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

Generic Mod Config Menu also includes an **Automatically delete fishing
trash** option. When enabled, caught trash and Joja Cola are removed instead
of being kept in the farmer's inventory. Algae and seaweed are preserved. If
the inventory is full and the catch would open the "place in inventory"
popup, the trash is removed straight from the popup — and if nothing else was
in it, the popup is skipped entirely.

The **Auto-destroy items** button on the config page opens a small editor for
your own auto-destroy list: click an item in your inventory to add it, and
click the X next to a listed item to remove it (mouse wheel scrolls long
lists). Listed items are destroyed whenever they're caught while fishing,
even when the trash option above is turned off.

## Fishing speed sync (Fast Animations + TimeSpeed)

If [Fast Animations](https://www.nexusmods.com/stardewvalley/mods/1089) and/or
[TimeSpeed](https://www.nexusmods.com/stardewvalley/mods/169) are installed,
this bridge can balance out how fast fishing becomes as you progress, so
higher fishing/mastery levels don't also grant a disproportionate amount of
extra time in the day. Both mods are optional; each sync feature below is
skipped if its corresponding mod isn't installed.

**Sync fishing animation speed with level/mastery** (enabled by default)
continuously sets Fast Animations' fishing speed multiplier from:

- 1x base
- +1x per fishing level, 0-10
- +1x for each of the other four skills (farming, mining, foraging, combat)
  that has reached level 10, up to +4x
- +1x per mastery level claimed in the Mastery Cave, up to +5x

This reaches 11x once fishing alone is maxed, 15x once every skill is level
10, and the full 20x once every mastery has also been claimed. When disabled,
Fast Animations' fishing speed is left at whatever value is configured in its
own settings.

**Sync time speed with fishing animation** (enabled by default) divides
TimeSpeed's seconds-per-minute settings (indoors, outdoors, mines, Skull
Cavern, and the Volcano Dungeon) by the current fishing animation speed
multiplier while a fishing rod is out, then restores TimeSpeed's original
values as soon as the rod is put away. For example, with TimeSpeed's default
0.7 seconds/minute outdoors, a 2x fishing animation speed becomes 0.35
seconds/minute, and the full 20x becomes 0.035 seconds/minute. This only
applies for the host player, since TimeSpeed only lets the host directly
control the flow of time.

The sync also pauses while a menu is open (for example the "place in
inventory" popup when a catch overflows a full inventory), so time doesn't
race ahead while you rearrange your inventory. In single player this matches
vanilla's menu pause; in multiplayer, where time always passes, time simply
flows at its normal speed until the menu is closed.

The **Time speed sync strength** slider (0-100%, default 100%) controls how
closely the two are tied together. At 100%, the relationship above applies
exactly. At 0%, TimeSpeed is left completely unaffected. Values in between
partially soften the effect.

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
it fails closed and prints a clear error instead of blindly altering state. The
same applies separately to the optional Fast Animations/TimeSpeed sync: if
either mod's internals change, that specific sync feature logs a warning and is
disabled, without affecting the Auto-Eat/Eidee bridge or the other sync feature.
