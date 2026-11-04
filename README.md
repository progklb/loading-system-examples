# Loading Systems: an interactive streaming demo

A Unity 6.3 (URP) demo of seven ways games fit big worlds into small memory, built for a
knowledge-sharing session on AAA game systems. Every system runs against the same simulated
disk and memory budget, so their behaviour can be compared like for like.

| Key | System | Reference games |
|---|---|---|
| 1 | Discrete levels | Half-Life 2 (Source) |
| 2 | Rooms & doors | Metroid Prime, Jak & Daxter, God of War (2018) |
| 3 | Cell grid | The Elder Scrolls V: Skyrim (Creation Engine) |
| 4 | Object streaming | Grand Theft Auto III → V (RenderWare / RAGE) |
| 5 | Chunks: generate or load | Minecraft, No Man's Sky |
| 6 | Quadtree LOD | Microsoft Flight Simulator, Google Earth |
| 7 | World Partition | Unreal Engine 5 |

## Running it

1. Open the project in Unity **6000.3.17f1** (or a later 6.3 patch).
2. Open `Assets/StreamingDemo/Scenes/StreamingDemo.unity`.
3. Press Play. Everything is generated from code at startup.

### Controls

| Input | Action |
|---|---|
| `1`–`7`, `PgUp` / `PgDn`, `[` / `]` | switch system (PgUp/PgDn suits a presentation clicker) |
| `Space` | toggle autopilot |
| `WASD` | drive manually (`Q` / `E` change altitude in the quadtree mode; `E` uses doors in the cell grid) |
| Right mouse drag / wheel | orbit / zoom |
| `T` | top-down view |
| `I` | hide or show the "how it works" text |
| `H` | hide the whole HUD |
| `R` | restart the current system |

## Presenter notes

`Docs/index.html` is a scrollable companion page for the talk. It has one section per system
with a mechanism diagram, the steps, a "show this in the demo" cue list, the core code and
the trade-offs. Open it in any browser.

## Code map

```
Assets/StreamingDemo/
  Scenes/StreamingDemo.unity     camera + light + DemoApp, nothing else
  Resources/Shaders/             VertexLit (vertex colour, alpha = glow), Glow (holograms), GridFloor
  Scripts/Core/
    SimulatedIO.cs               the storage model: priority queue, N in flight, latency, bandwidth
                                 + MemoryBudget
    DemoMode.cs                  base class; the 5-step streaming contract
    DemoApp.cs                   bootstrap, mode switching, frame order
    PlayerAvatar.cs, CameraRig.cs, Metrics.cs, Noise.cs, Palette.cs, DemoContext.cs
  Scripts/Modes/                 one file per system; search for "CORE ALGORITHM"
  Scripts/Rendering/             MeshBuilder, DemoAssets (materials), Tweens
  Scripts/UI/                    HudUi (widget kit on IMGUI), Hud (layout)
```

Every mode follows the same loop, run each frame:

1. **Desired set**: what should be resident, given the player?
2. **Diff** against what is resident or in flight.
3. **Request** what's missing, with a priority.
4. **Evict** what's unwanted or over budget.
5. **Present**: decide what to show while data is missing (nothing, a loading screen, a LOD, a locked door...).

All content is procedural, built with `MeshBuilder` into one mesh per streamable unit.
Meshes are generated when a simulated load completes, standing in for deserialisation.
