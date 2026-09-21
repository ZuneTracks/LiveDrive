# Changelog

All notable user-facing changes are documented here.

## 1.7.3.0 - 2026-09-21

### Features

- Camera Roll backup results now report how many items were uploaded, skipped,
  and failed, along with the reason for the first failure.

### Fixes

- Fixed a single problem file being able to stop an entire scheduled Camera Roll
  backup. Remaining files now continue to upload, while cancelling a backup still
  stops the run immediately.
- Fixed the background backup component being compiled against an older .NET
  framework package than the main app, which produced a mixed runtime closure
  inside a single Store package and could cause Store-side .NET Native
  compilation to fail. Windows 10 Mobile compatibility is unchanged.
- Fixed Store submissions failing .NET Native compilation in the Store's cloud
  compiler. The submitted package declared a Windows 10 Mobile minimum version
  while also requesting a .NET Native runtime that only exists on newer Windows
  releases, so the Store had no valid toolchain to build it with. The app now
  targets a single consistent platform version, and Windows 10 Mobile support is
  retained.
- Fixed Store submissions still failing .NET Native compilation because the
  uploaded package omitted the Windows Runtime metadata file that the Store's
  cloud compiler needs to rebuild the app. It is now included in Store packages,
  while sideload packages continue to exclude it.

## 1.6.0 - 2026-09-17

### Features

- Added separate **Photos** and **Videos** library views, with independently filtered
  image and video grids.
- Added **Manage folders** to add or remove OneDrive folders included in the media
  library after initial setup.
- Added LiveDrive-defined photo albums that synchronize between LiveDrive devices,
  plus month-based and **On this day** collections.
- Added photo/video actions for viewing, adding photos to albums, saving, sharing,
  copying, moving, deleting, and viewing **File info**.
- Added desktop right-click context menus for Drive items and photo/video tiles,
  while retaining touch long-press menus on Mobile.
- Added the **File info** dialog with filename, type, size, creation and modified
  dates, photo-taken date, and OneDrive location.
- Added OneDrive storage-usage indicators in navigation and Account & Settings.
- Added optional Live Tile modes for storage usage, recent files, a selected photo,
  and a selected album, including a manual tile refresh action.
- Added Camera Roll backup controls: **Check Camera Roll now** and an opt-in,
  best-effort 15-minute scheduled scan for supported photos and videos.
- Added scanning of accessible SD-card **Camera Roll** folders for Camera Roll backup.
- Added scan and upload stage updates to manual Camera Roll checks.
- Added the last-started date and time for manual and scheduled Camera Roll scans.
- Moved scheduled Camera Roll backup to a separate background process, isolating it
  from the foreground LiveDrive app.
- Added an in-app notification diagnostic that submits a test toast and records the
  device notification setting plus the toast submission result or error.
- Added a styled **About LiveDrive** button in Account & Settings.
- Added cache-backed large thumbnails for the media grid.

### Fixes

- Fixed Camera Roll backup and folder listings missing files when a OneDrive folder
  spans multiple Microsoft Graph result pages.
- Increased the durable local thumbnail cache to 128 MB and prefer valid local medium
  and large thumbnail files over remote OneDrive URLs, with oldest cached files evicted
  first when the limit is exceeded.
- Fixed photo album membership persistence when adding newly discovered media.
- Fixed album picker selection and Lumia theme rendering issues.
- Fixed album counts that could include unavailable or stale item IDs.
- Fixed media-index persistence so filtering the Photos or Videos view cannot remove
  the other media type from the shared index.
- Fixed deletion wording to use **file/files**, correctly covering photo, video, and
  mixed selections.
- Fixed removal of indexed media and cached thumbnails when OneDrive reports an item
  deleted or moved out of a selected folder.
- Added thumbnail-file synchronization and transient-network retries to prevent
  cache access conflicts and improve refresh reliability.
- Fixed opaque transient HTTP errors during photo and video thumbnail loading by
  retrying thumbnail requests and keeping the media grid usable if they fail.
- Fixed a Mobile startup XAML parsing crash caused by an unsupported File info icon.
- Fixed image-based Live Tiles to include Mobile-compatible medium and wide bindings
  without filename overlays.
- Improved selected-photo and album Live Tile image quality by using cached large
  thumbnails.
- Limited SD-card Camera Roll discovery to likely camera-folder paths, preventing a
  manual check from traversing an entire removable drive.
- Fixed Camera Roll backup history exceeding the Windows Phone application-settings
  size limit after repeated scans.
- Fixed Camera Roll uploads creating renamed copies of files already present in the
  LiveDrive Camera Roll folder.
