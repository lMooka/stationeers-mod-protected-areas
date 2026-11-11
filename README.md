# Protected Areas (Stationeers mod)

Anti-griefing for public servers. Each player claims a 30x30 area around their base; inside it **only
authorized players** can touch anything — the same idea as the Tool Cupboard in Rust, but done with a
command instead of an object.

For installation, see [INSTALL.md](INSTALL.md).

## What an area blocks

For anyone who is **not** authorized, inside the area:

- placing structures (the build cursor turns red and shows the reason);
- using tools or kits on structures: build steps, deconstruction, repair, upgrade, painting;
- using devices: doors and airlocks, levers, valves, on/off switches, buttons;
- taking items from or putting items into slots of machines, lockers and crates (chips included);
- picking up or using loose objects on the ground;
- mining or depositing terrain.

**Damage:** while the area's owner is **offline**, structures inside it take no damage at all
(grenades, explosions, fire, pressure) and explosions do not dig the terrain next to the area. While the
owner is online, damage works normally.

**Explosions:** grenades, explosives, dynamite, gas canisters, furnaces and any other explosion do
**nothing** — no damage to structures, objects or players, no terrain digging — inside an area or within
**10 blocks** (20 m) of it. Exception: if the exploding object belongs to a player who is authorized in
that area, the explosion works normally. An object "belongs" to whoever carried it last (structures: to
whoever built them); this is saved with the world. Objects nobody has carried since the mod was installed
have no owner and are treated as unauthorized. Change the distance with `ExplosionBufferBlocks`
(`-1` = off).

**Trespassing:** anyone who enters another player's area gets a warning and has **30 seconds** to leave
(reminders at 20, 10 and 5 s). If they stay they are removed: moved outside if they have the mod, or
**disconnected from the server** if they do not. Leaving the area or being authorized cancels the
countdown; dead and unconscious players are not counted. Turn it off with `TrespassKickSeconds = 0`.

Not blocked: damage to players.

## Rules for creating an area

| Rule | Default |
|---|---|
| Maximum size | 30x30 (`radius` 15). The area is a cube, so it also reaches 15 m above and below its center. |
| Areas per player | 1 |
| Overlap | Areas never overlap. **This also applies to admins.** |
| Other players' structures | You cannot create an area that contains structures built by someone else. |
| World spawn points | No area within 100 m of the world start/respawn locations. |

**Admins** (the host, plus the SteamIDs listed in `AdminSteamIds`) ignore every restriction except
overlap: they build and interact inside any area, create areas of any size anywhere, and manage other
players' areas.

Known limits:

- The builder of a structure is only recorded if it was built while the mod was running (or by the
  host). Old structures built by clients before the mod was installed have no known owner and do not
  prevent an area from being created.
- Respawn points built by players (inside bases) do not count for the 100 m rule.
- Some interaction requests reach the host without saying who sent them. In those cases the mod blocks
  the request when an unauthorized player is within 10 m of the device and no authorized player is
  nearby.

## Requirements

- BepInEx + [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).

## Multiplayer

Rules are enforced **on the host**. Only the host needs the mod.

| Who joins | What happens |
|---|---|
| Client **without** the mod | The blocked action simply does not happen and they get a notice in the console/chat. They can use the commands through chat (`/area ...`), except `show`. |
| Client **with** the mod | They also receive a copy of the areas from the host: red cursor, "blocked" tooltips and `show` to see the boundaries. |

- The copy of the areas travels through the game's own chat, in messages the mod hides. A client with
  the mod **never sends anything on its own**: it only asks for the copy (once per session) after
  receiving an `[Area]` message from the host — a block notice or the answer to a command. On servers
  without the mod nothing is sent to chat.
- Commands sent through chat are intercepted on the host and are not shown to other players.
- While a local (non-Workshop) mod is enabled, the host's LaunchPad stops offering its mod list to
  joining players. This does not prevent anyone from joining.

## Build and install from source

