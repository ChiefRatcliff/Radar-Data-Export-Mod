# Radar Data Export Mod — Nuclear Option

**A BepInEx mod that streams your in‑game radar, RWR, and avionics picture out of Nuclear Option over a local TCP socket as line‑delimited JSON — so you can build your own maps, RWR displays, AWACS tools, or anything else on top of it.**

*by Ratcliff Defense Systems · v0.1.0*

> Read‑only. Localhost‑only by default. It exposes **only the sensor/datalink picture you already see in‑game** — no hidden units — and it never writes anything back to the game.

---

## What it does

Every frame (configurable, ~10 Hz) the mod reads your faction's detected‑track database and your own aircraft's avionics, serializes them to a single JSON object, and broadcasts it to any connected client over TCP. Snapshots are stateless full pictures, one per line (NDJSON).

There is **no bundled UI** — this repo is just the data source. Point your own tool at `127.0.0.1:7070` and read the stream.

### What you get
- 📡 **Team radar picture** (fog‑of‑war respected) — aircraft, ships, ground, buildings, munitions, with world position + in‑game grid refs
- 🎯 **Per‑missile intel** — seeker type, guidance phase, locked target and shooter ids
- 🛡️ **Egocentric RWR** — who's painting you, **real radar track vs. datalink**, single‑target lock, and missile‑launch warnings
- 🛩️ **Own‑radar contacts** (no datalink), radar on/off + range, and your weapon loadout
- 🧭 Compass bearings, world coordinates, track freshness (`live`/`age`)

---

## How it works

```
 ┌────────────────────────────────┐   TCP · NDJSON · ~10 Hz    ┌──────────────────────┐
 │  Nuclear Option + this mod      │   127.0.0.1:7070  ───────▶ │  your client          │
 │  (RadarDataExportMod.dll)       │   (one JSON object / line) │  (map · RWR · AWACS…) │
 └────────────────────────────────┘                            └──────────────────────┘
```

- **Broadcast‑only.** The server never reads from clients, so there is no command/control surface — a client can only *receive* the picture.
- **Coalescing.** If a client falls behind, only the latest snapshot is sent (each one is a complete picture, so dropping intermediates is harmless).
- **Zero‑cost when idle.** No snapshots are built while no client is connected.

---

## Requirements

