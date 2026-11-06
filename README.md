# SpiderHeck CloudSave

Keeps your SpiderHeck save data, Silk settings and Silk mods in a cloud-synced folder (OneDrive, Google Drive or any folder you choose), so they follow you to your other PCs.

A mod for SpiderHeck by AeonS. Sister mods: [SpiderHeck Palette](https://github.com/AeonSaber/SpiderHeckPalette) and [SpiderHeck Jukebox](https://github.com/AeonSaber/SpiderHeckJukebox).

## Features

- **Three folders follow you.** The game's save folder, `Silk\Config` and `Silk\Mods` move into `<cloud folder>\SpiderHeck\`, and a directory link (junction) is left in each old place, so the game and Silk keep working as before.
- **Finds your sync folder.** OneDrive comes from Windows' own records. Google Drive comes from Google Drive for desktop's settings, so it works wherever you told the app to put your files. Or pick any folder.
- **Changes happen after the game closes.** Windows won't let folders that the game has open be moved, so a small helper waits for SpiderHeck to exit and then does the work.
- **Safe by design.**
  - Each change starts with a full backup of every folder it touches. The last 5 changes are kept in `%LOCALAPPDATA%\SpiderHeckCloudSave\Backups`.
  - Files are copied and checked by count and size before anything is removed.
  - Each folder is either changed completely or left as it was.
  - Cloud copies are never deleted, because your other PCs may be using them.
- **Turn off or switch at any time.** Turning it off copies everything back to this PC. Choosing another cloud folder moves everything there. Both keep the old cloud copy.
- **Second PC friendly.** If the cloud folder already has SpiderHeck data, the menu asks which copy to use, and the other one is kept as a backup.

## Requirements

- SpiderHeck on Windows 10 or 11 with the [Silk](https://github.com/SilkModding/Silk) mod loader
- Silk 0.7.0 or later
- A sync app (OneDrive, Google Drive for desktop, or similar) if you want the files to reach other PCs

## Installation

1. Install Silk 0.7.0 or later for SpiderHeck.
2. Download `SpiderHeckCloudSave.dll` from the [releases page](https://github.com/AeonSaber/SpiderHeckCloudSave/releases).
3. Put it in `SpiderHeck\Silk\Mods\` (inside the game folder, usually `C:\Program Files (x86)\Steam\steamapps\common\SpiderHeck`).
4. Start the game, open Silk's mod list, choose **SpiderHeckCloudSave**, and pick a cloud folder.
5. Quit the game. The helper runs for a few seconds after the game closes; wait for it before starting the game again.

On a second PC, install the mod the same way and pick the same cloud folder. Then choose **The cloud copy** when the menu asks which copy to use.

## Usage

Open Silk's mod list in the game and choose **SpiderHeckCloudSave**:

- **Status**: on, partly on, or off, plus where each of the three folders lives now.
- **Use OneDrive / Use Google Drive / Use a custom folder...**: turns cloud save on, or moves it to that folder if it is already on.
- **When the game closes**: shows the change waiting for the next exit, with **Cancel this change**.
- **Which copy should be used?**: shown when the chosen cloud folder already has SpiderHeck data. Pick the cloud copy (this PC's copy goes to the backup) or this PC's copy (the cloud copy is renamed to `<name>.replaced-<date>` and backed up).
- **Turn off cloud save**: copies the folders back to this PC when the game closes. The cloud copy is kept.
- **Finish moving the remaining folders**: shown if an earlier change only moved some folders.
- **Open cloud folder** and **Open backups folder**.
- **Last change**: the result of the helper's last run, with the reason and the log path if something failed.

Your sync app should keep the `SpiderHeck` folder available offline (OneDrive: "Always keep on this device"), so the game never waits for a download.

## Files

Everything CloudSave keeps for itself is in `%LOCALAPPDATA%\SpiderHeckCloudSave\`, on purpose outside the folders it moves, so settings stay per PC and never sync:

- `settings.txt`: the chosen cloud folder and any change waiting for the next exit.
- `Backups\<date-time>\`: full copies made before each change (the last 5 changes).
- `Runs\<date-time>.log`: what the helper did, step by step.
- `last-run.txt`: the summary the menu shows.

## Limitations

- Windows only (it uses directory junctions, PowerShell and robocopy, which all ship with Windows).
- If the game and the helper both try to use the folders (for example, you start the game again within seconds of quitting), the helper skips the remaining folders and the menu says so. Run the change again from the menu.
- Folders that contain links of their own are not moved; the helper reports which link stopped it.
- Two PCs playing at the same time on the same cloud folder can overwrite each other's save. Sync apps resolve that by keeping conflict copies, but play on one PC at a time.

## How it works

- A postfix on `GameSaveWrapper.OnApplicationQuit` (after the game's final save) writes a plan to `Runs\` and starts `cloudsave-helper.ps1`, which is embedded in the DLL, with Windows PowerShell.
- The helper waits for the game's process to exit, then for each folder: backs it up with robocopy, checks the copy, copies it to its new place, checks again, removes the original, and creates the junction. On any failure it puts that folder back the way it was.
- Google Drive for desktop's chosen folder is read from `%LOCALAPPDATA%\Google\DriveFS\root_preference_sqlite.db` (table `roots`, the row with `is_my_drive = 1`), using Windows' own `winsqlite3.dll`. Stream mode falls back to the drive labelled "Google Drive".
- A postfix on `Announcer.ModsPopup` builds the settings page in Silk's mod menu.

## Building from source

```
dotnet build -c Release
```

Requires the .NET SDK. Target framework: `net472`. Output: `bin\Release\net472\SpiderHeckCloudSave.dll`.

The project references DLLs in a `lib\` folder, which is not in the repository (the game's and Silk's files are not redistributed). Copy them from your own install:

- From `SpiderHeck\Silk\Library\`: every DLL listed in `CloudSave.csproj` (Silk.dll, 0Harmony.dll, YamlDotNet.dll, NewAssembly.dll, MyScriptAssembly.dll, UnityEngine.CoreModule.dll, UnityEngine.UI.dll, Unity.TextMeshPro.dll, and the rest)
- From `SpiderHeck\SpiderHeckApp_Data\Managed\`: any listed DLL that `Silk\Library` does not have (such as UnityEngine.AudioModule.dll and the UnityWebRequest modules)

The helper script can be tested on its own, without the game: it takes a JSON plan (`-PlanPath`) with the folders to move.

## Credits

Built on [Silk](https://github.com/SilkModding/Silk) and [Harmony](https://github.com/pardeike/Harmony).

## License

[MIT](LICENSE)
