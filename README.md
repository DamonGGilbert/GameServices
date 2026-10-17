# GameServices

A game-agnostic backend ("game services") for my games: accounts and sign-in today;
cloud saves, friends, chat and guilds next. One account works across every game I build
(the Yu-Gi-Oh-style card game, the walking game, and future projects).

**Stack:** C# / .NET 10, ASP.NET Core (minimal APIs), Entity Framework Core (SQLite now, PostgreSQL later),
JWT access tokens + rotating refresh tokens, xUnit integration tests.

## Run it

```bash
# run the automated tests (16 of them)
dotnet test

# start the server (development mode)
cd src/GameServices.Api
dotnet run --urls http://localhost:5080
```

Then open **http://localhost:5080/scalar/v1** in a browser: a page where you can try every endpoint.

## Endpoints (phase 1)

| Method | Path | What it does |
| ------ | ---- | ------------ |
| GET  | `/health` | Is the server alive? (used by monitoring/hosting) |
| POST | `/api/auth/register` | Create an account (username, password, optional email) |
| POST | `/api/auth/login` | Sign in, get an access token + refresh token |
| POST | `/api/auth/refresh` | Swap a refresh token for a fresh pair (old one stops working) |
| POST | `/api/auth/logout` | Revoke a refresh token |
| GET  | `/api/me` | Who am I? (needs `Authorization: Bearer <access token>`) |

## Which game is asking? (`X-Game-Id`)

One account plays every game, but **guilds belong to one game**. Each game sends a header with every request:

| Header value | Game | Guild size |
| ------------ | ---- | ---------- |
| *(no header)* or `yugioh` | the card game | up to 30 |
| `walking` | the walking game | up to 10 |

- A player can be in **one guild per game** (database index on `UserId + GameId`), and guild names are unique per game.
- Each game only sees, joins and gets invited to **its own** guilds. Older guilds were made before this existed, so they count as card-game guilds.
- Bosses, line-ups and Guild Wars use cards and Duel Points, so they are card-game only (other games get a 404).
- Friends, blocking, direct chat and avatars stay **account-wide**, so your friends follow you between games.
- Limits live in one place: `Infrastructure/GameIds.cs` (`MaxGuildMembers`). Guild replies include `maxMembers`, so the games' "x/10" displays never drift.

## Walking: steps, cheers and step races (`/api/walking`)

The walking game reports **today's total** about once a minute; the server decides how much of it to believe (`Walking/StepRules.cs`).

| Route | What it does |
| ----- | ------------ |
| `POST /steps` | `{ day, steps, utcOffsetMinutes }` - today's total. Returns what was accepted. |
| `GET /me` | My today / week / all-time, races won and lost, and the privacy switch. |
| `PUT /privacy` | `{ shareSteps }` - may friends see my steps? (On by default.) |
| `GET /friends` | Friends' steps today and this week (zeros if they keep them private). |
| `GET /cheers/phrases` | The fixed list of cheers ("Keep going!" ...). Players pick one; they can't type their own. |
| `POST /cheers/{friendId}` | `{ kind }` - cheer on a friend (3 per friend per day). |
| `GET /cheers`, `POST /cheers/seen` | Cheers I haven't seen yet; mark them seen. |
| `GET /races`, `POST /races` | My races; challenge a friend `{ friendId, hours }` (24 or 72; up to 3 open races). |
| `POST /races/{id}/accept`, `/decline`, `/cancel` | Answer or withdraw a challenge. |

