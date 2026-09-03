# LiveDrive

LiveDrive is an independent, phone-friendly UWP client intended to be a practical OneDrive app replacement for Windows 10 Mobile version 1703 (build 15063) and later. It connects directly to Microsoft Graph using the user's Microsoft account and focuses on browsing, organizing, moving, and safeguarding OneDrive content from a legacy Windows phone.

LiveDrive is not affiliated with Microsoft and does not attempt to replicate Microsoft branding or proprietary assets.

## Microsoft account sign-in

- Signs in through the Windows `WebAuthenticationBroker` using OAuth authorization code flow with PKCE.
- Uses a public-client app registration with no embedded client secret.
- Stores sign-in tokens in Windows PasswordVault.
- Supports personal Microsoft accounts and eligible work or school accounts, subject to tenant policy.

## My Drive browsing

- Browses OneDrive folders and files.
- Opens folders and navigates back toward the OneDrive root.
- Presents loading, empty, and error states for phone-sized screens.
- Refreshes the visible folder automatically after successful file changes.

## File management

- Selects multiple files or folders for copy, move, and delete actions.
- Uses a navigable OneDrive destination picker for copy and move operations.
- Preserves existing destination files during name conflicts by creating numbered copies, such as `photo (1).jpg`.
- Deletes files through OneDrive, where recovery remains subject to the OneDrive recycle bin and account policy.

## File upload and download

- Uploads a chosen device file to a selected OneDrive folder.
- Uses direct Microsoft Graph uploads, including upload sessions for larger files.
- Shows upload progress.
- Downloads files for opening with a compatible installed app or for saving to a user-selected device location.

## Search and sharing

- Searches the user's OneDrive files and folders.
- Creates anonymous view links where the user's OneDrive account or organization policy permits them.
- Copies returned share links to the Windows clipboard when available.

## Photos

- Lets the user select the OneDrive folders LiveDrive should index as photo sources.
- Uses per-folder Microsoft Graph delta synchronization to find additions, changes, and deletions.
- Caches photo metadata and visible thumbnails locally for faster return visits and reduced network requests.
- Provides a phone-friendly photo grid with selectable thumbnail sizes and sort modes.
- Opens photos and offers long-press actions to view, save, share, copy, move, delete, or add a photo to an album.

## Albums and collections

- Creates LiveDrive-defined photo albums.
- Stores album definitions in LiveDrive's private OneDrive app folder.
- Builds automatic month/year collections from cached photo dates.
- Provides an **On this day** collection for photos from prior years with the current month and day.

## Camera upload

- Provides a **manual** queue for photos and videos selected by the user.
- Uploads selected media to a root-level `LiveDrive Camera Roll` folder in OneDrive.
- Requires LiveDrive to stay in the foreground while uploading.
- Lets the user remove queued files before uploading.
- Skips selecting the same device file twice and preserves same-named OneDrive files by creating a new numbered copy.
- Removes completed items from the active queue and keeps their names in a persisted **Recently uploaded** list.

**Important:** Camera upload is not continuous or automatic backup. Windows 10 Mobile does not provide dependable long-running background transfer for this independent app, so the user must manually choose media and keep LiveDrive active until uploads finish.

## Local data and controls

- Stores Photos cache data, thumbnail cache data, and recent camera-upload names in LiveDrive's private local storage.
- Clears the local photo index and camera-upload history when the user signs in or signs out.
- Applies theme preference changes on the next LiveDrive launch for compatibility with the supported Mobile platform.
