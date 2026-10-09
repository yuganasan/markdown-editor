# markdown-editor

small markdown editor for windows. native win32, no electron, no browser, nothing to install. the exe is like 60kb and opens instantly.

editor on the left, live preview on the right (or just one of them).

## features

- edit / split / preview modes
- dark and light theme
- recent files sidebar
- undo/redo that doesn't eat memory on big files
- auto backups every 5 min of actual use
- lists continue when you hit enter, tab indents them
- clickable links, relative .md links open in the editor

## build

```powershell
.\build.ps1
```

uses the c# compiler that already comes with windows, so no visual studio or sdk needed.

## open .md files with it

```powershell
.\install.ps1
```

only writes to HKCU, no admin needed. `uninstall.ps1` removes it.

## shortcuts

| key | does |
|---|---|
| ctrl+n / o / s | new / open / save |
| ctrl+shift+s | save as |
| ctrl+b / i / e | bold / italic / code |
| ctrl+k | link |
| ctrl+1 / 2 / 3 | edit / split / preview |
| ctrl+d | toggle theme |
| ctrl + / - | font size |

## web-version

older version, an html editor opened in an edge app window with a small c# launcher for file access. kept it around, the main one is the native app.
