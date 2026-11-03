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

**Drive images.** Instead of a second drive, a whole drive can be saved as one image file and put
back later onto the same drive or a new one (it starts up on its own, and grows to fill a bigger
drive). In the app: **Clone or image a whole drive → Drive to image file** and **Image file to drive**.
On the command line: `pcmigrate-cli image --from 0 --to E:\images` and
`pcmigrate-cli restore-image --from "E:\images\PC disk 0 2026-11-03.vhdx" --to 2`.
Only the space in use is saved, so a 1 TB drive holding 200 GB makes a file of about 200 GB.
The image is a normal Windows virtual disk (.vhdx): double-click it in File Explorer and it opens
like a USB drive, so single files can be copied out of it without restoring anything.

**Scheduled backups.** **Tools → Scheduled backup** (or `pcmigrate-cli schedule --to E:\Backups
--every daily --at 21:00`) keeps a backup of the PC up to date by itself, daily or weekly, even when
nobody is signed in. Each run copies only new and changed files and removes files that were deleted
from the PC, so after the first one it's quick. If the PC was off at backup time it catches up when
it's next on; if the backup drive isn't plugged in, it tries again next time. The screen shows the
last result. The backup is an ordinary pc-migrate backup, so it loads onto a new PC the usual way.
`pcmigrate-cli backup --to <folder>` does the same update once, by hand.

**Rescue USB.** For a PC whose Windows won't start at all: **Tools → Rescue USB** (or
`pcmigrate-cli rescue-usb --disk <n>`) turns a USB stick into one that starts any PC straight into
pc-migrate's menu. Choose 6 there to save the PC's files to the stick or another USB drive, no
screwdriver needed. The stick is built from the PC's own Windows recovery environment, so nothing
is downloaded from Microsoft and Secure Boot doesn't block it. Sticks of 8 GB or more get a second
part (PCM-DATA) to save backups on. Start a PC from it with its boot menu key (F12 on most, F9 on HP).

`pcmigrate-cli keys` shows the license keys on this PC (or `--from-drive E:` for a dead PC's drive);
the app has the same under **Tools → License keys**. A key built into the PC's firmware by its maker
is shown too, but it only activates Windows on that PC.

**Drive health.** **Tools → Drive health** (or `pcmigrate-cli health`) shows what each drive reports
about itself: Windows' verdict and the SMART counters (predicted failure, uncorrectable read errors,
SSD wear, temperature, hours powered on). A pulled drive is checked as soon as it's picked, and a
failing one gets a red banner: start the copy straight away and don't run disk repairs on it first.
The source of a clone is checked too. Many USB adapters don't pass health through; those show as unknown.

`pcmigrate-cli scan` lists users, sizes and programs. `--dry-run` on apply changes nothing. Use
`--map olduser=newuser` when account names differ. `pcmigrate-cli help` lists everything.

## What moves

