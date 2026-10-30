<h1 align="center">pc-migrate</h1>
<p align="center">Move files and settings from an old Windows PC to a new one, over the network or with an external drive.</p>

<h2 align="center">Download</h2>

<table align="center">
  <tr>
    <td align="center" width="280">
      <a href="https://github.com/tylorfaber-arch/pc-migrate/releases/latest/download/pcmigrate.exe"><b>⬇&nbsp; pcmigrate.exe</b></a><br>
      <sub>Single file &middot; no install &middot; USB-friendly</sub>
    </td>
  </tr>
</table>

<p align="center">
  <sub><b>Requirements:</b> Windows 10 or 11, 64-bit &nbsp;&middot;&nbsp; Run as administrator &nbsp;&middot;&nbsp; Nothing else to install</sub><br>
  <sub>Windows may show a SmartScreen warning because the app isn't code-signed yet &mdash; click <b>More info &rarr; Run anyway</b>.</sub><br>
  <sub><a href="https://github.com/tylorfaber-arch/pc-migrate/releases">All releases</a></sub>
</p>

<br>

---

> Status: step 1 of the roadmap (files and settings). Moving installed programs is next.

## Using it

Run `pcmigrate.exe` from an **administrator** Command Prompt or PowerShell on both PCs.

**With an external drive**

```
old PC:  pcmigrate capture --to E:\migration
new PC:  pcmigrate apply --from E:\migration
```

**Over the network** (both PCs on the same network)

```
new PC:  pcmigrate receive
         (prints its IP address and a 6-digit pairing code)
old PC:  pcmigrate send --host 192.168.1.50 --code 123456
```

The new PC applies everything as soon as the transfer finishes.

**Look before you copy:** `pcmigrate scan` lists every user, how much would be copied, and the
installed programs. `pcmigrate apply --from ... --dry-run` shows what apply would do without
changing anything. Run `pcmigrate help` for all options.

If the user account has a different name on the new PC, use `--map olduser=newuser`.

## What moves

| Moves | Notes |
|---|---|
| Desktop, Documents, Downloads, Pictures, Music, Videos, Favorites, Contacts, Links, Saved Games | For every user, or only `--user` |
| Chrome, Edge, Brave, Firefox and Opera profiles | Bookmarks, history, extensions, settings. Chrome/Edge **saved passwords do not move**: Windows locks them to the old PC. Export them from the browser first. |
| Outlook signatures, Office templates, Start menu shortcuts, pinned taskbar items, themes | |
| Wallpaper | |
| Mapped network drives | Reconnect at next sign-in |
| Saved Wi-Fi networks, with passwords | |
| List of installed programs | The apply report lists the programs still to install on the new PC |

Caches and temporary files are skipped. OneDrive "online-only" files are skipped since OneDrive
will download them again on the new PC.

## How it works

- **Scanning.** Users come from the registry list of profiles (`ProfileList`). Programs come from
  the same registry keys "Apps & features" reads (`Uninstall`), for all-users and per-user
  installs, 64-bit and 32-bit.
- **Open files.** Before copying, pc-migrate takes a *Volume Shadow Copy*: a frozen snapshot of
  the drive that Windows makes in a second or two. Files that are open and locked (Outlook,
  browsers) are read from the snapshot. It is deleted when the capture ends.
- **Users who are not signed in.** Their settings live in `NTUSER.DAT` in their profile. pc-migrate
  loads that file into the registry temporarily, reads or writes it, and unloads it.
- **The store.** A capture is a folder: the files under `files/users/<name>/...`, settings under
  `settings/`, and a `manifest.json` written last with a SHA-256 checksum for every file. A
  folder without `manifest.json` is an unfinished capture.
- **Network transfer.** The new PC listens on TCP port 47800 (opened in Windows Firewall only while
  it waits). The old PC streams each file in 1 MB chunks with its checksum, and the new PC checks
  every checksum and rejects any path that would land outside the store. The pairing code stops
  other machines on the network from sending data.
- **Applying.** Files are written to a temporary name, checked against the manifest checksum, then
  renamed into place. If a file already exists, the newer one wins (`--conflicts` changes this).
  Everything is listed in an `apply-report-*.txt` saved next to the store.

## Security notes

- The store and the network transfer are **not encrypted** yet. The store contains Wi-Fi passwords
  and browser data, so keep the drive safe and wipe it afterwards, and only use network mode on a
  network you trust.
- `pcmigrate.exe` is not code-signed yet, so Windows SmartScreen will warn the first time it runs
  ("More info" > "Run anyway").

## Building

Needs the .NET 8 SDK. It builds on Windows, macOS or Linux.

```
dotnet test
dotnet publish src/PcMigrate.Cli -c Release -o artifacts   # -> artifacts/pcmigrate.exe
```

GitHub Actions tests and builds `pcmigrate.exe` on every pull request (under the run's
**Artifacts**). Every push to `main` also publishes a GitHub release with the exe, which is what the
Download link at the top serves.

## Layout

```
src/PcMigrate.Core      the engine: scanning, capture, apply, network transfer
  Capture/              what to copy and how to walk folders
  Apply/                restoring onto the new PC and the report
  Transfer/             the network protocol
  Storage/              the store format and checksums
  Windows/              registry, shadow copies, Wi-Fi, wallpaper, mapped drives
src/PcMigrate.Cli       pcmigrate.exe
tests/PcMigrate.Tests   tests, including full capture, send, receive and apply runs
```

## Roadmap

1. Files and settings ← here
2. Move installed programs (files, registry, services, shortcuts) with a "will it move?" rating, and a winget reinstall fallback
3. Step-by-step GUI wizard and a code-signed .exe
4. Licensing, installer and a place to sell it
