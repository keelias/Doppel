# Doppel

**Made by RUDYHILL.**

*Doppel, as in doppelganger: a second you.*

AI bots for BONELAB that are real player bodies, not NPCs. A bot has the same rig you have: real
hands that grab, real legs that walk and jump, a head that looks around. It moves the way you move,
because it learns from recordings of you.

This is a test version. Expect rough edges, and please tell me what breaks.

## What the bots do

- Walk, run, jump and roam the map on their own, or follow you.
- Fight zombies, NPCs and enemy bots with guns, melee weapons and fists. They pick up weapons they
  find, reload, take cover, and put their weapon away when they do not need it.
- Climb walls with their bare hands. You can climb any surface the same way: close an empty hand on
  a wall to hold on.
- Drive vehicles they were shown how to drive, and follow you by car.
- Pick things up and try them out, then use them the way you showed.
- Go and try whatever you point at.

Bots only use what is on the map. Spawn a gun if you want them armed, spawn a car if you want them
to drive.

## The menu (BoneMenu > Doppel)

| Option | What it does |
| --- | --- |
| Spawn bot | A friendly bot. |
| Spawn enemy bot | A bot that fights you. |
| Remove bots / Bring bots to me | What they say. |
| Play on its own | The bot decides what to do: roam, fight, pick things up, drive. |
| Stand still / Follow me | What they say. |
| Practise driving | Bots drive again and again and keep what works better. |
| Show what it is thinking | A line of text over each bot's head. |
| Learn from me | Watches everything you do and learns from it. |
| Forget the last 30 seconds | Throws away the last half minute of learning. |
| Forget what it learned | Clears what the bots worked out by themselves. |
| Moves (record in slots) | Short moves you record yourself: idle, walk, run, jump, punch, melee, pick up and more. Pick a slot, then hold both sticks down to record. |
| Record clone / Play clone / Loop clone | Record everything you do and have a bot repeat it exactly. |
| Copy my avatar | Bots take the avatar you are wearing. |

## It comes with recordings

The mod includes the maker's own recordings (moves, climbing, driving, item use), so bots know how to
move from the first start. They are copied into `UserData/Bot Player` the first time the mod runs and
never overwrite anything you recorded yourself. Record your own and the bots use yours.

## Known issues

- Driving in tight places is poor: lots of backing out.
- Climbing sometimes fails at the top edge.
- Punches do not always do damage.
- A bot can only use an item the way a recording shows.

## Install

Use a mod manager (r2modman or the Thunderstore app), or copy the `Mods` and `UserData` folders into
your BONELAB folder. Needs MelonLoader and BoneLib.

## Credits

Created by **RUDYHILL**.
