# Doppel

**A BONELAB mod by RUDYHILL.**

*Doppel, as in doppelganger: a second you.*

Doppel adds AI bots that are real player bodies, not NPCs. A bot has the same rig you have: real
hands that grab, real legs that walk and jump, a head that looks around. It moves the way you move,
because it learns from recordings of you.

This is a test version. Expect rough edges.

## What the bots do

- Walk, run, jump and roam the map on their own, or follow you.
- Fight zombies, NPCs and enemy bots with guns, melee weapons and fists. They pick up weapons they
  find, reload, take cover, and put their weapon away when they do not need it.
- Climb walls with their bare hands.
- Drive vehicles they were shown how to drive, and follow you by car.
- Pick things up and try them out, then use them the way you showed.
- Go and try whatever you point at.

Bots only use what is on the map. Spawn a gun if you want them armed, spawn a car if you want them
to drive.

## Installing the mod

Get it from Thunderstore with a mod manager, or build it yourself (below) and copy `Mods/Doppel.dll`
into the game's `Mods` folder. It needs [MelonLoader](https://thunderstore.io/c/bonelab/p/LavaGang/MelonLoader/)
and [BoneLib](https://thunderstore.io/c/bonelab/p/gnonme/BoneLib/).

The menu is in BoneMenu > Doppel. What each option does is in [the mod's page text](thunderstore/README.md).

## Building it

You need the .NET SDK (6 or newer), Python 3, and BONELAB installed with MelonLoader and BoneLib
(the build takes the game's own libraries from the game's folder).

```
python package.py
```

That builds `Doppel.dll` and makes `dist/Doppel-<version>.zip`, the package Thunderstore takes.

If the game is not in Steam's usual folder, say where it is first:

```
set GameDir=D:\Games\BONELAB
python package.py
```

## What is where

| Folder | What it is |
| --- | --- |
| `Mods/BotBrain` | Everything the bot is and does: its body, senses, brain, fighting, climbing, driving, learning. |
| `Mods/Doppel` | The mod as it is given out: one DLL with the brain built in. |
| `Mods/BotPlayer` | For working on the bot only: a small loader that swaps in each new build of the brain while the game runs. Not part of the release. |
| `Common` | Two small helpers: where the mod keeps its files, and showing the player a notice. |
| `starter` | The maker's own recordings (moves, climbing, driving, item use). The mod copies them into `UserData/Bot Player` the first time it runs, and never over a player's own. |
| `thunderstore` | The store page: manifest, readme, icon, changelog. |

A good place to start reading is `Mods/BotBrain/Bot.cs` (a bot's body, once a frame) and
`Mods/BotBrain/BotBrain.cs` (how it decides what to do).

### The lab

`Mods/BotBrain/Lab.cs` is a test bench used while making the mod: commands read from a text file,
and a log of what every bot does. It is off unless a file named `enable.txt` is in
`UserData/Bot Player/lab`. Players do not need it.

## Known issues

- Driving in tight places is poor: lots of backing out.
- Climbing sometimes fails at the top edge.
- Punches do not always do damage.
- A bot can only use an item the way a recording shows.

## Credits

Created by **RUDYHILL**.

## License

MIT. See [LICENSE](LICENSE).
