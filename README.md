<p align="center"><img src="src/PcMigrate.App/Assets/app-256.png" width="96" alt="pc-migrate icon"></p>
<h1 align="center">pc-migrate</h1>
<p align="center">Move files and settings from an old Windows PC to a new one, over the network or with an external drive.</p>

<h2 align="center">Download</h2>

<table align="center">
  <tr>
    <td align="center" width="280">
      <a href="https://github.com/tylorfaber-arch/pc-migrate/releases/latest/download/pc-migrate.exe"><b>⬇&nbsp; pc-migrate</b></a><br>
      <sub>pc-migrate.exe<br>The app &middot; single file &middot; no install</sub>
    </td>
    <td align="center" width="280">
      <a href="https://github.com/tylorfaber-arch/pc-migrate/releases/latest/download/pcmigrate-cli.exe"><b>⬇&nbsp; Command line</b></a><br>
      <sub>pcmigrate-cli.exe<br>For scripts and technicians</sub>
    </td>
  </tr>
</table>

<p align="center">
  <sub><b>Requirements:</b> Windows 10 or 11, 64-bit, with the WebView2 runtime (built into Windows 11 and up-to-date Windows 10) &nbsp;&middot;&nbsp; Run as administrator &nbsp;&middot;&nbsp; Nothing else to install</sub><br>
  <sub>Windows may show a SmartScreen warning because the app isn't code-signed yet &mdash; click <b>More info &rarr; Run anyway</b>.</sub><br>
  <sub><a href="https://github.com/tylorfaber-arch/pc-migrate/releases">All releases</a></sub>
</p>

<br>

---

> Status: files and settings move today, with a step-by-step app. Moving installed programs is next.

## Using it

Download `pc-migrate.exe` onto both PCs and double-click it (it asks for admin rights).

1. On the **new** PC choose *This is my new PC* → *Over the network*. It shows a 6-digit code.
2. On the **old** PC choose *This is my old PC* → *Over the network*. The new PC appears in the
   list on its own; pick it and type the code.
3. Choose who to move, press **Start moving**, and when it finishes, confirm on the new PC whose
   files go into which account.

No network between them? Choose *With an external drive* on the old PC, then *From an external
drive* on the new one.

### Command line

`pcmigrate-cli.exe` does the same from an administrator terminal, for scripts and remote work:

```
old PC:  pcmigrate-cli capture --to E:\migration         new PC:  pcmigrate-cli apply --from E:\migration
new PC:  pcmigrate-cli receive                            old PC:  pcmigrate-cli send --host 192.168.1.50 --code 123456
```

`pcmigrate-cli scan` lists users, sizes and programs. `--dry-run` on apply changes nothing. Use
`--map olduser=newuser` when account names differ. `pcmigrate-cli help` lists everything.

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
- **Finding the new PC.** The old PC broadcasts a short question on UDP port 47801 and every PC
  waiting to receive answers with its name, so nobody has to look up an IP address.
- **The app window.** The screens are a web page (`src/PcMigrate.App/ui/index.html`) shown with
  WebView2, the Edge engine built into Windows. The page asks the C# side to do the work and gets
  progress back as messages. Open that file in a browser to preview the screens with pretend data.
- **Network transfer.** The new PC listens on TCP port 47800 (opened in Windows Firewall, with the
  discovery port, only while it waits). The old PC streams each file in 1 MB chunks with its checksum, and the new PC checks
  every checksum and rejects any path that would land outside the store. The pairing code stops
  other machines on the network from sending data.
- **Applying.** Files are written to a temporary name, checked against the manifest checksum, then
  renamed into place. If a file already exists, the newer one wins (`--conflicts` changes this).
  Everything is listed in an `apply-report-*.txt` saved next to the store.

## Security notes

- The store and the network transfer are **not encrypted** yet. The store contains Wi-Fi passwords
  and browser data, so keep the drive safe and wipe it afterwards, and only use network mode on a
  network you trust.
- The exes are not code-signed yet, so Windows SmartScreen will warn the first time it runs
  ("More info" > "Run anyway").

## Building

Needs the .NET 8 SDK. The engine, tests and command line build anywhere; the app window needs Windows
(or GitHub Actions) to build.

```
dotnet test
dotnet publish src/PcMigrate.App -c Release -o artifacts   # -> artifacts/pc-migrate.exe (build on Windows)
dotnet publish src/PcMigrate.Cli -c Release -o artifacts   # -> artifacts/pcmigrate-cli.exe
```

GitHub Actions tests and builds both exes on every pull request (under the run's
**Artifacts**). Every push to `main` also publishes a GitHub release with the exe, which is what the
Download links at the top serve.

## Layout

```
src/PcMigrate.Core      the engine: scanning, capture, apply, network transfer
  Capture/              what to copy and how to walk folders
  Apply/                restoring onto the new PC and the report
  Transfer/             the network protocol
  Storage/              the store format and checksums
  Windows/              registry, shadow copies, Wi-Fi, wallpaper, mapped drives
src/PcMigrate.App       pc-migrate.exe, the app window
  ui/index.html         every screen (works in a browser with pretend data for designing)
src/PcMigrate.Cli       pcmigrate-cli.exe
tests/PcMigrate.Tests   tests, including full capture, send, receive and apply runs
```

## Roadmap

1. Files and settings ← here
2. Move installed programs (files, registry, services, shortcuts) with a "will it move?" rating, and a winget reinstall fallback
3. Step-by-step app window ← done early; code signing still to do
4. Licensing, installer and a place to sell it
