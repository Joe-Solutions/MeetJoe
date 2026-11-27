# Spotify setup for MeetJoe Music

MeetJoe Music uses **your users’ Spotify accounts** (Authorization Code + PKCE). MeetJoe does not pay Spotify for streams.

## 1. Create a Spotify app

1. Open [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) and create an app.
2. App type: **Desktop**.
3. Redirect URI allowlist — add **exactly**:

   ```text
   http://127.0.0.1/callback
   ```

   (No port. Spotify allows dynamic ports on loopback; MeetJoe picks a free port at connect time.)

4. Copy the **Client ID** (public). Do **not** put a Client Secret in the desktop app — PKCE does not need one.
   If a Client Secret was ever pasted into chat or committed, **rotate it** in the Spotify dashboard immediately.

## 2. Configure MeetJoe

MeetJoe ships with the Joe Solutions Spotify **Client ID** baked in (public PKCE id).  
Optional override:

```bash
export MEETJOE_SPOTIFY_CLIENT_ID="your-client-id-here"
```

Restart MeetJoe after changing the variable. Never set or ship `client_secret` in the desktop app.

## 3. User expectations

- **Connect Spotify** opens the browser; after approve, MeetJoe loads playlists / liked / recent.
- Playback is **Spotify Connect**: the user should have the Spotify app open on a phone or computer.
- Full play/pause control usually requires **Spotify Premium**.
- Tokens stay on the desktop (`LocalApplicationData/MeetJoe/spotify-refresh.bin`). Disconnect clears them.

## 4. Shared test account (org-wide, testing only)

Development Mode only allows ~5 allowlisted Spotify users. For MeetJoe testing you can publish **one** Premium account so every signed-in MeetJoe user uses that library:

1. Deploy an API build that includes `/api/music/spotify/*`.
2. On a machine, Connect the allowlisted Premium Spotify account.
3. Music → **Share with MeetJoe (testing)**.
4. Other MeetJoe users open Music — no personal Connect needed; they search/play the shared account.
5. Keep the Premium account’s Spotify app open on a device for playback.

Clear with `DELETE /api/music/spotify/shared` or remove `/var/meetjoe/keys/spotify-shared.json` on the API host.

## 4. Scopes requested

`user-read-private`, `user-read-email`, playlist + library read, recently played, playback state / modify / currently playing.