**Anti-cheat rules** (server-side, so a modified app can't skip them):
- No more than **200 steps a minute** since the last report (plus a little slack), and **60,000 a day**.
- Totals only go up; one report every **15 seconds** at most (`429` otherwise).
- The day must be *today* where the player is (offset within ±14 hours, with a 2-hour grace just after midnight).
- Anything over the limit is rejected and counted; more than 1,000 rejected steps flags the profile for a look.

**Walking guilds** (`Walking/WalkingGuilds.cs`, guilds of up to 10):

| Route | What it does |
| ----- | ------------ |
| `GET /guild/journey` | This week's **Guild Journey**: a shared target of 5,000 steps a day per member (Monday-Sunday, UTC), who walked what, and last week's result. |
| `POST /guild/journey/claim` | Collect last week's reward (Glimmer). Once per player per week (unique index). |
| `GET /guild/race` | This weekend's **guild step race** (Saturday-Monday, UTC), the last result, or when the next one starts. |
| `GET /guild/ranking` | The top 10 walking guilds by tier score. |

- Journey reward = 250 x (how far the guild got, max 100%) x (how much of your fair share you walked, max 100%), plus 100 if the guild hit its target. So the most anyone can earn is 350, and one keen walker can't carry everyone.
- Each Saturday, walking guilds with at least 2 members are paired by rating. A guild's score is its **top 5** walkers' weekend steps. The winner gets +1 war win and +20 rating, which uses the same tiers and banners as the card game. These races live in their own `GuildStepRaces` table, so the card game's Guild Wars job never touches them.

**Races** count only steps walked *after* the race starts (everyone's all-time total is snapshotted when the challenge is accepted). `WalkingLifecycleService` checks once a minute: it finishes races whose time is up (and records the winner) and expires challenges nobody answered within 24 hours.

## Map relay: OpenStreetMap squares for the walking game (`/api/map`)

`GET /api/map/15/{x}/{y}` returns one map square (roads, paths, rivers, coastline, parks, woods and water) as
Overpass JSON, which is exactly what the walking game's drawn map reads.

Why it exists: the public Overpass servers are run by volunteers for developers, not as the back end for an app
with many players, and they're often too busy to answer. So the server fetches each square **once**, keeps it on disk
(`map-cache/`, gzip, about 20 to 70 KB each) for 30 days, and shares it with every player who walks there.

| Situation | What happens |
|---|---|
| Fresh copy on disk | Sent straight away (about 0.3 s instead of about 30 s from Overpass) |
| No copy yet | Fetched from Overpass, saved, sent |
| Overpass busy or down | The old copy, marked `X-Map-Stale: true`; with no copy at all, `503` plus `Retry-After` |
| 50 players ask for one square at once | One fetch; everyone shares it ("request coalescing") |

The relay is polite to Overpass: one request at a time, a 1.5 s gap, a pause for everyone after "429 too busy", and
an answer with a `remark` (Overpass gave up part-way) counts as a failure, never a half-empty square.

**Privacy:** no account is needed or looked at, and map requests aren't logged. A square is about 1 km across, so the
server never builds up a picture of where anyone walks. Settings live under `Map` in `appsettings.json`
(cache folder, days, Overpass addresses), and there's a separate rate limit (`RateLimit:MapRequestsPerMinute`).

Map data © OpenStreetMap contributors, available under the Open Database Licence.

## How sign-in works

1. **Register**: the server validates the input, stores only a salted **hash** of the password, and returns tokens.
2. **Access token** (JWT, 15 min): a signed note saying "this is user X". The game sends it with every request.
   The server verifies the signature - no database lookup needed - so it's fast.
3. **Refresh token** (random, 30 days): used only to get a new access token. The server stores its **hash** so it can
   be revoked. Each refresh token works **once** ("rotation"). If an old one is replayed, the whole account is signed
   out everywhere (a stolen-token defence called *reuse detection*).

## Security decisions (good interview material)

- Passwords: never stored; PBKDF2 hashing via ASP.NET Core Identity's `PasswordHasher`, which upgrades old hashes automatically.
- Same vague "Invalid username or password" for unknown user and wrong password, and a fake hash check for unknown
  users, so response time doesn't reveal which usernames exist (*user enumeration*).
- Case-insensitive unique usernames enforced by a **database unique index** (not just a code check) to defeat race conditions.
- Rate limiting on the auth endpoints (429 after too many attempts per minute per IP).
- Server-side validation of everything; the client is never trusted.
- Entities are never sent over the wire; DTOs only (no accidental leaking of hashes).
- JWT signing key is a secret: a dev-only key in `appsettings.Development.json`, and **must** come from the
  `Jwt__Key` environment variable in production.

## Project layout

```
src/GameServices.Api/
  Program.cs                      the request pipeline
  Infrastructure/                 dependency-injection setup
  Auth/                           endpoints, token service, DTOs
  Data/                           database entities + AppDbContext
tests/GameServices.Tests/         integration tests (real server, throw-away database)
```

## Roadmap

- [x] **Phase 1** - accounts, sign-in, token rotation, rate limiting, tests
- [x] **Phase 2a** - cloud saves (per user, per game) with optimistic concurrency; EF Core migrations
- [x] **Phase 2b** - server-authoritative economy: card catalog, wallet, server-side pack opening, capped duel rewards, ledger
- [x] **Phase 3 (server)** - friends, blocking, direct chat (REST + SignalR live push), presence
- [x] **Phase 4 (server)** - guilds, roles, invites, guild chat, group boss battles with a scheduled background job
- [~] **Phase 5 (in progress)** - PostgreSQL + EF migrations, Docker, CI, deployment, logging and health checks


## Running with Docker + PostgreSQL (Phase 5)

One-time setup (needs Docker Desktop running):

```
powershell -ExecutionPolicy Bypass -File scripts\make-env.ps1     # creates .env with random secrets
docker compose up --build                                        # database + server
```

The game server is then on `http://localhost:5080` (same address as `dotnet run`, so stop that first).
`Database:Provider` selects the engine: `Sqlite` (default, `dotnet run`) or `Postgres` (Docker).
Each engine has its own migrations folder (`Migrations/` and `MigrationsPostgres/`).

Run the tests against PostgreSQL instead of SQLite:

```
$env:TEST_DATABASE = "postgres"; dotnet test
```
