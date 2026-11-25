# Users and permissions

## Users

The admin, made at setup, may do everything. Add other people under **Settings → Users**, giving each only what they
need: adding someone gives them a username and password to start with. Nobody can give a permission they don't have.
Removing someone signs them out and revokes their API keys; what they asked for stays.

Each user's card lists what they may do; click the list to see all of it. Its settings button changes what they may
do; the admin may do everything, so theirs has none. On your own card it opens your account.

Everyone changes their own account under **Your account**, at the top of Settings or in the account menu at the top
right, saved together:

- **Login**: their username and password. Changing either needs their current password, and a new password signs
  them out everywhere else.
- **Media server accounts**, when a media server that's set up syncs playlists: their login on each, to sync their
  playlists. The one in use is marked; the others' logins are kept for switching to them. Each login is checked with
  its server when they save.

**Sign out**, at the foot of the page, signs them out in that browser.

## What each permission allows

| Permission | Allows |
| --- | --- |
| **Browse** | Seeing the library, the catalogue, what's being fetched and what's running |
| **Request** | Asking for music to be fetched into the library, and ignoring albums and songs; looking for upgrades, approving or rolling them back, replacing files that seem lossy, and keeping a copy turned away for its quality |
| **Listen** | Playing music, fetching songs to listen to without adding them, having playlists, and [Subsonic players](subsonic-players.md) |
| **Review and match** | Approving, matching, excluding, deferring and hiding files on Review, and trusting files that seem lossy to be lossless |
| **Format** | Rewriting files' tags and covers, and moving them; choosing album and song covers; looking for, choosing, editing and deleting a song's lyrics |
| **Remove music** | Deleting music from the library, or hiding it |
| **Scan** | Scanning the library, and pausing, cancelling and reordering tasks |
| **Settings and plugins** | Changing settings and plugins, and reading the log |
| **Webhooks** | Managing webhooks. On an API key, it also lets the app make and revoke your keys |
| **Manage users** | Adding, changing and removing other users |

Someone who just listens needs Browse and Listen. Buttons for what someone may not do don't show.

## API keys

Apps use a key from **Settings → Server → API keys**. Anyone signed in can make keys there, as many as they like, but each can do only
what its maker may. A key belongs to whoever made it and can never do more than they may: if their
permissions shrink, or they're removed, so does the key. **Everything you may do** gives a key all its owner's
permissions, now and later. Your keys are under **Your keys**; the admin also sees everyone else's, under **Other
people's keys**, and can revoke any of them. See the [client guide](../client-guide.md) for the API itself.

## A lost admin password

Start Melodex once with `Melodex__ResetLogin=true`, open the web app and set a new password, then remove the variable
and restart. Other users, and everyone's keys, are kept. Anyone else who forgets theirs can have a new one set by
someone who may manage users.

Next: [Appearance](appearance.md).
