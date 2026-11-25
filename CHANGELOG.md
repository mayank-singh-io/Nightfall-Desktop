# Changelog

## v1.5.0 (November 24, 2026)

### New: Play
A new **Play** tab (in the Moon Clock settings and in the new tab's settings) with a switch for everything below.

- **Hover** (while holding Ctrl, or always on): fireflies drift toward you, stars sparkle as you pass, the cat notices you, ripples follow you on water, grass parts and snow swirls around you, birds scatter, and lamps glow brighter
- **Clicks:** hold the sky for a meteor shower; click the moon for its phase and tonight's sky; catch a shooting star for a little wish; send the train; the cat and steaming mugs react; drag across stars to draw your own constellation; double-click the sky for a minute of rain (or snow)
- **Rare surprises:** now and then, a comet, an owl, a fox or a whale
- The cat now has a living tail that swishes
- **Sparkling trails** behind every shooting star: glittering dust, a glowing ribbon or sparkler sparks, in a mix of colours, lasting about 1, 3 or 6 seconds (Play tab)

### New: special days, more screens, pictures, music
- **Special days:** diyas floating on the water and lanterns rising on Diwali (three days), drifting clouds of colour on Holi (two days), soft fireworks on New Year's Eve (blooms, golden willows and far-off bursts), and lanterns with a heart of stars on your birthday. Each can be switched off, and your birthday is set in the Play tab
- **A different scene on each screen** when you have more than one monitor (or the same everywhere, in Settings → Wallpaper)
- **Save the scene as a 4K picture:** from the tray menu, or the new tab's Scene settings
- **Music glow:** while music plays, a warm glow rises and lights, fireflies and the aurora brighten a little
- Water is now marked in Firefly Lake, Northern Lights and Ocean Shore, so ripples and diyas work there too
- **A website** (index.html) that shows the scenes live, for GitHub Pages

### New: a real sky
- **Real weather in the scenes:** rain, storms (with soft, silent lightning), snow, mist and drifting clouds follow the weather in your saved city, using the forecast the clock already downloads. Always-rainy and snowy scenes keep their own weather
- **Tonight's real moon:** every scene's moon shows the actual phase, and the dark side melts into the sky
- **Lighter weather checks:** at most once an hour, only while a scene is showing, and never on a metered connection or in battery saver (the new tab skips when data saver is on or the battery is at 20% or less)
- **Gentle start:** when Windows starts, the night sky fades in, stars appearing one by one, then the desktop appears (a few seconds; can be switched off in Settings → Clock)

### Changed
- Northern Lights: the aurora visibly reshapes every few seconds and gently pulses

## v1.4.1 (November 24, 2026)

### Changed
- Northern Lights: the aurora now visibly reshapes every few seconds and gently pulses brighter and dimmer, instead of changing too slowly to notice

## v1.4.0 (November 24, 2026)

### New
- Four nature scenes: Porch in the Forest, Moonlit Meadow (grass swaying in the wind), Wide Falls, and Mountain Lake at Dawn, for 18 in total
- Dawn versions of every scene that follow the real sunrise (and optionally sunset) where you are. Settings → Scenes → Time of day: follow the sunrise, sunrise and sunset, always night, or always dawn
- Galaxy Peaks and City Below now have real motion: drifting clouds, satellites, a distant aircraft, and more shooting stars

### Changed
- Rooftop Rain is now set on a wooden deck with twinkling string lights
- Snowy Village is redrawn with proper houses, snowy roofs, icicles, doors with lamps, fences, and a church at the end of the street
- Earth from Space is now realistic: continents, coastal city lights, moonlit clouds and a thin atmosphere, slowly turning
- Every glowing light (fireflies, lamps, the boat, the beacon, city lights) is now a soft glow instead of a flat disc
- If sunrise passes while the wallpaper is hidden behind windows (or the new tab is in the background), the colours fade to dawn over a few seconds when you come back, instead of jumping
- Ocean waves are a soft sheet of water with broken foam, instead of hard lines
- Rain over the City has a soft, blurred reflection instead of streaks

## v1.3.1 (November 23, 2026)

### Changed
- Northern Lights: the aurora now slowly changes shape and brightness in place, instead of sliding sideways (and the seam where it wrapped around is gone)
- Water in Northern Lights and Firefly Lake: soft glints that fade in and out, instead of hard lines
- Moving the mouse no longer shifts the scene unless you turn it on in Settings

## v1.3.0 (November 23, 2026)

### Changed
- Settings are now organized into tabs: Clock, Away mode, Scenes, Wallpaper, and Weather (Moon Clock), and Sites, Scene, Sky, Weather, and More (new tab page)
- Scenes are chosen from a grid of previews instead of a dropdown
- The tray menu is shorter: show the night sky, live wallpaper, lock position, Settings, move back, and quit. Clock and taskbar options moved to Settings → Clock
- Double-click the moon in the tray to open Settings
- Puddles on Rooftop Rain ripple when clicked

### Fixed
- The live wallpaper no longer flashes on top of your windows when it starts
- Saving settings no longer restarts the wallpaper unless the scene itself changed
- With several monitors, the Cozy Room shows the same window view on each
- Clicking a desktop icon no longer sets off a scene interaction
- The live wallpaper uses about 8 MB less memory per monitor
- New tab: the settings panel couldn't be clicked while a scene was showing
- New tab: switching scenes could briefly show an error
- New tab: scenes respect your system's "reduce motion" setting
- New tab: "Match the weather outside" for the Cozy Room waits for the real weather
- A frame that can't be drawn is now skipped instead of stopping the sky or wallpaper

## v1.2.0 (November 23, 2026)

### New
- Four more live scenes, for 14 in total: Steam Train, Snowy Village, Cozy Room, and Earth from Space
- Cozy Room has four window views (Rainy City, Mountain Lake, Snowy Forest, Beach and Sea): pick one, get a different one each time, or match the weather outside
- Choose how often the Steam Train passes: every few minutes, every 10 minutes, or one after another
- Interactive scenes on the live wallpaper and the new tab page: click the sky for a shooting star, click windows and lamps to switch them, click water for ripples, and move the mouse for a gentle parallax. Each can be switched on or off.

### Fixed
- A rare crash when a shooting star finished fading out
- Scenes whose skyline reached the bottom of the screen could fail to draw

## v1.1.0 (November 23, 2026)

### New
- Six new live scenes: Snowy Cabin, Ocean Shore, Desert Night, Rooftop Rain, Firefly Lake, and Northern Lights, for 10 in total
- New kinds of motion: falling snow, chimney smoke and steam, waves rolling up the beach, gliding seabirds, a bobbing boat light, sand drifting off dunes, city windows switching on and off, a blinking tower beacon, puddle ripples, fireflies, and drifting aurora curtains
- Live wallpaper: any scene behind your desktop icons, frozen whenever a window is open. One scene, a new one each day, or a new one each unlock, with battery options (pause on battery, in battery saver, or below a percentage)
- All 10 scenes on the new tab page: one you pick, or a different one each tab

### Changed
- The settings window is now called Settings, and has a Live wallpaper section
- Scene images are smaller, so everything loads faster

## v1.0.0 (November 22, 2026)

The first public release.

### Moon Clock
- Transparent desktop clock with the date and tonight's real moon phase
- "Tonight's constellation": which constellation is best placed in your sky, where to look, and when
- "What's playing" from any app in Windows' media controls
- Right-aligned layout that stays anchored when lines appear or disappear
- Draggable, and click-through once locked
- Taskbar icon centering, plus clear, blurred, or frosted taskbar looks (replaces TaskbarX and TranslucentTB); Windows 10 only, and switched off automatically on Windows 11
- Settings window that always fits on screen, with Save, Preview, and Cancel pinned at the bottom

### Away mode
- Desktop icons hide when you're idle, then a full-screen sky fades in
- The real moon from NASA's lunar maps, lit with tonight's phase
- Twinkling stars, shooting stars, meteor showers, and real constellations
- Live illustrated scenes: River Night, Galaxy Peaks, City Below, and Rain over the City
- Weather that matches outside, sky colors through the day, clouds, aurora, and parallax
- The song you're listening to, 250 poetic lines, and a quiet shimmer every hour
- Locks your PC after a while, with a checkbox to turn it off and adjustable timing; never locks while something is playing; skips full-screen apps and games

### Nightfall Tab
- New tab page with a clock, search, quick links, and an animated starfield
- Meteor showers on demand (press M), real constellations, and click-to-draw constellations
- Live weather, the real moon phase, and sky colors through the day
- A tiny see-through to-do and notes panel

### Nightfall Theme
- Plum and lavender browser colors to match

### Tools
- One-command install and uninstall
- Backup and restore scripts
- Shortcut-arrow removal, with an undo
- Installer asks before changing the taskbar, and only reports success when the build really worked
- Uninstaller restores desktop icons and the taskbar even if Moon Clock has to be force-closed