```powershell
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

The script builds the mod and copies it to `Documents\My Games\Stationeers\mods\ProtectedAreas` (close
the game first). A new local mod starts disabled: tick **Protected Areas** in the LaunchPad mod list.

## Commands

In **chat** use `/area ...`; in the **console (F3)** use `area ...` (no slash).

| Command | What it does |
|---|---|
| `create <name> [radius]` | Creates the area centered on you. `radius` = half the side (default and maximum 15). |
| `add [name] <player>` | Authorizes a player (name, part of the name, or SteamID). |
| `del [name] <player>` | Removes the authorization. |
| `kick [name] [force]` | Moves unauthorized players out of the area. See below. |
| `remove [name]` | Deletes the area. |
| `list` | Lists every area in the world. |
| `info [name]` | Details, authorized players, and whether the owner is online (structures immune or not). |
| `show [on\|off]` | Draws the boundaries: green = you may modify, red = blocked. Needs the mod in your game. |
| `help` | Lists the commands. |

Without `[name]`, the command uses the area you are standing in. Only the owner or an admin can manage
an area.

### About `kick`

In Stationeers each player's position is controlled by **their own** game, not by the server. So:

- the host and clients **with the mod** are teleported outside, on the nearest side;
- clients **without the mod** cannot be moved — the command reports who stayed, and they are
  disconnected automatically when their 30 s trespass warning runs out;
- an admin can use `kick <name> force`, which **disconnects from the server** anyone who could not be
  moved.

### Testing alone

As the host you are an admin and ignore the restrictions. To see the blocking while playing alone, turn
that off temporarily (`AdminsBypass = false` in the config) and:

```
area create base
area del YourName       (removes yourself from the list: the area now blocks you)
... try to build, open a door or mine inside the cube ...
area add YourName       (allows you again)
```

## Configuration

`BepInEx\config\mooka.stationeers.protectedareas.cfg` (created on first run; the host's file is the one
that counts). Options from the old `[Areas]` section (version 1.x) are no longer used.

| Section | Option | Default | |
|---|---|---|---|
| Rules | `DefaultRadius` | 15 | Radius used when `create` is given none. |
| Rules | `MaxRadius` | 15 | Maximum radius for regular players (15 = 30x30). |
| Rules | `MaxAreasPerPlayer` | 1 | Areas per player (0 = unlimited). |
| Rules | `AdminOnlyCreate` | false | Only admins create areas. |
| Rules | `SpawnProtectionDistance` | 100 | Minimum distance from the world spawn points (0 = off). |
| Rules | `DamageProtection` | OwnerOffline | When structures are immune: `OwnerOffline`, `Always` or `Never`. |
| Rules | `ProtectInteractions` | true | Blocks devices, slots and containers for unauthorized players. |
| Rules | `ExplosionBufferBlocks` | 10 | Explosions do nothing inside an area or within this many blocks (1 block = 2 m) of it, unless the object's owner is authorized there. -1 = off. |
| Rules | `TrespassKickSeconds` | 30 | Time an unauthorized player may stay in an area before being removed (0 = off). |
| Admins | `AdminsBypass` | true | Admins ignore the restrictions (except overlap). |
| Admins | `AdminSteamIds` | (empty) | SteamID64 of extra admins, comma-separated. The host is always an admin. |

## Where the data lives

`Documents\My Games\Stationeers\saves\<StationName>\ProtectedAreas.xml` on the host's computer, written
the moment an area changes (it does not depend on saving the game). It applies to every save of that
station. In a brand-new world the file is only created after the first save.

## Testing

`tools/selftest.ps1` builds a test variant of the mod, starts the game in a new throwaway world, runs the
in-game self-test, prints the result from the BepInEx log and reinstalls the normal build. The game must
be closed. It covers single-player and simulated host/client message handling; it does not cover a real
second player.

## Code

| File | |
|---|---|
| `ProtectedAreas/Plugin.cs` | BepInEx entry point, configuration and boundary drawing. |
| `ProtectedAreas/Patches.cs` | Patches for building, tools, terrain and chat. |
| `ProtectedAreas/ProtectionPatches.cs` | Patches for interactions, containers, damage, explosions and object ownership. |
| `ProtectedAreas/Guard.cs` | Permission rules, admins and player notices. |
| `ProtectedAreas/Commands.cs` | Chat and console commands, creation rules and `kick`. |
| `ProtectedAreas/Trespass.cs` | Trespass warning with countdown and removal. |
| `ProtectedAreas/Sync.cs` | Multiplayer: sending areas from the host to clients that have the mod. |
| `ProtectedAreas/AreaStore.cs`, `Area.cs` | Model and XML persistence. |
| `ProtectedAreas/SelfTest.cs` | In-game self-test; only compiled with `-p:DefineConstants=SELFTEST`. |
| `tools/selftest.ps1` | Runs the self-test in a new throwaway world and reinstalls the normal build. |
| `tools/decomp/` | Decompiler used to study the game's code. |
