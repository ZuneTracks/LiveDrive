# LiveDrive

LiveDrive is an independent, phone-friendly UWP prototype for browsing a personal OneDrive through Microsoft Graph. It targets **Windows 10 version 1703 (build 15063)** and uses `HttpClient` instead of Graph SDK packages so its platform floor stays compatible with Windows 10 Mobile.

## Set up Microsoft sign-in

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), register an application for **Accounts in any organizational directory and personal Microsoft accounts** (or choose the account type appropriate to your deployment).
2. Under **Authentication**, add the Mobile and desktop application redirect URI `https://login.microsoftonline.com/common/oauth2/nativeclient` (or choose another public-client redirect URI and update both places).
3. Enable public-client flows. LiveDrive is a public client: **do not create or embed a client secret**.
4. Under **API permissions**, add delegated Microsoft Graph permissions `User.Read`, `Files.ReadWrite.All`, and `Files.ReadWrite.AppFolder`, then grant consent as required by your tenant.
5. Replace `REPLACE-WITH-YOUR-CLIENT-ID` in `Services\ClientConfiguration.cs` with the Application (client) ID. If you changed the redirect URI, update `RedirectUri` there too.

## Capabilities

- Microsoft sign-in through `WebAuthenticationBroker`, authorization code + PKCE, refresh tokens in PasswordVault.
- Browse folders, navigate upward, search, upload files to a user-selected OneDrive folder, download/open files, save-as, create anonymous view links, and copy, move, or delete selected files/folders where the account policy permits it. Copy and move name conflicts preserve the destination file and create an automatically numbered copy, such as `photo (1).jpg`.
- Camera backup page that queues manually selected photos/videos while the app remains foregrounded and uploads them to a root-level `LiveDrive Camera Roll` folder. Queue items can be removed before uploading; selecting an already-queued device file skips it. Queue status appears only for uploads in progress or failures. Completed uploads leave the active queue and remain available from the persisted **Recently uploaded** list. Name conflicts in OneDrive are saved as new copies rather than overwriting the existing file.
- The first Photos visit lets the user choose OneDrive folders to include; LiveDrive caches only those folders and retains a separate delta link for each source. Change the selection later with **Folders** on the Photos command bar.
- Photos render cached metadata immediately and load thumbnails only as tiles become visible, with at most two thumbnail requests active at once.
- After the first complete sync, LiveDrive stores a compact 150-photo preview and retains the full index in memory. The photo grid renders and scrolls in small batches while synchronization updates the non-blocking footer indicator.
- Long-pressing a photo opens actions to view, add it to an album, save it, share it with compatible device apps, copy or move it, or delete it with confirmation.
- LiveDrive-defined photo albums stored as a small JSON file in the private OneDrive app folder, plus automatic month-based and prior-year **On this day** collections from the local photo index.

## Legacy Mobile limitations

Windows 10 Mobile does not provide dependable indefinite background execution/transfer for a standalone app. Camera backup is intentionally a **foreground manual queue**, rather than claiming continuous automatic backup. Uploads use Graph upload sessions for large files but require the app to stay active. Opening a downloaded file also depends on an installed app registered for that file type. Link sharing can be disabled by OneDrive or tenant policy.

## Build

Open `LiveDrive.csproj` in Visual Studio with the Windows 10 SDK (10.0.15063.0 or compatible SDK) and the Universal Windows Platform development workload installed. Deploy to a Windows 10 Mobile device running build 15063 or later.
