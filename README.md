<h1 align="center">Jellyfin Merge Versions Plugin</h1>

<p align="center">
Jellyfin Merge Versions automatically groups repeated movies and episodes. This fork adds incremental processing for large libraries while retaining Jellyfin's native merge and split operations.

</p>

## Install

1. Download the .zip file from release page
2. Extract it and place the .dll file in a folder called ```plugins/Merge Versions``` under  the program data directory or inside the portable install directory
3. Restart Jellyfin

## Incremental processing

New, removed and metadata-updated movies and episodes are added to a deduplicated, persistent queue. After a 60-second settling period, the plugin queries only the affected version groups. Provider-backed groups are queried in batches of up to 256 keys, and changes received while a batch is running remain queued for the next batch.

Updates caused by a merge are ignored by the listener, preventing a merge feedback loop. Merge and split operations continue to use the installed Jellyfin server's native video-version actions.

The full-library movie and episode tasks remain available as manual repair operations, but no longer have a default daily trigger. Existing installations may retain their saved task triggers; disable those triggers after upgrading if full daily scans are not wanted.

Pending work is journaled in Jellyfin's plugin configuration directory and restored after a restart. A target is removed from the journal only after its batch completes successfully. The journal is compacted automatically and created with owner-only permissions on Unix systems.

## User guide

1. After installation, run **Merge All Movies** and **Merge All Episodes** once to process an existing library.
2. Normal library changes are processed automatically after the initial full-library pass.
3. Use the scheduled-task page or plugin configuration when another full-library repair pass is required.
4. Splitting all versions is available through the plugin configuration.

Merge and split use the installed Jellyfin server's native video-version actions. No URL or API key configuration is needed. Manual operations require administrator permissions.

This does not migrate or repair local-version groups created by earlier plugin releases. Split follows Jellyfin's native behavior for linked alternate versions; it does not detach local alternate versions.

Cancellation stops the scan between groups, after the current native operation finishes.



## Build
1. Clone or download this repository
2. Ensure you have .NET Core SDK setup and installed
3. Build plugin with following command.
```sh
dotnet publish Jellyfin.Plugin.MergeVersions/Jellyfin.Plugin.MergeVersions.csproj --configuration Release --output bin
```
4. Place the resulting `.dll` file in a folder called `plugins/Merge Versions` under the program data directory or inside the portable install directory.

## Tests

```sh
dotnet test Jellyfin.Plugin.MergeVersions.sln --configuration Release
```

The tests cover controller discovery and dispatch, DI scope lifetime, error handling, authorization requirements, asynchronous completion, cancellation, targeted provider queries, already-merged groups, queue changes received during an active batch and persistent queue recovery. They do not replace a merge/split smoke test against a Jellyfin test library.

The adapter discovers `Jellyfin.Api.Controllers.VideosController` through MVC and resolves it from Jellyfin's service container. If Jellyfin changes the action signatures, operations fail with a compatibility error instead of falling back to manual relationship changes.
