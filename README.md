# WatchSync

An Emby plugin that keeps watch history and **Continue Watching** the same between two
servers, per user:

- this Emby server ↔ a Plex server
- this Emby server ↔ another Emby server
- or two remote servers (Emby or Plex), with this Emby doing the work

For each pair of users (for example *alex* on Emby and the Plex owner account) it syncs:

| What | How |
|---|---|
| Watched / unwatched | Marking a title watched or unwatched on one side does the same on the other. |
| Resume position | A movie or episode stopped part-way resumes at the same point on the other side. |
| Continue Watching order | Titles are written so both rows end up in the same order (most recent first). |
| Removed from Continue Watching | "Remove from Continue Watching" (Plex) / "Hide" (Emby) carries over. |
| Re-watches | Starting a watched title again keeps it watched and shows it in Continue Watching on both. |

Titles are matched by their TMDB, IMDb or TVDB ids (episodes by show id plus season and episode
number), so the two libraries don't need the same file paths or names.

## Install

1. Download `WatchSync.Emby.dll` from the [latest release](../../releases/latest).
2. Copy it into Emby's `plugins` folder and restart Emby.
3. Open **WatchSync** from the Emby dashboard menu (or Plugins → WatchSync).

## Set up

On the **Sources & users** tab:

1. **Source A** is normally *This Emby server* (no key needed).
2. **Source B** is the other server:
   - **Plex**: the address, e.g. `http://<plex-host>:32400`, and the server owner's Plex token
     ([how to find it](https://support.plex.tv/articles/204059436-finding-an-authentication-token-x-plex-token/)).
     Shared and Home users of that server are listed too.
   - **Another Emby server**: its address, e.g. `http://<emby-host>:8096`, and an API key
     from that server's Dashboard → API Keys.
3. Press **Test and load users** on each source, then **Add a pair** for each user to sync, and **Save**.

On the **Settings** tab, **Preview only** is on to begin with: a pass works out what it would
change and lists it on the Status tab without writing anything. When the list looks right,
press **Apply the preview** or turn Preview off.

## How it runs

- **Right away** for changes on this Emby server: about 10 seconds after a title is stopped or
  marked (once a minute while something is playing).
- **Every few minutes** (*Check for changes every*, default 5): picks up what changed on the
  other server since the last check.
- **A full pass** every *Full comparison every* hours (default 6), and from Scheduled Tasks →
  *Sync watch history (WatchSync)* or the **Full sync** button.

If a pass fails (the other server is down or too busy to answer), automatic passes wait 5
minutes, then 10, 20, 40 and at most 60 before trying again; **Full sync** always runs straight away.
With **Preview only** on, the plan is worked out on the full-sync schedule or with **Full sync**,
not on every check.

When the same title changed on both sides since the last pass, the most recent change wins.
*Direction* can limit syncing to one way.

## Continue Watching tab

Shows both rows side by side and marks titles that are missing, at a different position, or
in a different place. **Fix now** runs a full pass. Things that are expected and only noted:

- Emby and Plex show one episode per show (the latest one played).
- Plex also lists the next episode of a show you're watching; Emby keeps those in *Next Up*.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Direction | Both ways | Or A → B only / B → A only. |
| Watched and unwatched / Resume positions / Removing from Continue Watching | on | What to carry over. |
| Preview only | on | Plan without writing. |
| Check for changes every | 5 min | Quick pass for the other server. |
| Full comparison every | 6 h | Compares everything. |
| Emby / Plex: watched after (%) | 90 | A position past this marks the title watched on that server. Match each server's own setting. |
| Ignore positions under | 60 s | Very short plays aren't synced as resume points. |

**Reset sync state** forgets what was synced before; the next pass compares everything again
as if for the first time (it doesn't change anything on the servers by itself).

## Notes

- Keys and tokens are stored in the plugin's configuration on this Emby server and are kept
  out of the log.
- Titles without any TMDB/IMDb/TVDB id can't be matched and are listed under *Not matched* on
  the Status tab.
- Plex sets "last viewed" to the moment it's written, so WatchSync writes Continue Watching
  titles oldest first (a second apart) to keep the order. A large first sync to Plex can take
  a while for that reason.

## Build

```
mkdir emby-sdk   # copy MediaBrowser.Common.dll, MediaBrowser.Controller.dll and
                 # MediaBrowser.Model.dll from an Emby server's system folder into it
dotnet build WatchSync.Emby.csproj -c Release -o out
```

Pushing a new `<Version>` in `WatchSync.Emby.csproj` to `main` builds and publishes a release.
