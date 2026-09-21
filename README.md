# LiveDrive

LiveDrive is an independent, phone-friendly UWP prototype for browsing a personal OneDrive through Microsoft Graph. It targets **Windows 10 version 1703 (build 15063)** and uses `HttpClient` instead of Graph SDK packages so its platform floor stays compatible with Windows 10 Mobile.

## Public documents

- [App description and feature set](APP_DESCRIPTION.md)
- [Changelog](CHANGELOG.md)
- [Privacy Policy](https://zunetracks.github.io/LiveDrive/privacy.html)
- [Terms of Service](https://zunetracks.github.io/LiveDrive/terms.html)

The GitHub Pages site source is in `docs/`.

## Set up Microsoft sign-in

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), register an application for **Accounts in any organizational directory and personal Microsoft accounts** (or choose the account type appropriate to your deployment).
2. Under **Authentication**, add the Mobile and desktop application redirect URI `https://login.microsoftonline.com/common/oauth2/nativeclient` (or choose another public-client redirect URI and update both places).
3. Enable public-client flows. LiveDrive is a public client: **do not create or embed a client secret**.
4. Under **API permissions**, add delegated Microsoft Graph permissions `User.Read`, `Files.ReadWrite.All`, and `Files.ReadWrite.AppFolder`, then grant consent as required by your tenant.
5. Replace `REPLACE-WITH-YOUR-CLIENT-ID` in `Services\ClientConfiguration.cs` with the Application (client) ID. If you changed the redirect URI, update `RedirectUri` there too.

## Capabilities

- Microsoft sign-in through `WebAuthenticationBroker`, authorization code + PKCE, refresh tokens in PasswordVault.
- Browse folders, navigate upward, search, upload files to a user-selected OneDrive folder, download/open files, save-as, create anonymous view links, and copy, move, or delete selected files/folders where the account policy permits it. Copy and move name conflicts preserve the destination file and create an automatically numbered copy, such as `photo (1).jpg`.
- Camera backup page that queues manually selected photos/videos while the app remains foregrounded and uploads them to a root-level `LiveDrive Camera Roll` folder. Users can use **Check Camera Roll now** for an immediate scan or opt into a best-effort 15-minute Camera Roll scan that uploads newly detected supported media. Scans include the Camera Roll library and accessible SD-card folders named `Camera Roll`; manual checks show the current scan or upload stage. Queue items can be removed before uploading; selecting an already-queued device file skips it. Queue status appears only for uploads in progress or failures. Completed uploads leave the active queue and remain available from the persisted **Recently uploaded** list. Files already present in OneDrive with the same name are skipped rather than added as renamed copies.
- The first Photos visit lets the user choose OneDrive folders to include; LiveDrive caches only those folders and retains a separate delta link for each source. Change the selection later with **Folders** on the Photos command bar.
- Photos render cached metadata immediately and load thumbnails only as tiles become visible, with at most two thumbnail requests active at once.
- After the first complete sync, LiveDrive stores a compact 150-photo preview and retains the full index in memory. The photo grid renders and scrolls in small batches while synchronization updates the non-blocking footer indicator.
- Long-pressing a photo opens actions to view, add it to an album, save it, share it with compatible device apps, copy or move it, or delete it with confirmation.
- LiveDrive-defined photo albums stored as a small JSON file in the private OneDrive app folder, plus automatic month-based and prior-year **On this day** collections from the local photo index.
- Optional Live Tile modes for OneDrive storage usage, most recent OneDrive file, a selected cached photo, or a selected LiveDrive album.

## Legacy Mobile limitations

Windows 10 Mobile does not provide dependable indefinite background execution/transfer for a standalone app. Scheduled Camera Roll backup uses a 15-minute trigger in a separate background process, so its completion does not terminate the foreground LiveDrive UI. Windows can still delay, throttle, or cancel that worker; it is not continuous or guaranteed backup. After updating the app, turn scheduled Camera Roll backup off and back on once to replace an existing task registration. Manual uploads remain available when an upload must finish while the app is active. Uploads use Graph upload sessions for large files but require the app to stay active. Opening a downloaded file also depends on an installed app registered for that file type. Link sharing can be disabled by OneDrive or tenant policy.

## Build

Open `LiveDrive.csproj` in Visual Studio with the Windows 10 SDK (10.0.15063.0 or compatible SDK) and the Universal Windows Platform development workload installed. Deploy to a Windows 10 Mobile device running build 15063 or later.

## Release process

1. Start from a clean, committed source tree. Set `Package.appxmanifest`'s package version to match the intended release tag, commit the change, and create an annotated tag from that exact commit.
2. From that clean tag, produce signed ARM and x64 Store/upload package artifacts. Both Release artifacts must retain .NET Native. Do not reuse packages from another commit or architecture.
3. Inspect each outer package, its inner architecture package, and their package maps before publishing. For sideload (`Release`) packages, SDK `Windows.winmd` and SDK `*Contract.winmd` files must be absent. For `Store` packages destined for Partner Center, SDK `WinMetadata\Windows.winmd` **must be present** — the Store recompiles the uploaded MSIL with .NET Native in the cloud and cannot resolve Windows Runtime types without it. App-owned runtime-component WinMDs must remain in both cases.
4. Verify every `PackageDependency` in each Store package's manifest is obtainable on Windows 10 Mobile. Read the `MinVersion` attribute of each `<PackageDependency>` inside the inner architecture package's `AppxManifest.xml` — that declared minimum is the only thing that matters. Do **not** judge this from the redist packages staged under `Dependencies\`: Visual Studio always stages the newest copy installed on the build machine regardless of what the app declares, so that folder routinely shows `14.0.33519.0` even when the manifest correctly declares `14.0.22929.0`.

   Mobile stopped receiving framework-package updates at build 15254, so its Store can only provision `Microsoft.VCLibs.140.00` up to roughly `14.0.24217.0`. A package that declares a newer minimum **installs from the Store on Mobile but terminates immediately after the splash screen**, because the Store never provisions the dependency and activation fails. Building against the 15063 target and the .NET Native 1.7 toolchain yields a Mobile-compatible `14.0.22929.0`; a value such as `14.0.33519.0` indicates a newer toolchain leaked in and the Store build must not be published.

   **A successful sideload is not evidence that the Store build is correct.** Sideloading installs the bundled redist by hand, bypassing the Store catalog, and that redist declares support down to `10.0.10042.0` — so it installs happily on a Lumia even when the Store would refuse to provision it. This defect is only observable through an actual Store install on a Mobile device, which is why it survived every release from 1.0.2 through 1.1.1.

   The correct dependency set depends on `UseDotNetNativeToolchain` being set for every non-Debug configuration, not merely on the `Microsoft.NETCore.UniversalWindowsPlatform` version. Without it the .NET Native targets never engage and packaging silently falls back to the in-box SDK path, producing `14.0.33519.0` and `Microsoft.NET.CoreRuntime.1.1` even when the restore graph correctly resolves the 1.7 compiler. A build that emits no `obj\<arch>\<config>\ilc` directory, or a payload containing MSIL reference assemblies and no `System.Private.CoreLib`, did not run .NET Native at all.
5. Attach the signed packages to the GitHub release along with certificate and dependency installation directions appropriate to the signing setup. Attach only artifacts produced by step 2 from the tagged commit. Packages built before the tag, or from a working tree with uncommitted changes, silently misrepresent the release: the attached binary can declare a different identity, device family, or dependency set than the tagged source would produce.
6. Publish the GitHub release from the same verified tag. Do not move the tag or alter historic releases after publication.

A published artifact is not a reliable witness of what a commit builds. When diagnosing a packaging defect, treat any dependency block read from a released binary as a hypothesis and confirm it with a build from a clean checkout of the source in question.
