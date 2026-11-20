NSLTVR
VR mod for New Super Lucky's Tale (Steam).
Native stereo, both eyes drawn from the same game moment, using the game's own VR camera from Lucky's Tale VR: each level is a toy world around you, and Lucky runs through it in front of you. Played seated with a gamepad.

You need
- The game on Steam.
- A PC VR headset with an OpenXR runtime: SteamVR set as your OpenXR runtime in SteamVR settings, or Virtual Desktop's VDXR. The installer warns you if none is set.
- A gamepad. An Xbox pad is what it was made with.
- PlayStation DualSense: turn on Steam Input for the game (Steam, the game's Properties, Controller), so the game sees it as an Xbox pad. The mod does not read a DualSense on its own.

Install
1. Unzip this folder anywhere.
2. Double click "Install NSLTVR.bat" and type 1.

Play
1. Start SteamVR or connect Virtual Desktop.
2. Start New Super Lucky's Tale from Steam. VR starts by itself.
3. The title screen and loading screens show on a big screen in front of you. Levels are all around you. Play as usual.

Settings
Hold both sticks down for half a second to open the menu, or press Insert on a keyboard. The game gets no input while it is open.
LB and RB change tab. Up and down choose a setting. Left and right change it. A selects. B closes.
On a keyboard: arrow keys move and change, Enter selects, Esc closes, Page Up and Page Down change tab.
Each setting has a short explanation at the bottom of the menu.

General tab
- Recenter: puts the view, the HUD and the menu straight ahead of where you face. Your headset's own recenter does the same.
- Resolution: how sharp the game is drawn, as a share of what your headset asks for. Takes effect the next time you start the game.
- Picture: how the HUD, the menu and the title screens reach the headset. Auto uses Steady on Virtual Desktop and Panel on everything else. Steady holds still when you turn your head. Panel can look a little sharper.
- Click sounds: how loud the menu clicks are, down to Off.
- Load VR baseline: puts the game's own display settings back to what this mod starts with (VSync off, no frame cap, a window, ambient occlusion, bloom and god rays off). Your quality preset stays. Press A twice.
- Load best VR performance: the game's Low preset with your texture quality kept, anti-aliasing off, simple shadows and low effects, plus everything in Load VR baseline. Press A twice, then restart the game: the speed gain only shows after a restart.
- Close.

HUD tab
- HUD: Fixed (hearts, coins and clovers stay in one place in the room), Follows head, or Hidden.
- Size, Left / right, Height, Distance: where the HUD panel sits.
- Reset HUD.

Mod Menu tab
- Size, Left / right, Height, Distance: where this menu sits.
- Reset menu.

Uninstall
Double click "Install NSLTVR.bat" and type 2. No game files are changed by this mod.

If something goes wrong
- The game stays flat: a "VR DID NOT START" message at the top of the screen says why. Press Insert to see it again in the mod menu. Check your OpenXR runtime is set (SteamVR or VDXR). Then look in BepInEx\LogOutput.log in the game folder.
- Windows warns about the bat file: this mod is not signed, so Windows may ask you to confirm. Read the bat file first if you like. It is plain text.
- Smart App Control: if it is on, Windows can block unsigned mod DLLs and the game runs flat. Windows does not make this easy to turn back on, so decide for yourself. There is no workaround in the mod.
- When SteamVR starts, its dashboard may appear in front of you. Close it as usual.
- Lucky drifts or stops while you hold the stick: another controller-like device may be connected. The mod already prefers an Xbox pad; unplug other game controllers if it still happens, and send the log.
- The frame rate drops: in SteamVR, check per-app throttling and motion smoothing are off for this game first. Then try Load best VR performance, then a lower Resolution.

Tested on
Pimax Dream Air through SteamVR at 72 Hz, and Meta Quest 3 through Virtual Desktop's VDXR at 90 Hz, both with an RTX 5090. Others should work through OpenXR but have not been tried. AMD and Intel graphics have not been tried.

Known limits
- Steam version only.
- No motion controls. A gamepad is required.
- The camera follows the game's own VR rig from Lucky's Tale VR, so the right stick does not turn the camera.
- No DLSS or other upscaler. The game is light and does not need one.
- While VR runs, the game window on your monitor is 1920x1080.

Notes
- The game keeps its own 60 updates a second, so it runs at exactly its normal speed at any headset rate.
- The mod turns off bloom and god rays, which look different in each eye.
- Settings are saved in BepInEx\config\com.scubasteve2365.nsltvr.cfg in the game folder.

Version 1.0. Credits and licences are in THIRD-PARTY-NOTICES.txt. NSLTVR is MIT licensed, by scubasteve2365.
This is a fan mod, not made or approved by Playful Studios.
