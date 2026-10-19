---
name: Bug report
about: X11 keypad digits are mapped to navigation keys while NumLock is on, then their committed text is dropped
title: "[Bug]: X11 numpad digits are treated as End/Down/PageDown and their text is dropped"
labels: []
---

## Environment

- Linux (KDE Plasma, X11 / XWayland)
- .NET 10.0.12
- `Aprillz.MewUI` 0.22.1, `Aprillz.MewUI.Platform.X11`, `Aprillz.MewUI.Backend.MewVG.X11`
- Repro app: PiWeft Desktop (`TextBox` / `MultiLineTextBox`)
- `MEWUI_IME_DEBUG=1` for the logs below

## Reproduction steps

1. Open any MewUI window containing a `TextBox` or `MultiLineTextBox` and focus it.
2. Turn NumLock on.
3. Press keypad `1`, `2`, `3` (the numeric keypad, not the top-row digits).

## Expected behavior

`1`, `2`, `3` are inserted as text.

## Actual behavior

No digit is inserted. The key is interpreted as `End` / `Down` / `PageDown`; depending on the control, the caret moves or nothing visible happens. With `MEWUI_IME_DEBUG=1` the platform reports both a navigation key and a committed digit, then drops the digit:

```text
[XIM] Xutf8LookupString status=4 bytes=1 filtered=False
[X11][IME] [X11Key] im=XimInputMethod handled=False fwd=True text='1'
[X11][IME] [X11Deliver] '1' dropped by handled KeyDown
```

The same happens for the other keypad position keys (`KP_Home`, `KP_Up`, `KP_PageUp`, `KP_Left`, `KP_Begin`, `KP_Right`, `KP_End`, `KP_Down`, `KP_PageDown`, `KP_Insert`, `KP_Delete`).

## Minimal repro / code / screenshots / logs

The current X11 key-name mapping reads only the first keysym and ignores NumLock:

```csharp
// src/MewUI/Shared/Platform/Linux/X11/X11KeyMap.cs
var ks = NativeX11.XLookupKeysym(ref e, 0); // index 0, NumLock is not consulted
var key = X11KeyMap.Resolve(ks, (int)e.keycode, Display, _keysymOfGroup);
```

`XLookupKeysym(ref e, 0)` returns the `XK_KP_End` / `XK_KP_Down` / ... keysym even when NumLock makes the key type a digit. `MultiLineTextBox.OnKeyDown` then handles `End` / `Down` / `PageDown` and sets `e.Handled = true`. `X11WindowBackend` deliberately drops the platform's committed text when the KeyDown was handled:

```csharp
// src/MewUI/Shared/Platform/Linux/X11/X11WindowBackend.cs
bool suppressed = keyArgs?.Handled == true && imeResult.IsKeyTranslation;
if (suppressed) { /* committed text is dropped */ }
```

Two possible fix directions:

- Resolve the key using the current modifier/lock state (for example through `Xutf8LookupString`/XKB state), and map the keypad positions to `Key.NumPad0`..`Key.NumPad9`/`Key.Decimal` when NumLock is active.
- Or do not suppress a committed key translation when the key was handled only as a navigation key; at minimum document the conflict.

A temporary PiWeft-side workaround (rewriting the `KeyEventArgs.Key` in `PreviewKeyDown` when NumLock/Mod2 is active) reproduced correctly with the logs above, but it has been removed from the application repository so the fix can live in MewUI.

## Before submitting

- [x] I searched for existing issues and did not find a duplicate.
- [x] I included enough information to reproduce or investigate the problem.
