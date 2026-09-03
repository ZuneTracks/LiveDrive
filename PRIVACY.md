# LiveDrive Privacy Policy

**Last updated: September 3, 2026**

LiveDrive is an independent UWP client for accessing a user's Microsoft OneDrive account on Windows 10 Mobile. It is not affiliated with, endorsed by, or operated by Microsoft.

## Information LiveDrive accesses

After the user signs in, LiveDrive requests the Microsoft Graph permissions needed to provide its features:

- Basic signed-in account access through `User.Read`.
- Access to OneDrive files and folders through `Files.ReadWrite.All`.
- Access to LiveDrive's private OneDrive app folder through `Files.ReadWrite.AppFolder` for LiveDrive-defined photo albums.

LiveDrive uses these permissions only to perform actions initiated by the user, such as browsing, uploading, downloading, copying, moving, deleting, sharing, and organizing files.

## Storage and retention

LiveDrive stores the following data in its private app storage:

- Microsoft sign-in tokens in Windows PasswordVault.
- The selected Photos source folders, cached photo metadata, and cached thumbnails to improve performance.
- Names of recently completed manual camera uploads.

LiveDrive-defined album data is stored in its private OneDrive app folder.

Signing out removes the stored sign-in token, local photo index, and recent upload history. Files uploaded to, copied within, or deleted from OneDrive remain subject to the user's OneDrive account, including its recycle-bin and retention behavior.

## Information sent to third parties

LiveDrive communicates directly with Microsoft identity services for sign-in and Microsoft Graph for OneDrive operations. It does not operate its own server, collect analytics, sell personal information, or send OneDrive file contents to an independent LiveDrive service.

When the user creates a share link, Microsoft Graph and OneDrive determine its availability and scope according to the account and tenant policy. The user is responsible for sharing that link only with intended recipients.

## Security

LiveDrive uses the Windows account and application-storage facilities available on the supported platform and transmits OneDrive requests over HTTPS. No client secret is embedded because LiveDrive is registered as a public client. As with any cloud-storage client, users should protect their device and Microsoft account credentials.

## Changes and contact

This policy may be updated as LiveDrive changes. The current version is published in this repository. Questions or concerns can be submitted through the [LiveDrive issue tracker](https://github.com/ZuneTracks/LiveDrive/issues).
