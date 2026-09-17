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
| **Netflix thumbs** | `Ratings.csv` from the account data export | Explicit opinion |
| **Your own ratings** | Rated in the app | Explicit opinion, per episode if you want |
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

The profile does three things that matter:

- **Recency weighting.** Every watch decays on a 180-day half-life. What you finished
  last month counts far more than what you finished in 2019.
- **Fidelity weighting.** A Tautulli event that says *"96% complete, living room Apple
  TV"* outweighs a Netflix CSV row that says only *"you saw something that day."*
- **Ratings outrank watching.** Watching something only says you watched it. A
  thumbs-down says what you thought — so ratings push genre and cast affinity in either
  direction rather than merely adding to it, and a thumbs-down is weighted harder than
  a thumbs-up because people rate things down far less often.

Abandonment is a first-class signal too: a film you bailed on at 8% is negative evidence
about that *kind* of film, not just that title. Finishing it later cancels that out.

**Per-person and household profiles.** Viewers are discovered automatically from each
source and can be merged on the Household page (Tautulli's `scott` and Trakt's `swolf`
are the same person). Picking for two people pools their history rather than averaging
two profiles — that way something you both watched outweighs two things only one of you
did, which is what *"what should **we** watch"* actually means.

### Ratings

Rate anything you've watched on the **Rate** page — films, series, or individual episodes.
Three states: 👎 / 👍 / ❤️ ("loved it"), matching the scale Netflix already uses. Clicking
the active one clears it. You can also rate a pick straight from the recommendation card.

Episode ratings are deliberately weighted at about a third of a title rating, and never
land a show on the do-not-recommend list. *"Great show, terrible finale"* is a real
opinion and the model is told to read it that way.

### Ratings vs. verdicts

Two different questions, deliberately kept apart:

- **Thumbs (👎 / 👍 / ❤️)** answer *what did you think of it*. Per person, weighted, and
  the strongest input to the taste profile.
- **"We watched it" / "Already seen it"** answer *what became of this recommendation*.

"We watched it" records a real watch immediately rather than waiting for Tautulli to
notice — which matters because if you watched it on a streaming service, nothing else
may ever notice.

"Already seen it" is the more interesting one. It is not an opinion, it is a gap in the
data: you saw that title somewhere this system cannot observe, which is the blind spot of
the whole design. It removes the title from future picks, but deliberately invents no
watch date — claiming a film from years ago was watched tonight would corrupt the recency
weighting, and admitting we do not know is better.

Your Netflix thumbs import alongside these. They live in `Ratings.csv` inside the **full
account data export** (netflix.com/account/getmyinfo) — not the viewing-activity page,
which only has watch dates. Both the current thumbs scale and the pre-2017 five-star
scale are handled. Ratings you make in the app are never overwritten by an import.

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

CI publishes `ghcr.io/wolfson292/magicmovienight:latest` on every push to `main`, and the
package is public — the NUC pulls it with no registry login.

```bash
cp .env.example .env    # fill it in
docker compose up -d
```

On the NUC, deploy as a Portainer stack instead and paste the variables into the stack's
environment editor. `compose.yaml` deliberately has no `build:` key, because Portainer has
no build context and a build directive makes the file undeployable there.

The stack joins the existing `home` bridge network, so `tautulli:8181` resolves directly
and swag can reverse-proxy it. That network must already exist on the host:

```bash
docker network create home    # only if it does not exist yet
```

The app publishes on host port **8100** by default. Check the port is actually free before
deploying — and note that listing container ports is not enough, because host-network
containers do not report published ports. Ask the host's network namespace instead:

```bash
ss -ltn | awk 'NR>1{print $4}'
```

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

### 6. Rate some things

**Rate.** Recommendations get noticeably sharper once there are explicit opinions to work
from. Importing your Netflix thumbs is the fastest way to seed this — years of opinions in
one upload.

---

## Local development

```bash
docker compose up -d db
dotnet run --project src/MagicMovieNight.Web
```

To run the whole stack from source rather than the published image:

```bash
docker compose -f compose.yaml -f compose.dev.yaml up -d --build
```

```bash
dotnet test
```

The suite is offline and deterministic by default. Two live tests in `ClaudeLiveTests`
hit the real API and no-op unless a key is present — they verify that the request the
engine builds is actually accepted, and that the system prompt really does come back from
cache on a repeat call. To run them:

```bash
set -a && . ./.env && set +a && dotnet test
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

**Verified end to end.** The image builds, the stack boots, both migrations apply, and
`/health` returns 200. The live Claude integration is confirmed against the real API:
the request shape is accepted, structured output parses, picks stay inside the candidate
pool, and prompt caching measurably works — a repeat call read back all 1,237 system-prompt
tokens instead of paying for them again. 41 tests.

**Still unverified:** Trakt pairing and a real Tautulli sync, both of which need
credentials this project does not have yet.
