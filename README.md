# MagicMovieNight

A C# app that reads what your household actually watches — across Plex, Trakt, and the
streaming services — and uses Claude to tell you what to put on tonight.

Runs in Docker on the NUC, deployed as a Portainer stack.

---

## The honest constraint

**Netflix, Hulu, Prime Video and the Apple TV app publish no viewing-history API.**
Nothing can poll them. That is not a limitation of this app; there is simply no
endpoint to call. So the architecture routes around it:

| Source | How it gets in | Fidelity |
|---|---|---|
| **Plex** | Tautulli API, polled | Who watched, which device, completion % |
| **Trakt** | Trakt API, polled | Per-play, unified across services |
| **Netflix / Hulu / Prime** | Trakt browser scrobbler (ongoing) + CSV import (backfill) | Title and date only |
| **Apple TV app** | No export exists | — |

Two things make the streamers work:

1. **The Trakt browser scrobbler.** Trakt's extension pushes web playback on Netflix,
   Hulu, Prime and Disney+ straight into Trakt, which this app already reads. Install it
   once and streamer history flows in automatically from then on. It does not cover
   playback on the Apple TV box itself.
2. **CSV exports**, uploaded on the Import page. The only way to backfill years of
   history in one go.

Anything played through Plex or Channels on the Apple TV *is* captured — Tautulli sees it.

---

## How it decides

```
Tautulli ─┐
Trakt ────┼──> Catalog (TMDB) ──> Watch history ──> Taste profile ──┐
CSVs ─────┘                                                          │
                                                                     ▼
Plex library ────┐                                            ┌─────────────┐
Trakt VIP recs ──┼──> Candidate pool (trimmed) ──────────────>│   Claude    │──> Tonight's slate
Continue watching┘                                            └─────────────┘
```

Claude never sees raw history — tens of thousands of rows would be mostly noise. It sees
a distilled **taste profile** and a pre-trimmed **candidate pool**, and does the part it
is actually good at: judgement, and explaining itself.

The profile does two things that matter:

- **Recency weighting.** Every watch decays on a 180-day half-life. What you finished
  last month counts far more than what you finished in 2019.
- **Fidelity weighting.** A Tautulli event that says *"96% complete, living room Apple
  TV"* outweighs a Netflix CSV row that says only *"you saw something that day."*

Abandonment is a first-class signal: a film you bailed on at 8% is negative evidence
about that *kind* of film, not just that title. Finishing it later cancels that out.

**Per-person and household profiles.** Viewers are discovered automatically from each
source and can be merged on the Household page (Tautulli's `scott` and Trakt's `swolf`
are the same person). Picking for two people pools their history rather than averaging
two profiles — that way something you both watched outweighs two things only one of you
did, which is what *"what should **we** watch"* actually means.

---

## Layout

```
src/
  MagicMovieNight.Core/          Domain model, taste profile maths — no I/O
  MagicMovieNight.Data/          EF Core + Postgres, migrations
  MagicMovieNight.Integrations/  Trakt, Tautulli, TMDB, Claude, CSV importers
  MagicMovieNight.Web/           Blazor Server UI + background sync
tests/
  MagicMovieNight.Tests/         Profile maths and CSV parsing
```

---

## Setup

### 1. Credentials

| What | Where | Required |
|---|---|---|
| `ANTHROPIC_API_KEY` | [console.anthropic.com](https://console.anthropic.com) | Yes |
| `TMDB_API_TOKEN` | [themoviedb.org/settings/api](https://www.themoviedb.org/settings/api) — v4 read token | Effectively yes |
| `TAUTULLI_API_KEY` | Tautulli → Settings → Web Interface → API | For Plex history |
| `TRAKT_CLIENT_ID` / `TRAKT_CLIENT_SECRET` | [trakt.tv/oauth/applications](https://trakt.tv/oauth/applications), redirect URI `urn:ietf:wg:oauth:2.0:oob` | For Trakt history |

TMDB is listed as "effectively required" because without it titles arrive with no genres,
cast, runtime or streaming availability — and recommendations get noticeably worse.

### 2. Deploy

```bash
cp .env.example .env    # fill it in
docker compose up -d
```

On the NUC, deploy as a Portainer stack instead and paste the variables into the stack's
environment editor. The stack joins the existing `home` bridge network, so `tautulli:8181`
resolves directly and swag can reverse-proxy it.

### 3. Pair with Trakt

Open the app → **Setup** → **Start pairing**. Trakt has no redirect URI for a container,
so it uses the device flow: the app shows a code, you enter it at trakt.tv once, and the
refresh token handles it from then on. The token lives in the `config` volume and survives
redeploys.

### 4. First sync

**Setup → Sync now.** The first run backfills ten years and will take a while — every new
title costs a TMDB lookup. After that it syncs every six hours in the background.

### 5. Merge duplicate viewers

**Household.** Each source invents its own user ids, so the same person usually appears
two or three times after the first sync. Merge them so their taste is one profile.

---

## Local development

```bash
docker compose up -d db
dotnet run --project src/MagicMovieNight.Web
```

```bash
dotnet test
```

Schema changes:

```bash
dotnet ef migrations add SomeChange -p src/MagicMovieNight.Data -s src/MagicMovieNight.Web -o Migrations
```

Migrations apply automatically on boot — this is a single-instance household app, there is
no rolling deploy to coordinate with.

---

## Cost

One recommendation run sends the taste profile plus ~150 candidates. The system prompt is
byte-identical between runs and sits behind a cache breakpoint, so you pay for it once
rather than on every movie night. Expect a few cents per run on `claude-opus-5`.

Turn it down in the stack environment if you want:

- `CLAUDE_MODEL=claude-sonnet-5` — cheaper, still good at this
- `Claude__Effort=medium` — less deliberation per run
- `Claude__MaxCandidates=80` — smaller pool

---

## Status

Working and tested: taste profile maths, CSV parsing, EF schema, the full build.

Not yet verified against live services — Trakt pairing, a real Tautulli sync, and the
Docker image build all need credentials and a running daemon to exercise. See the
deployment checklist in the project notes.
