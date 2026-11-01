# Icon masters

The SVG files here are the source of every GameShift icon. The `.ico` files under `src/GameShift.App` are generated from them; never edit an `.ico` by hand.

Regenerate after changing a master, from the repository root:

```
dotnet run tools/icons/IconGen.cs
```

A second run must reproduce the same files. The generator renders only flat `rect`, `path` and `circle` shapes with `#RRGGBB` colours and stops on anything else.

| Master | Output | Frames |
|---|---|---|
| `app-icon-16.svg` (16 and 20 px), `app-icon-256.svg` (24 px and up) | `Assets/Icons/app-icon.ico` (exe, installer), `Resources/gameshift.ico` (window) | 16, 20, 24, 32, 48, 64, 256 |
| `tray-{idle,active,paused,warning,error}-{dark,light}-taskbar.svg` | `Assets/Icons/tray-{state}-{dark,light}.ico` | 16, 20, 24, 32 |

The tray picks the dark or light set from the Windows taskbar theme, and the frame for the current display scaling (16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %).

## Drawing on the 16 px grid

- Each tray state has its own shape, so it reads without its colour: idle is one outline chevron, active two solid chevrons, paused a chevron with two bars, warning a small chevron with a triangle, error a small chevron with a round badge.
- Badges sit in the lower right 8 x 8 px and never overlap the chevron.
- Put chevron apexes on a pixel boundary (x = 8 or x = 6) with arms at exactly 45 degrees, and keep strokes at least 2 px wide.
- Flat colours only: no gradients, transparency effects or shadows.
- If a frame looks soft, move the shape in the master onto whole pixels and regenerate.
