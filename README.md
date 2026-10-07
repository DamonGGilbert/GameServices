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
- [ ] **Phase 2** - per-game player profiles + cloud saves; server-authoritative economy (packs, rewards)
- [ ] **Phase 3** - friends, blocking, real-time chat (SignalR/WebSockets)
- [ ] **Phase 4** - guilds, roles, guild chat, group boss battles with scheduled background jobs
- [ ] **Phase 5** - PostgreSQL + EF migrations, Docker, CI, deployment, logging and health checks
