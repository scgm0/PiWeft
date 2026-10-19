---
name: Bug report
about: Shift+? with a Chinese IME inserts an ASCII slash instead of the IME's full-width question mark on X11
title: "[Bug]: X11 Chinese IME Shift+? inserts '/' instead of '？'"
labels: []
---

## Environment

- Linux (KDE Plasma, X11 / XWayland)
- .NET 10.0.12
- `Aprillz.MewUI` 0.22.1, `Aprillz.MewUI.Platform.X11`, `Aprillz.MewUI.Backend.MewVG.X11`
- fcitx5 with a Chinese input method (Rime)
- Session hints: `XMODIFIERS=@im=fcitx`, `INPUT_METHOD=fcitx`; `GTK_IM_MODULE` / `QT_IM_MODULE` are empty
- Repro app: PiWeft Desktop (`MultiLineTextBox` composer)

## Reproduction steps

1. Focus a MewUI `TextBox` or `MultiLineTextBox`.
2. Activate the Chinese input method (fcitx5 + Rime) and switch to Chinese punctuation.
3. On a US keyboard, press `Shift` + `/` (the key that normally produces `?`).

## Expected behavior

The IME commits the Chinese full-width question mark `？` (the same behavior as other toolkits in the same session).

## Actual behavior

Only the ASCII slash `/` is inserted. `?` is not produced either, which suggests the raw unshifted key translation is being inserted instead of the IME's committed text.

## Minimal repro / code / screenshots / logs

No MewUI application code is needed: any focused text input in the session reproduces it. Enabling `MEWUI_IME_DEBUG=1` should capture the input path and clarify whether IBus or the XIM fallback was selected:

```text
[IMFactory] Hints: GTK_IM_MODULE=(null) QT_IM_MODULE=(null)
[IMFactory] Trying IBus...
[IMFactory] Selected: ...
[XIM] XFilterEvent filtered=... isDown=True ...
[XIM] Xutf8LookupString status=... bytes=...
[X11][IME] [X11Key] ... text='...'
[X11][IME] [X11Deliver] '...' -> MultiLineTextBox
```

Suspected paths in 0.22.1:

1. `X11InputMethodFactory.Create` only looks at `GTK_IM_MODULE` / `QT_IM_MODULE` when deciding whether fcitx is configured. On KDE/fcitx5 those can be empty while `XMODIFIERS=@im=fcitx` and `INPUT_METHOD=fcitx` are set, so the app may try IBus first and fall back to XIM instead of using the configured fcitx path.
2. `XimInputMethod.ProcessKeyEvent` routes filtered events in commit-only mode (`ForwardKeyToApp = true`) and `LookupString` calls `Xutf8LookupString` for them. A filtered punctuation key can therefore deliver its raw key translation while the IME's `？` commit is lost.
3. `IBusInputMethod` has the same fallback shape: when IBus does not handle the key, it uses `XimInputMethod.LookupStringWithoutIc`, bypassing the IME's punctuation conversion.
4. `X11WindowBackend` drops committed text when the KeyDown is handled. If a raw key translation is delivered first and the IME commit arrives later, the observable result is the ASCII character only.

Suggested fixes:

- Prefer the IM configured by `XMODIFIERS` / `INPUT_METHOD`; do not attempt IBus when the session is explicitly configured for fcitx.
- For `XFilterEvent`-owned keys, do not deliver the key's raw `Xutf8LookupString` translation. Let the IME commit the text, or suppress the fallback while a composition/commit is pending.
- Add a regression test with a fake IME that filters `Shift+/` and commits `？`, asserting that no `/` reaches the focused text control.

The same bug is likely reproducible with any IME that converts punctuation (Japanese/Korean), not only Chinese.

## Before submitting

- [x] I searched for existing issues and did not find a duplicate.
- [x] I included enough information to reproduce or investigate the problem.
