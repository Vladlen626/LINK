---
name: preview-svg
description: Look at a drawing in docs/images (the README's SVG pictures of Link's windows) before committing it - render it to PNG with headless Edge and read the PNG.
---

# Preview an SVG drawing

The built-in browser cannot open `file://`, so render with headless Edge into the scratchpad and read the PNG:

```powershell
& .agents\skills\preview-svg\render.ps1 -Svg docs\images\studio.svg -Png <scratchpad>\studio.png -Width 980 -Height 600
```

Width and height are the SVG's own `width` and `height`. Then read the PNG with the Read tool.

## How the drawings are made

- Two drawings: `overview.svg` (Link's card and panel over a 3D editor) and `studio.svg` (the task list, a task's
  thread, the Harness column). Redraw a part when the UI it shows changes.
- Link's dark theme from `src/Link.Desktop/Themes/Colors.Dark.xaml`: window `#1B2027`, text `#F2F5F8`, muted
  `#BAC5D0`, accent `#D95E0E` / `#FF9F4A`, green dot `#3FB950`. Labels are the real ones from the XAML.
- No callouts: the README's text names what the picture shows, in English.
- No real project names, chats or paths of the user: the example project is "My game" in `C:\Users\me\...`.

## Pitfalls

- Several spaces in one `<text>` collapse into one: place each part in a `<tspan x="…">`.
- `&` and `<` in text are `&amp;` and `&lt;`.
- Text that does not fit is not cut: check the PNG for overlaps (a placeholder running under a button).
