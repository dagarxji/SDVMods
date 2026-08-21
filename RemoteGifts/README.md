# Remote Social Interactions

A SMAPI mod for Stardew Valley 1.6.x which makes the vanilla Social-tab interaction icons clickable for remote NPC interaction.

## Features

- **Talk icon** — click the vanilla speech-bubble icon to show the NPC's normal current dialogue and grant the normal once-per-day conversation friendship bonus.
- **Gift icon** — click the vanilla present icon to open your backpack and choose an item.
  - If the selected item matches an active vanilla item-delivery quest for that NPC, it is turned in as a quest item.
  - Otherwise it is handled as a normal gift through Stardew's normal NPC item-receive logic.
- The icons get a subtle hover frame and tooltip so they read as clickable without adding permanent gray buttons to the Social page.
- After NPC dialogue closes, you return to the same Social menu object, preserving your scroll position.
- No Gift Discovery dependency.

## Build

Run `build.ps1` from PowerShell in this folder, or build `RemoteSocialInteractions.csproj` in Release mode.

`Pathoschild.Stardew.ModBuildConfig` locates your Stardew Valley install and sets up the game/SMAPI references.

## Notes

- Quest delivery currently targets vanilla `ItemDeliveryQuest` quests (the normal "bring X item to NPC" type).
- Talk intentionally bypasses physical-location checks (including inaccessible rooms), since remote interaction is the purpose of the mod.
