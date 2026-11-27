# MeetJoe Music — plan (YouTube URL)

**Status:** Spotify removed. Music opens YouTube URLs in an in-app WebView.  
**Chosen:** Paste / browse YouTube (no Spotify Connect, no org shared token).

## Goal

Rail **Music** (between Files and Settings): paste a YouTube link or browse youtube.com inside MeetJoe.

## v1

1. URL field + Open
2. Browse YouTube (home)
3. In-app WebView (same native stack as LiveKit: WKWebView / WebView2)
4. Recent links (local JSON)
5. Pause HTML5 / YouTube player when a call or meeting starts
6. Mini bar while on other rails → Open Music (WebView lives on the Music page)

## Out of scope

- Spotify OAuth / Connect / shared Premium token
- MeetJoe-paid streaming catalog
- Background audio when Music view is torn down (return to Music to continue)

## Done

- [x] Remove Spotify client + API shared endpoints
- [x] YouTube URL normalize + Music WebView host
- [x] Shell mini reminder + call pause hook
