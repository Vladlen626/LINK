---
name: preview-svg
description: Look at a drawing in docs/images (the README's SVG pictures of Link's windows) before committing it - render it to PNG with headless Edge and read the PNG.
---

# Preview an SVG drawing

The built-in browser cannot open `file://`, so render with headless Edge into the scratchpad and read the PNG:

```powershell
& .agents\skills\preview-svg\render.ps1 -Svg docs\images\overlay.svg -Png <scratchpad>\overlay.png -Width 560 -Height 604
```

Width and height are the SVG's own `width` and `height`. Then read the PNG with the Read tool.

## How the drawings are made

- Link's dark theme from `src/Link.Desktop/Themes/Colors.Dark.xaml`: window `#1B2027`, text `#F2F5F8`, muted
  `#BAC5D0`, accent `#D95E0E` / `#FF9F4A`, green dot `#3FB950`. Labels are the real ones from the XAML.
- Numbered orange callouts beside the window; the README explains each number. Keep the numbers when a drawing
  changes, or update the README's list with it.
- No real project names, chats or paths of the user: the example project is "My game" in `C:\Users\me\...`.

## Pitfalls

- Several spaces in one `<text>` collapse into one: place each part in a `<tspan x="…">`.
- `&` and `<` in text are `&amp;` and `&lt;`.
- Text that does not fit is not cut: check the PNG for overlaps (a placeholder running under a button).
- `overview.svg` embeds the card and the panel from `overlay.svg` and `panel.svg`; after changing them, update its
  copy of their content too.
