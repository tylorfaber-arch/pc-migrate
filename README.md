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

> Status: moves files, settings and programs, with a step-by-step app. Code signing and selling are next.

## Using it

Download `pc-migrate.exe` onto both PCs and double-click it (it asks for admin rights).

1. On the **new** PC choose *This is my new PC* → *Over the network*. It shows a 6-digit code.
2. On the **old** PC choose *This is my old PC* → *Over the network*. The new PC appears in the
   list on its own; pick it and type the code.
3. Choose who to move, press **Start moving**, and when it finishes, confirm on the new PC whose
   files go into which account.

No network between them? Choose *With an external drive* on the old PC, then *From an external
drive* on the new one.

**Old PC won't start?** Pull its drive, plug it into the new PC with a USB adapter or dock, and
choose *The old PC is dead, but I have its drive*. Pick the drive, then *Onto this PC*. Files,
settings and programs come off it the same way, and the drive is only read. A BitLocker drive asks
for its 48-digit recovery key (at aka.ms/myrecoverykey for the Microsoft account the old PC used).
The drive can also be sent to another PC over the network or saved to a second external drive.

**Clone a whole drive.** *Clone a whole drive* makes a start-up-ready copy of one drive onto another:
this PC's drive onto a new SSD in a USB enclosure, or one USB drive onto another. Every partition
comes across (boot, Windows, recovery); only used space is copied, and if the new drive is bigger the
main partition grows to fill it. If the new drive is smaller but the files fit, it offers to shrink
the old drive's main partition first (free space only). The drive being written is erased, so you
type ERASE to start. The drive Windows is running from can be copied but never written to.

**Updates.** When it starts, pc-migrate checks GitHub for a newer version and offers it in a bar at the
top: **Update now** downloads it, checks it against GitHub's checksum, swaps it in and restarts.
The command-line tool does the same with `pcmigrate-cli update`. Nothing is checked without internet.

When a job finishes or stops, Windows shows a notification and the taskbar button flashes, so you
can walk away from a long copy. Clicking the notification opens the log or report.

### Command line

`pcmigrate-cli.exe` does the same from an administrator terminal, for scripts and remote work:

```
old PC:  pcmigrate-cli capture --to E:\migration         new PC:  pcmigrate-cli apply --from E:\migration
new PC:  pcmigrate-cli receive                            old PC:  pcmigrate-cli send --host 192.168.1.50 --code 123456
```

From a dead PC's drive: `pcmigrate-cli drives` lists plugged-in drives with Windows on them, then
`pcmigrate-cli capture --from-drive E: --to F:\migration` (or `send --from-drive E: ...`). Add
`--recovery-key <key>` for a BitLocker drive.

Clone a drive: `pcmigrate-cli disks` lists the drives by number, then
`pcmigrate-cli clone --from 0 --to 2`. Add `--shrink-source` to let it shrink the source first when needed.

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
| Saved Wi-Fi networks, with passwords | From a pulled drive the networks move but not their passwords (Windows encrypts them with the old PC's own key), so each asks once. |
| **Programs** | See *How programs move* below |

Caches and temporary files are skipped. OneDrive "online-only" files are skipped since OneDrive
will download them again on the new PC.

## How programs move

Every program in *Apps & features* gets one of three badges:

| Badge | What happens | Which programs |
|---|---|---|
| 🟢 **Reinstall + settings** | The new PC installs a fresh copy with **winget** (Windows' own app installer), then the program's settings folders (AppData) and registry settings from the old PC are copied in | Anything winget knows: Chrome, Spotify, Discord, Steam, VS Code, VLC, Zoom, Notepad++ and thousands more |
| 🟡 **Copy as is** | The program folder, its registry entries, its Start menu and desktop shortcuts and its settings are copied | Programs winget doesn't have that were installed by simply unpacking files |
| 🔴 **Do by hand** | Listed on the finish screen with the reason | Drivers, antivirus, Office/Microsoft 365, Steam games (reinstall from your library; saves come with your files), Windows Installer (.msi) programs winget doesn't have, and anything that runs a Windows service |

Runtimes and parts of Windows (Visual C++ redistributables, .NET, Edge, WebView2) are left out of the list.
A program that's already installed on the new PC just gets its settings.

Known limits: a few installers (Spotify is one) refuse to run from an administrator program, so
winget can't install them from pc-migrate. They're reported, with a note to install them normally.
Licence keys and sign-ins don't move.

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
- **Programs.** `winget list` matches installed programs to winget's catalogue. Settings are found by
  name: folders in `AppData\Roaming`, `AppData\Local` and `AppData\LocalLow`, and keys under
  `HKEY_CURRENT_USER\Software`, called after the program (or *Maker\Program*). Registry settings are
  exported with `reg export` and, on the new PC, rewritten for the right account before `reg import`.
- **A drive from a dead PC.** Its registry files (`Windows\System32\config\SOFTWARE` and `SYSTEM`,
  and each person's `NTUSER.DAT`) are copied off the drive and the copies loaded, so the drive is
  never written to. Everything is recorded with the old PC's own paths (`C:\Users\sam`) and only
  translated to where the drive is plugged in (`E:\Users\sam`) when a file is read. Folders that
  only the old PC's accounts could open are read with Windows' backup right, the way backup
  programs do, without changing their permissions. winget can't see the old PC's programs, so each
  one is looked up by name instead. *Onto this PC* reads the drive into a holding folder in
  `C:\ProgramData\pc-migrate` and then moves (not copies) the files into place.
- **Drive clone.** The source's partition table is read straight from the disk and a new one is
  planned for the target: partitions before the main Windows partition keep their places, ones after
  it (usually recovery) move to the end, and the main one takes the space between. The target gets
  fresh disk and partition IDs so both drives can be plugged in together. Lettered NTFS partitions are
  copied from a shadow copy, and only the clusters NTFS marks as in use are read (plus its backup boot
  sector); other partitions are copied whole. Afterwards the NTFS partition is extended, the clone's
  saved drive letters are cleared so it takes C: on its own, and `bcdboot` rewrites its boot files.
  The disks are checked again (size, model, serial) right before anything is erased.
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
  Apps/                 rating programs, finding their settings, winget
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

1. Files and settings ← done
2. Move installed programs ← done (winget reinstall + settings, or copy as is)
3. Step-by-step app window ← done early; code signing still to do
4. Rescue a dead PC's drive over USB ← done
5. Full disk clone, drive to drive (including external to external) ← done, needs testing on real drives
6. Keep or rebuild program installers, and install prerequisites (VC++, .NET, Java) automatically
7. Licensing, installer and a place to sell it