| Moves | Notes |
|---|---|
| Each person's whole user folder: Desktop, Documents, Downloads, Pictures, Music, Videos, OneDrive, Dropbox and any other folder in it | For every user, or only `--user`. AppData (program data) is left out except the parts below, and so are developer caches like `.nuget`. |
| Desktop, Documents and the rest when they were moved elsewhere | Followed to another folder or drive. On a network share they're left where they are, with a warning. |
| Outlook data files (.pst) and Sticky Notes | Outlook's .ost caches are left out; they rebuild from the mail server. |
| Chrome, Edge, Brave, Firefox and Opera profiles | Bookmarks, history, extensions, settings. Chrome/Edge **saved passwords do not move**: Windows locks them to the old PC. Export them from the browser first. |
| Outlook signatures, Office templates, Start menu shortcuts, pinned taskbar items, themes | |
| Wallpaper | |
| Mapped network drives | Reconnect at next sign-in |
| License keys | Windows, older Office (2007 to 2016 bought with a key) and programs that store a key or serial number in the registry. Saved as `license-keys.txt` at the top of the backup and listed in the new PC's report. Account-based licences (Microsoft 365, Creative Cloud) have no key to find. |
| Saved Wi-Fi networks, with passwords | From a pulled drive the networks move but not their passwords (Windows encrypts them with the old PC's own key), so each asks once. |
| **Programs** | See *How programs move* below |

Caches and temporary files are skipped. OneDrive "online-only" files are skipped (they're in the
cloud, not on the drive) and counted in one warning; they come back when the person signs in to OneDrive.
Folders outside everyone's user folder, such as `C:\QuickBooks` or `C:\Users\Public\Documents`, aren't
copied, but any over 1 MB are named in a warning so nothing is left behind without you knowing. Windows'
own setup accounts (`defaultuser0`, `defaultuser100000`) are ignored.

## How programs move

Every program in *Apps & features* gets one of four badges:

| Badge | What happens | Which programs |
|---|---|---|
| 🟢 **Reinstall + settings** | The new PC installs a fresh copy with **winget** (Windows' own app installer), then the program's settings folders (AppData) and registry settings from the old PC are copied in | Anything winget knows: Chrome, Spotify, Discord, Steam, VS Code, VLC, Zoom, Notepad++ and thousands more |
| 🟡 **Copy as is** | The program folder, its registry entries, its Start menu and desktop shortcuts and its settings are copied | Programs winget doesn't have that were installed by simply unpacking files |
| 🟡 **Saved installer** | The program's original installer was found on the old PC. It's saved with the backup and run on the new PC, then the settings are copied in. An .msi installs with just a progress bar; other installers open normally so you can click through them | Programs winget doesn't have whose installer is still on the old PC: the copy Windows keeps in *Package Cache*, the folder an .msi was installed from, or a matching file in someone's Downloads folder |
| 🔴 **Do by hand** | Listed on the finish screen with the reason | Drivers, antivirus, Office/Microsoft 365, Steam games (reinstall from your library; saves come with your files), and programs winget doesn't have whose installer couldn't be found (.msi installs, or anything that runs a Windows service) |

**Shared runtimes.** Visual C++ redistributables, .NET and Java aren't listed as programs, but older
programs won't start without them. pc-migrate notes every one the old PC had and, before installing
any programs, puts the missing ones on the new PC with winget. While saving, it also downloads a copy
of each runtime's installer into the backup, so a new PC with no internet still gets them.
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
- **Drive images.** An empty, growable .vhdx the size of the drive is created and attached with
  `diskpart`, and the drive is cloned into it exactly as above, so only used clusters are written
  and the file stays about the size of the data. Restoring attaches the image read-only and clones
  it onto the chosen drive. A half-written image is deleted if anything goes wrong.
- **Scheduled backups.** A Task Scheduler entry runs `pcmigrate-cli backup --scheduled` as SYSTEM
  from a copy kept in `C:\ProgramData\pc-migrate`. Settings and the last result are in
  `schedule.json` there, readable only by SYSTEM and administrators; a backup password is stored
  locked to the PC with Windows DPAPI. Each run reads the last manifest and skips any file with the
  same size and modified time, then writes the new manifest in one step and deletes stored files it
  no longer lists.
- **Rescue USB.** The stick gets a 2 GB FAT32 boot part and an NTFS part for backups. A copy of
  the PC's Windows recovery image (winre.wim, from its recovery partition or System32\Recovery) is
  opened with DISM, and a `winpeshl.ini` is added so it opens pc-migrate instead of the recovery
  menu. Windows' signed boot manager, `boot.sdi` and a boot menu (made with `bcdedit`) that starts
  the image from a RAM disk go on the boot part, the same way Windows' own install USBs start.
  The command-line tool runs there (the app needs WebView2, which the rescue system doesn't have).
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

- **Password-protected backups.** Tick *Protect it with a password* when saving to a drive (or use
  `capture --encrypt`). Every file, the manifest and the license key list are encrypted with
  AES-256-GCM: each file gets its own key derived from the backup key, the backup key comes from the
  password through PBKDF2-SHA256 (600,000 rounds), and each 1 MB chunk is sealed so a changed,
  swapped or cut-short file is refused. The password isn't stored anywhere and can't be recovered.
  File and folder names stay readable. On the new PC the backup is unlocked into
  `C:\ProgramData\pc-migrate`, moved into place, and the unlocked copy deleted; the drive is left as it was.
- Without a password the store is **not encrypted**. It contains Wi-Fi passwords, license keys and
  browser data, so keep the drive safe and wipe it afterwards. The network transfer isn't encrypted
  either, so only use network mode on a network you trust.
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
6. License key finder ← done
7. Drive health check ← done
8. Password-protected backups ← done
9. Keep program installers, and install runtimes (VC++, .NET, Java) automatically ← done
10. Disk images (.vhdx), save and restore ← done
11. Rescue USB ← done
12. Scheduled backups ← done
13. Technician mode
14. Licensing, installer and a place to sell it
