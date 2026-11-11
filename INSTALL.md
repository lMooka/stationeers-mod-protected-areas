# Protected Areas — Installation Guide

Protected Areas is an anti-griefing mod for Stationeers. Players claim a 30x30 area around their base;
inside it only authorized players can build, deconstruct, use devices, take items or mine.

## Who needs the mod?

| You are | Do you need it? |
|---|---|
| The **host** (or playing single-player) | **Yes.** All rules are enforced on the host. |
| A **player joining** a server that uses it | **Optional.** Without it you are still protected and can use every `/area` command in chat. With it you also get the red build cursor, "blocked" tooltips, the `show` boundary display, and you are moved out of an area instead of being disconnected. |

## 1. Install the requirements (once)

The mod is loaded by **BepInEx** through **StationeersLaunchPad**.

1. Close Stationeers.
2. Follow the official StationeersLaunchPad installation steps:
   <https://github.com/StationeersLaunchPad/StationeersLaunchPad>
   (it installs BepInEx and the LaunchPad plugin into your Stationeers game folder).
3. Start the game once. You should see the LaunchPad window with a mod list before the main menu.

## 2. Install Protected Areas

### Option A — Steam Workshop

1. Open the mod's Workshop page and click **Subscribe**.
2. Start the game. In the LaunchPad mod list, make sure **Protected Areas** is ticked.

### Option B — Manual install

1. Close Stationeers.
2. Copy the whole `ProtectedAreas` folder into your local mods folder:

   ```
   Documents\My Games\Stationeers\mods\ProtectedAreas
   ```

   The folder must look like this:

   ```
   ProtectedAreas\
     ProtectedAreas.dll
     About\
       About.xml
       thumb.png
       Preview.png
     GameData\
   ```

3. Start the game. A manually installed mod starts **disabled**: tick **Protected Areas** in the
   LaunchPad mod list, then load.

## 3. Check that it is running

Load or host a world, open the console with **F3** and type:

```
area help
```

You should get the list of commands. If the game answers "unknown command", the mod is not loaded —
go back to the LaunchPad mod list and check that it is enabled.

## 4. Quick start

Use `/area ...` in chat or `area ...` in the F3 console.

| Command | What it does |
|---|---|
| `create <name>` | Claim a 30x30 area centered on you. One area per player. |
| `add <player>` | Let a friend build and use things in your area. |
| `del <player>` | Remove that permission. |
| `info` | Show the area you are standing in, who is authorized and whether the owner is online. |
| `list` | List every area in the world. |
| `show` | Draw the area boundaries (green = you may modify, red = blocked). Needs the mod. |
| `kick` | Move unauthorized players out of your area. |
| `remove` | Delete your area. |

What you should know as a player:

- You cannot claim a spot that overlaps another area, contains another player's structures, or is
  within 100 m of the world spawn.
- If you walk into someone else's area you get a warning and **30 seconds to leave**. If you stay you
  are moved out (with the mod) or disconnected from the server (without it).
- While an area's owner is offline, the structures inside it cannot be damaged.

## 5. Server settings (host only)

After the first run the host can edit:

```
<Stationeers game folder>\BepInEx\config\mooka.stationeers.protectedareas.cfg
```

| Setting | Default | Meaning |
|---|---|---|
| `DefaultRadius` / `MaxRadius` | 15 | Half the side of an area. 15 = 30x30. |
| `MaxAreasPerPlayer` | 1 | Areas each player may own. 0 = unlimited. |
| `AdminOnlyCreate` | false | Only admins may create areas. |
| `SpawnProtectionDistance` | 100 | Minimum distance from the world spawn. 0 = off. |
| `DamageProtection` | OwnerOffline | When structures are immune to damage: `OwnerOffline`, `Always` or `Never`. |
| `ProtectInteractions` | true | Block doors, levers, valves, switches, slots and containers for unauthorized players. |
| `TrespassKickSeconds` | 30 | Time an intruder may stay before being removed. 0 = off. |
| `AdminsBypass` | true | Admins ignore every restriction except overlapping areas. |
| `AdminSteamIds` | (empty) | Extra admins, as comma-separated SteamID64 values. The host is always an admin. |

Restart the game after changing the file.

Areas are stored on the host in
`Documents\My Games\Stationeers\saves\<StationName>\ProtectedAreas.xml`.

## Uninstall

Untick **Protected Areas** in the LaunchPad mod list (or unsubscribe on the Workshop / delete the
`ProtectedAreas` folder) and restart the game. Mods are only loaded and unloaded when the game starts.
Your worlds are not affected; the `ProtectedAreas.xml` file can be deleted safely.

## Troubleshooting

| Problem | Fix |
|---|---|
| No LaunchPad window when the game starts | BepInEx / StationeersLaunchPad is not installed correctly. Redo step 1. |
| Mod is in the list but `area help` is unknown | The mod is not ticked, or you changed it while the game was open. Tick it and restart. |
| As host I am never blocked | Hosts are admins. Set `AdminsBypass = false` to test the restrictions on yourself. |
| A new area "disappears" after a restart of a brand-new world | The area file is written to the save folder, which only exists after the first save. Save the world once. |
| Red cursor / `show` do nothing on a server | They start working after your first interaction with the mod on that server (any `/area` command, or being blocked once). |