- **Nuclear Option** with **[BepInEx 5](https://github.com/BepInEx/BepInEx)** (Mono) installed.
- To build from source: the **.NET SDK** and a local game install (the mod references the game's managed assemblies).

## Installation

Drop the DLL into your game folder:

```
…/Nuclear Option/BepInEx/plugins/RadarDataExportMod/RadarDataExportMod.dll
```

Launch the game once. BepInEx generates the config file and logs:

```
[Info: Radar Data Export Mod by Ratcliff Defense Systems 0.1.0] Radar export listening on 127.0.0.1:7070 @ 10 Hz.
```

The feed is now live whenever you're in a match and a client is connected.

---

## Configuration

BepInEx writes the config to:

```
…/Nuclear Option/BepInEx/config/com.ratcliffdefense.radardataexport.cfg
```

| Section | Key | Default | Notes |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch |
| Network | `BindAddress` | `127.0.0.1` | Use `0.0.0.0` to allow LAN clients |
| Network | `Port` | `7070` | TCP listen port |
| Network | `UpdateRateHz` | `10` | Snapshots/sec (1–60); 10 is the sweet spot |
| Data | `IncludeFriendlies` | `true` | Export own‑team units |
| Data | `IncludeBogeyNames` | `true` | Add the generic reporting name |
| Data | `OnlyMapVisibleUnits` | `true` | Match exactly what's on the in‑game map |
| Data | `IncludeGridReference` | `true` | Add grid refs (e.g. `Cf37`) |

Close the game → edit → relaunch.

---

## The data feed

**Protocol:** open a TCP connection to `127.0.0.1:7070`, then read **newline‑delimited JSON** — each line is one complete snapshot.

World axes: `x` = East, `y` = Altitude, `z` = North (metres). Bearings are compass degrees (0 = N, 90 = E).

*Illustrative snapshot (values are examples):*
```jsonc
{
  "v": 2, "t": 842.3, "faction": "Boscali", "selfId": 5123,
  "hostile": [
    { "id": 9011, "name": "CI-22 Cricket", "bogey": "Fighter", "cls": "Aircraft",
      "x": 18450, "y": 4200, "z": 31200, "grid": "Df41",
      "hdg": 214, "spd": 280, "live": true, "age": 0.3 }
  ],
  "friendly": [
    { "id": 5123, "name": "FS-12 Kestrel", "cls": "Aircraft",
      "x": 5000, "y": 3000, "z": 8000, "hdg": 92, "spd": 245,
      "wpn": [ {"s":0,"n":"AIM-9X","a":2}, {"s":1,"n":"AMRAAM","a":4} ], "wsel": 1,
      "live": true, "age": 0 }
  ],
  "avionics": {
    "radarOn": true, "radarRange": 92600,
    "rcontacts": [ {"id":9011,"name":"CI-22 Cricket","cls":"Aircraft","x":18450,"y":4200,"z":31200} ],
    "rwr": [ {"id":7001,"name":"S-300 Battery","cls":"Building","brg":120,"rng":38400,
              "pwr":0.8,"det":true,"lock":true,"age":0.2} ],
    "mw":  [ {"id":8005,"name":"R-77","brg":95,"rng":12000,"seeker":"ARH","radar":true,"active":true} ]
  }
}
```

### Field reference

**Envelope**

| Field | Type | Meaning |
|---|---|---|
| `v` | int | schema version (`2`) |
| `t` | float | sim time (seconds since level load) |
| `faction` | string | your faction name |
| `selfId` | uint | your aircraft id (`0` if not flying) |
| `hostile` | array | detected enemy/munition tracks |
| `friendly` | array | own‑team units *(if `IncludeFriendlies`)* |
| `avionics` | object | egocentric RWR/radar *(only while piloting)* |

**Contact** (`hostile[]` / `friendly[]`)

| Field | Type | Meaning |
|---|---|---|
| `id` | uint | persistent unit id |
| `name` | string | full model name |
| `bogey` | string | generic reporting name *(optional)* |
| `cls` | string | `Aircraft` · `Missile` · `Ship` · `Ground` · `Building` |
| `x` `y` `z` | float | position (East / Alt / North, metres) |
| `grid` | string | in‑game grid ref *(optional)* |
| `hdg` `spd` | float | heading (0–360) and speed (m/s) — **only while `live`** for hostiles |
| `live` `age` | bool / float | currently observed vs. stale, and seconds since last spotted |
| *missiles add* `seeker` `smode` `tgt` `own` | | seeker type · phase (`lock`/`search`/`passive`) · target id · shooter id |
| *own aircraft add* `wpn` `wsel` | | stores `{s,n,a}` (station, weapon, ammo) and selected station |

**Avionics** (egocentric)

| Field | Type | Meaning |
|---|---|---|
| `radarOn` `radarRange` | bool / float | your radar state and max range |
| `rcontacts[]` | array | **own‑radar direct detections, no datalink** — `{id,name,cls,x,y,z}` |
| `rwr[]` | array | radars painting you — `{id,name,bogey?,cls,brg,rng,pwr,det,lock,fresh?,age}`; `det` = real track on you, `lock` = single‑target lock |
| `mw[]` | array | incoming missiles your MWS holds — `{id,name,brg,rng,seeker?,radar,active}`; `active` = radar missile gone "pitbull" |

### Writing a client

Minimal Python reader (standard library only):

```python
import json, socket

with socket.create_connection(("127.0.0.1", 7070)) as s:
    buf = b""
    for chunk in iter(lambda: s.recv(65536), b""):
        buf += chunk
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            if line.strip():
                snap = json.loads(line)
                print(f"t={snap['t']}  hostiles={len(snap.get('hostile', []))}")
```

Or just peek at it: `ncat 127.0.0.1 7070` (Windows) / `nc 127.0.0.1 7070` (Linux/macOS).

---

## Fog of war & fair play

- Enemy data comes **only** from your faction's detected‑track database — the same picture the game draws on your own map/HUD. The mod never reads an undetected unit's position, and enemy loadouts are never exported.
- It is **read‑only and client‑side** — it sends nothing back to the game or server and cannot affect other players or match state.
- It's still an **external read‑out** of your legitimate data. Whether competitive servers allow external tooling is a community/server‑rules question — check before using in organized play.

---

## Building from source

```bash
dotnet build src -c Release
# If the game isn't at the Steam default path:
dotnet build src -c Release /p:GameDir="D:\Games\Nuclear Option"
```

The build auto‑deploys `RadarDataExportMod.dll` into `BepInEx/plugins/RadarDataExportMod/` (disable with `/p:DeployToGame=false`).

Run the TCP server smoke test:
```bash
dotnet run --project test/SmokeTest -c Release
```

## Project layout

```
src/            The mod (C#)  →  RadarDataExportMod.dll
test/SmokeTest/ TCP server smoke test
```

> The decompiled game assembly (`.decomp/`) and the game's managed DLLs are **not** committed — they're proprietary; don't redistribute them (see `.gitignore`).

---

## Disclaimer

Unofficial, fan‑made mod. Not affiliated with or endorsed by the developers of Nuclear Option. Use at your own risk; modding may break across game updates, and you shouldn't use it to violate any server's rules.

## License

[MIT](LICENSE) © 2026 Ratcliff Defense Systems.

## Credits

Built by **Ratcliff Defense Systems**.
