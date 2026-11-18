# The Melodex app

The Melodex app is the web app on your phone: scans, Review, requests, artwork, lyrics, plugins, users and settings, as
in a browser, and it plays music the same way. For listening above all, a Subsonic player pointed at Navidrome may suit
you better.

## Getting it

- **Android**: each release has an APK, `melodex-<version>.apk`, on the
  [releases page](https://github.com/dextrd/Melodex/releases). Open it on your phone and allow installing it. Later
  versions install over it, keeping your servers and sign-ins.
- **iOS**: not available yet. It's on its way through TestFlight.

## Connecting

The first time, the app asks for your Melodex server:

- **Look on this network** finds any Melodex on the Wi-Fi you're on, at its usual ports (8787, 80 and 8080). Tap one to
  use it.
- Or type the **address** you open Melodex at in a browser, such as `192.168.0.10:8787`. Away from home, use the
  address that reaches it from anywhere, for example through Tailscale: `https://melodex.your-tailnet.ts.net`.

Then sign in with your username and password, as in a browser. The app keeps you signed in until you sign out.

The app remembers every server you've connected to, each with its own sign-in. **Another server** in the account menu
goes back to the list, to switch or add one; **Forget** there removes a server and its sign-in.

## Your devices

Signing in on the app makes a key for that device, kept in its secure storage. **Your account**, in the account menu,
lists your devices, in the app and in a browser: **Sign out** on one there and that app asks to sign in again, as you
would if you lost the phone. A new password signs out all your devices but the one you changed it on, and the admin
setting you a new password signs out all of them.

A device's key isn't an API key: it does what you may, no more, and isn't listed under **Settings → API**.

## What's different on a phone

- **Exports** under **Tools** open your phone's share sheet, to save the file or send it somewhere.
- Links to the API reference open in your browser.
- Music stops when the app is closed or the phone locks: playing in the background is for a later version.
- The app needs a Melodex as new as itself, which says so when you connect.
