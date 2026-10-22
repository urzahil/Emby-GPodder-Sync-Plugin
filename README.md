# Emby gPodder Sync plugin

An Emby Server plugin that syncs **podcast listening progress** with a **gpodder v2 sync server**, such as
[PodBridge](https://github.com/urzahil/PodBridge). AntennaPod (or any other gpodder client) talks to the same
server, so a podcast half-listened on the phone resumes at the same point in Emby, and the other way round.

## How it works

```
AntennaPod ──gpodder sync──► gpodder server (PodBridge) ◄──gpodder sync── Emby + this plugin
```

* **Episodes are matched by their audio URL.** Emby and AntennaPod must subscribe to **exactly the same feed
  addresses**, so that both see the same enclosure URL for each episode. The plugin only touches Emby audio items
  whose path (or media source) is an http(s) URL — what a podcast plugin creates from an RSS feed.
* **Emby → server:** when playback stops (and every 60 s while it plays), or you mark an episode played, the plugin
  sends a gpodder `play` action with the position and length. gpodder clients need the feed URL too, so the plugin
  reads the feeds in the server's subscription list to find which feed each episode belongs to.
* **Server → Emby:** a scheduled task (at startup and every 5 minutes) reads the latest play position of every
  episode and applies it to the matching Emby item, but only when it is newer than Emby's own state and didn't come
  from Emby itself. An episode with 30 s or less left (or 95 % played) is marked played.
* With PodBridge, positions of Il Post episodes sent by Emby also reach ilpost.it, exactly as AntennaPod's do.

## Status

| Area | State |
|---|---|
| `dotnet build` | Verified (0 warnings, 0 errors) |
| gpodder calls (device, subscriptions, feeds, push, pull, wrong password) | Verified against a local PodBridge |
| Loading in Emby, finding podcast items by URL | Working on Emby 4.10.1 (Linux): 2,694 of 2,751 server episodes matched on the first run |
| AntennaPod → Emby and Emby → AntennaPod progress | Working on Emby 4.10.1 with PodBridge and AntennaPod |

Check the Emby log for lines starting with `GPodderSync:`. If the pull says Emby has no audio items with an
http(s) path, your podcast plugin stores episodes differently and matching needs adjusting.

## Target

* Built against **Emby Server 4.9.x** (`MediaBrowser.Server.Core` **4.9.1.90**, `netstandard2.0`).
* There is **no settings page**. Configuration is a file.

## Build

Requires the .NET SDK 6.0+ (developed with 8.0).

```powershell
cd src
dotnet build -c Release
```

Result: `src\bin\Release\netstandard2.0\Emby.GPodderSync.dll` is the only file you need. Tagged releases (`v*`) also
get the DLL attached by GitHub Actions.

## Install and configure

1. Stop Emby Server.
2. Copy `Emby.GPodderSync.dll` into the Emby `plugins` folder (Windows: `%AppData%\Emby-Server\programdata\plugins`).
3. Start Emby once. The plugin writes its config file and logs the path, normally
   `programdata\plugins\configurations\Emby.GPodderSync.xml`. Stop Emby again.
4. Edit that file:

   | Setting | Meaning |
   |---|---|
   | `ServerUrl` | Address of the gpodder server as Emby reaches it, e.g. `http://homeserver:8080` |
   | `Username` / `Password` | gpodder login (for PodBridge: `GPODDER_USERNAME` / `GPODDER_PASSWORD`). Plain text in this file — keep it private |
   | `DeviceId` | Device name this Emby uses on the server; default `emby` |
   | `SyncEmbyUsers` | Comma-separated Emby user names to sync; empty = all users. All of them share the one gpodder account |
   | `FinishedRemainingSeconds` / `FinishedRatio` | When an episode counts as finished: default 30 s left or 95 % played |

5. Start Emby. *Dashboard → Scheduled Tasks → gPodder Sync → Sync podcast progress* runs at startup and every 5
   minutes; run it by hand to sync right away.

## Known limitations

* **Marking an episode unplayed is not synced**, in either direction (gpodder clients can't clear progress).
* Episodes are matched only by exact audio URL. Different feed addresses for the same podcast (e.g. Il Post's own RSS
  in Emby and PodBridge's in AntennaPod) won't match.
* The pull reads the whole action history each run; fine for one person's listening, not tuned for huge histories.
* Progress sync uses timestamps; if the clocks of the devices disagree, an older state can win.
* A player that only pauses (e.g. Emby for Android) is reported as stopped by Emby after about a minute of idling,
  so the position is sent then.
* With `SyncEmbyUsers` empty, the server's progress is applied to **every** Emby user. Set it to your own user
  name if others share the Emby server.
* Subscriptions are not synced; add the same feeds to Emby and AntennaPod yourself.

## Project layout

```
src/Emby.GPodderSync.csproj  netstandard2.0, MediaBrowser.Server.Core 4.9.1.90
src/Plugin.cs                BasePlugin<PluginConfiguration> (no UI pages)
src/PluginConfiguration.cs   settings stored in Emby.GPodderSync.xml
src/EntryPoint.cs            creates the config file; sends Emby progress to the gpodder server
src/SyncTask.cs              scheduled task: applies the server's progress to Emby items
src/FeedIndex.cs             episode URL → feed URL map, built from the subscribed feeds
src/GPodderClient.cs         gpodder v2 calls and RSS enclosure reading
```
