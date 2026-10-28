# SideNotch

A small edge-docked mini player for Windows that controls whatever is playing through Windows media sessions. It prefers YouTube in Chrome. It shows text only: no video, thumbnails or artwork.

## Download

Get `SideNotch.exe` from the [latest release](https://github.com/vinay121314/SideNotch/releases/latest). The exe isn't code-signed, so Windows SmartScreen may warn: choose **More info → Run anyway**, or build it yourself (below).

## Run

Double-click `SideNotch.exe` (or `dist\SideNotch.exe` after building). There's nothing to install; it uses the .NET Framework 4.8 built into Windows 10/11.

- **Open:** click the slim tab on the screen edge, or left-click the tray icon.
- **Hotkey:** Ctrl+Alt+N opens or closes the player from anywhere. If another app already uses that combination, the shortcut is skipped and a note goes in the log.
- **Scroll on SideNotch** (the tab or the open player): scroll down for the next track, up for the previous one. Hold Ctrl while scrolling to change the playing app's volume in the Windows mixer, 5% per notch. This only works while the cursor is over SideNotch.
- **Peek:** hover over the tab to see the title, play state and time without opening the player.
- **Full screen:** the tab hides while a full-screen window (video, game, slideshow) covers its monitor, and returns afterwards.
- **Close the panel:** click the × in its corner, click outside it, or press Esc. SideNotch doesn't show up in Alt+Tab.
- **Move:** drag the tab anywhere. It snaps to the nearer left/right edge of that monitor.
- **Previous / Next:** the buttons either side of play/pause. They're greyed out when the source doesn't offer them. While the next video loads, the panel keeps the last title dimmed and shows a moving loading bar instead of "Nothing playing". If no track arrives within about 6–8 s, it shows what's actually true.
- **Resume:** SideNotch remembers the last track, even across restarts. If Chrome or the tab is closed, the panel shows it as *Last played* with its saved position, and Play reopens it in Chrome at that point (it starts over if the song had finished). The link is read from Chrome's address bar while that video's tab is active (nothing is clicked). If the link wasn't caught, Play opens a YouTube search for the title. Other apps are relaunched through their app ID.
- **Keyboard (panel open):** Space plays/pauses. Tab moves through Previous, Play/Pause, Next and the seek bar. On the seek bar, ←/→ skip 5 s and PgUp/PgDn skip 15 s.
- **Quit:** tray icon (it may be under the ^ overflow) → *Quit SideNotch*.

Tray menu: Hide/Show notch, Open player, Side (left/right), Media source (Automatic prefers Chrome, or pin a specific app), Start with Windows (off by default; adds an HKCU Run entry), Quit.

Settings: `%APPDATA%\SideNotch\settings.ini`. Errors log: `%APPDATA%\SideNotch\sidenotch.log`.

## Build

`build.cmd` compiles `SideNotch.cs` into `dist\SideNotch.exe` with the in-box `csc.exe`. No SDK is needed.

`diag\Diag.cs` is a console check of the media-session API. `Diag.exe` lists sessions; `Diag.exe toggle` toggles play/pause; `Diag.exe seek 60` seeks to 60 s.

## Limitations

- Controls depend on what the source exposes. Chrome exposes one session for the tab that last played media.
- Live streams or sources without a duration show `--:--`, and seeking is disabled.
- Elapsed time is extrapolated between Chrome's timeline updates, so it may drift by about a second until the next update.
- DPI handling is system-level; on mixed-DPI multi-monitor setups the notch may look slightly soft on the secondary display.
