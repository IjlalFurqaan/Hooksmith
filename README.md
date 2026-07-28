# Hooksmith

A self-hosted webhook delivery platform. Producers post events; Hooksmith signs them, delivers
them to registered subscriber endpoints, retries failures on a jittered exponential backoff,
dead-letters what will never succeed, and keeps a per-attempt audit trail you can replay from.

> **Build status: Phases 1–2 of 6 complete.** The domain model, retry policy, signature scheme,
> persistence, ingest and SSRF guard are built and verified. **Nothing is delivered yet** — the
> delivery worker is Phase 3. See [What is not implemented](#what-is-not-implemented) for the
> honest inventory.

---

## Table of contents

- [Status at a glance](#status-at-a-glance)
- [Quick start](#quick-start)
- [Solution layout](#solution-layout)
- [Domain model](#domain-model)
- [The delivery state machine](#the-delivery-state-machine)
- [Retry policy](#retry-policy)
- [Signature scheme](#signature-scheme)
- [Verifying a signature (C# and TypeScript)](#verifying-a-signature)
- [API reference](#api-reference)
- [Idempotent ingest](#idempotent-ingest)
- [The SSRF guard](#the-ssrf-guard)
- [Configuration](#configuration)
- [Database schema](#database-schema)
- [Testing and verification](#testing-and-verification)
- [What is not implemented](#what-is-not-implemented)
- [Known gaps and deliberate decisions](#known-gaps-and-deliberate-decisions)
- [Out of scope](#out-of-scope)

---

## Status at a glance

| Phase | Scope | Status |
|---|---|---|
| **1** | Core domain, transition table, retry policy, signature scheme | ✅ **Done** |
| **2** | EF Core + Postgres, migrations, subscription CRUD, ingest, idempotency, SSRF guard | ✅ **Done** |
| **3** | Delivery worker: `BackgroundService`, `SKIP LOCKED`, Channels, HTTP dispatch | ❌ Not started |
| **4** | Delivery query endpoints, replay, SignalR live feed | ❌ Not started |
| **5** | Angular dashboard | ❌ Not started |
| **6** | Testcontainers integration tests, CI, full compose | ❌ Not started |

**175 unit tests**, all passing in ~190 ms. Release build clean at zero warnings with
`TreatWarningsAsErrors`.

### What works end to end today

You can register subscriptions, post events, and watch Hooksmith fan them out into `Pending`
delivery rows in Postgres — correctly matched, correctly deduplicated, with hostile URLs
refused at the door.

### What does not work yet

Those `Pending` rows **stay pending forever**. There is no worker to claim them, no HTTP
dispatch, no attempt records, no retries, no dead-lettering, no replay, no dashboard.

---

## Quick start

**Prerequisites:** .NET 10 SDK, Docker (for Postgres).

```bash
git clone <your-remote> && cd Hooksmith
```

Start Postgres:

```bash
docker compose up -d postgres
```

Apply migrations and run the API (Development applies migrations on startup):

```bash
dotnet run --project Hooksmith.Api --launch-profile http
```

The API listens on `http://localhost:5266`. Register a subscription:

```bash
curl -X POST http://localhost:5266/api/v1/subscriptions -H "Content-Type: application/json" -d "{\"name\":\"Billing\",\"url\":\"https://example.com/hooks\",\"eventTypes\":[\"invoice.paid\"]}"
```

Post an event:

```bash
curl -X POST http://localhost:5266/api/v1/events -H "Content-Type: application/json" -d "{\"type\":\"invoice.paid\",\"payload\":{\"amount\":\"19.99\"}}"
```

The response reports how many deliveries were created. Inspect them:

```bash
docker exec hooksmith-postgres psql -U hooksmith -d hooksmith -c "SELECT status, attempt_count, next_attempt_at FROM deliveries;"
```

They will all read `Pending` — and will stay that way until Phase 3 exists.

Run the tests:

```bash
dotnet test Hooksmith.slnx
```

---

## Solution layout

```
Hooksmith.Core            domain model, state machine, retry policy, signature computation
Hooksmith.Infrastructure  EF Core + Postgres, SSRF guard   (HTTP dispatch: Phase 3)
Hooksmith.Api             ASP.NET Core controllers          (SignalR hub + worker: Phases 3–4)
Hooksmith.Tests           xUnit + FluentAssertions          (Testcontainers: Phase 6)
hooksmith-ui              Angular dashboard                 (Phase 5 — does not exist yet)
docker-compose.yml        Postgres                          (API service: Phase 6)
```

### Build constraints, enforced not just intended

These live in `Directory.Build.props`, so a project added later cannot silently opt out:

- `<Nullable>enable</Nullable>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- `<GenerateDocumentationFile>true</GenerateDocumentationFile>` — this promotes CS1591 to an
  error, which turns "every public API gets XML docs" into a build failure rather than a review
  comment.
- **`Hooksmith.Core` has zero package references.** Everything it needs (`TimeProvider`,
  `System.Text.Json`, `System.Security.Cryptography`, `System.Collections.Frozen`) ships in the
  shared framework. Verify with `grep PackageReference Hooksmith.Core/Hooksmith.Core.csproj` —
  no matches.
- **`DateTimeOffset.UtcNow` is never called.** `TimeProvider` is injected everywhere;
  `FakeTimeProvider` drives the retry-schedule tests, which is why they run in milliseconds with
  no `Thread.Sleep` anywhere in the codebase.
- Domain types are `record`s with no mutable public setters. State changes return new instances.
- Async all the way down: no `.Result`, no `.Wait()`, no `async void`.

Package versions are centralised in `Directory.Packages.props` with transitive pinning.

---

## Domain model

All four are `sealed record` types in `Hooksmith.Core`, mapped directly by EF rather than copied
into parallel persistence models — the mapping lives in `IEntityTypeConfiguration` classes in
Infrastructure, which is what keeps Core free of EF.

| Type | Fields |
|---|---|
| `Subscription` | `Id`, `Name`, `Url`, `Secret`, `EventTypes[]`, `IsActive`, `CreatedAt` |
| `Event` | `Id`, `Type`, `Payload` (`JsonDocument`), `ReceivedAt`, `IdempotencyKey?` |
| `Delivery` | `Id`, `EventId`, `SubscriptionId`, `Status`, `AttemptCount`, `NextAttemptAt?`, `CreatedAt` |
| `Attempt` | `Id`, `DeliveryId`, `AttemptNumber`, `StatusCode?`, `ResponseBody?`, `Error?`, `DurationMs`, `AttemptedAt` |

`NextAttemptAt` is nullable and non-null **exactly when** a delivery is awaiting dispatch
(`Pending` or `Retrying`).

`Attempt.ResponseBody` is capped at **2 KB measured in UTF-8 bytes**, cut on a character
boundary. A subscriber answering a webhook with a 5 MB HTML error page should not be able to
fill the database one failed attempt at a time, and a truncated emoji must not leave a lone
surrogate.

---

## The delivery state machine

```
Pending      -> InFlight
InFlight     -> Succeeded | Retrying | DeadLettered
Retrying     -> InFlight
Succeeded    -> (terminal)
DeadLettered -> Pending   (replay only)
```

The table is a `FrozenDictionary` in `DeliveryStatusTransitions` — **data, not a pile of
`if` statements**. That lets the test enumerate the full 5×5 cartesian product and assert on all
25 edges, rather than only the 6 legal ones someone remembered to write down. Illegal
transitions throw `InvalidDeliveryTransitionException`.

**`AttemptCount` increments on `Claim()`, before the HTTP call, not after it.** A worker that
dies mid-flight has already spent the attempt, so a crash-looping delivery exhausts its budget
and dead-letters. Counting on completion would make a crash free and retry forever.

---

## Retry policy

Six attempts total. For attempt *n*, `raw = 2^(n-1)` seconds and
`delay = raw/2 + random(0, raw/2)`:

| Attempt | Raw window | Actual delay |
|---|---|---|
| 1 | 1s | 0.5–1s |
| 2 | 2s | 1–2s |
| 3 | 4s | 2–4s |
| 4 | 8s | 4–8s |
| 5 | 16s | 8–16s |
| 6 | 32s | 16–32s |

A delivery that never succeeds dead-letters **between 31.5s and 63s** after ingest.

**The `raw/2` floor keeps backoff monotonic.** Each window's floor equals the previous window's
ceiling, so the unluckiest draw for attempt *n+1* is never sooner than the luckiest draw for
attempt *n*. Full jitter (`random(0, raw)`) would break that.

**The jitter is load-shedding, not decoration.** If a subscriber is down for a minute, every
delivery that failed against it computed its schedule from roughly the same base instant with
the same formula. Without the random term they would all be due at the identical millisecond, so
the moment that subscriber recovers it takes the entire backlog in one burst and falls over
again — a retry loop that keeps a recovering service down.

The tests check **spread, not just bounds**: a policy that computes the floor and forgets the
jitter term passes a bounds check perfectly, so the test also requires draws either side of each
window's midpoint and 900+ distinct values out of 1000.

---

## Signature scheme

Every delivery carries:

```
X-Hooksmith-Signature: t=<unix-seconds>,v1=<lowercase-hex-hmac-sha256>
```

The MAC is computed over **`{timestamp}.{body}`** — the timestamp is *inside* the signed
material, not merely transported alongside it.

### Why the timestamp is signed

Sign the body alone and ship `t=` as adjacent metadata, and an attacker captures one valid
delivery, then re-sends it tomorrow with `t=` rewritten to the current time. The signature still
verifies, because it never covered `t=`. Your freshness check is now validating a field the
attacker fully controls — it is decorative.

Signing the timestamp closes both exits:

- Replay it **unmodified** → the MAC verifies, but the timestamp is stale and the tolerance
  window rejects it.
- **Rewrite** the timestamp to look fresh → the signed payload changed, so the MAC fails.

### Verification properties

- `CryptographicOperations.FixedTimeEquals` on the decoded bytes.
- **Order is parse → constant-time MAC compare → freshness.** `TimestampOutsideTolerance` is
  only ever reported for a message whose MAC already verified, so a forger learns nothing about
  our clock from the answer.
- **The 5-minute tolerance is symmetric.** A stale timestamp is a replay; a far-future one is a
  broken or manipulated clock. Accepting the future case would let one skewed sender mint
  deliveries that stay valid indefinitely.
- Header parsing is strict, except that **unknown schemes are ignored** so a future `v2=` can
  ship alongside `v1=`. Duplicate `t=` or `v1=` elements are rejected outright rather than
  letting an attacker choose which one the verifier reads.

Secrets are generated with `RandomNumberGenerator` (256 bits, `whsec_` prefix, base64url) — not
`Random`. The retry jitter can be predictable without consequence; a predictable signing secret
means anyone can forge deliveries.

---

## Verifying a signature

> ⚠️ **Sign and verify the raw bytes.** Read the body as a string *before* deserialising it.
> Re-serialising JSON before verifying is the most common cause of signatures that fail for no
> visible reason.

### C#

`SignatureVerifier` is public API in `Hooksmith.Core` and depends on nothing outside the BCL, so
a receiving application can reference it directly instead of reimplementing HMAC comparison and
getting `==` on strings.

```csharp
using Hooksmith.Core.Signing;

app.MapPost("/hooks", async (HttpRequest request, TimeProvider clock) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    var verifier = new SignatureVerifier(clock); // 5-minute tolerance by default
    var result = verifier.Verify(
        request.Headers[SignatureHeader.HeaderName],
        body,
        MySubscriptionSecret);

    if (result != SignatureVerificationResult.Valid)
    {
        return Results.Unauthorized();
    }

    var payload = JsonDocument.Parse(body);
    return Results.Ok();
});
```

### TypeScript (Node)

No dependency on Hooksmith; this implements the wire format directly.

> This snippet is not aspirational. It was run against signatures produced by the actual
> `WebhookSigner` — including a body containing `Müller 🪝 事件` — and it accepts the genuine
> signature, rejects a tampered body, a wrong secret, a rewritten timestamp, a stale
> signature, duplicate `v1=` elements and a malformed header, while ignoring an unknown `v2=`.

```ts
import { createHmac, timingSafeEqual } from "node:crypto";

export function verifyHooksmithSignature(
  header: string | undefined,
  body: string,
  secret: string,
  toleranceSeconds = 300,
): boolean {
  if (!header) return false;

  let timestamp: string | undefined;
  let signature: string | undefined;

  for (const element of header.split(",")) {
    const i = element.indexOf("=");
    if (i <= 0) continue;
    const key = element.slice(0, i).trim();
    const value = element.slice(i + 1).trim();

    // A duplicate element would let an attacker pick which one we read.
    if (key === "t") {
      if (timestamp !== undefined) return false;
      timestamp = value;
    } else if (key === "v1") {
      if (signature !== undefined) return false;
      signature = value;
    }
    // Unknown schemes (a future v2=) are ignored, not rejected.
  }

  if (!timestamp || !signature) return false;
  if (!/^\d+$/.test(timestamp)) return false;
  if (!/^[0-9a-f]{64}$/.test(signature)) return false;

  // The timestamp is part of the signed payload, which is what stops it being rewritten.
  const expected = createHmac("sha256", secret)
    .update(`${timestamp}.${body}`, "utf8")
    .digest();
  const provided = Buffer.from(signature, "hex");

  if (expected.length !== provided.length) return false;
  if (!timingSafeEqual(expected, provided)) return false;

  // Freshness is checked only after the MAC verifies, and applies in both directions.
  const age = Math.abs(Math.floor(Date.now() / 1000) - Number(timestamp));
  return age <= toleranceSeconds;
}
```

Express, with the raw body preserved:

```ts
app.post(
  "/hooks",
  express.raw({ type: "application/json" }),
  (req, res) => {
    const body = req.body.toString("utf8");
    if (!verifyHooksmithSignature(req.header("X-Hooksmith-Signature"), body, SECRET)) {
      return res.sendStatus(401);
    }
    res.sendStatus(200);
    void handle(JSON.parse(body));
  },
);
```

---

## API reference

All routes are under `/api/v1`. Enums serialise as names, not integers.

### Subscriptions

| Method | Route | Status |
|---|---|---|
| `GET` | `/api/v1/subscriptions` | ✅ |
| `GET` | `/api/v1/subscriptions/{id}` | ✅ |
| `POST` | `/api/v1/subscriptions` | ✅ |
| `PATCH` | `/api/v1/subscriptions/{id}` | ✅ |
| `DELETE` | `/api/v1/subscriptions/{id}` | ✅ |

`POST` body — `secret` is optional and generated if omitted, which is the recommended path:

```json
{
  "name": "Billing service",
  "url": "https://example.com/hooks",
  "eventTypes": ["invoice.paid", "customer.created"],
  "isActive": true
}
```

`201 Created` returns the subscription **and the secret in full**. This is the only response
that contains it; every later read returns a masked preview.

`PATCH` is partial — any omitted or null field is left unchanged. Changing `url` **re-runs the
SSRF guard**: a subscription that was safe at registration is not automatically safe after
someone repoints it.

`DELETE` **cascades to delivery history**. To stop delivering while keeping the audit trail,
`PATCH` `isActive` to `false` instead.

Event types are normalised (trimmed, lowercased) on both registration and ingest, so a producer
sending `Invoice.Paid` reaches a subscriber that registered `invoice.paid`.

### Events

| Method | Route | Status |
|---|---|---|
| `POST` | `/api/v1/events` | ✅ |

```json
{ "type": "invoice.paid", "payload": { "amount": "19.99" } }
```

Returns **202 Accepted**, not 200 — the event is durably recorded and fanned out, but nothing
has been delivered. Claiming otherwise would be a lie the producer might act on.

```json
{ "eventId": "…", "deliveryCount": 3, "duplicate": false }
```

Ingest **never dispatches inline**. It writes the event and its delivery rows in a single
transaction and returns, which keeps producer latency independent of how slow, or how numerous,
its subscribers are.

### Not yet implemented

| Method | Route | Phase |
|---|---|---|
| `GET` | `/api/v1/deliveries?status=&subscriptionId=&from=&to=` | 4 |
| `GET` | `/api/v1/deliveries/{id}` | 4 |
| `POST` | `/api/v1/deliveries/{id}/replay` | 4 |
| — | `/hubs/deliveries` (SignalR) | 4 |

---

## Idempotent ingest

Send an `Idempotency-Key` header to make producer retries safe. Three paths:

1. **Key seen within 24h** → returns the original event id, creates nothing.
2. **Key seen outside 24h** → the old event **releases** the key (set to null) and keeps
   existing, so the new request is genuinely new. Without this step the unique index would
   reserve the key forever and the 24-hour window would silently become permanent.
3. **Two requests racing with the same key** → both read nothing, both insert, and the partial
   unique index picks a winner. The loser catches the `23505`, re-reads, and returns the
   winner's id rather than failing the producer.

**The index is the arbiter, not the read-then-write.** This is verified: 32 concurrent requests
with one key produce exactly one event, with 11 losers logging the race-recovery path.

---

## The SSRF guard

Subscriber URLs are **attacker-supplied by design**. Anyone who can register a subscription can
make this server issue an HTTP request of their choosing and — because attempts record response
bodies — read the response back. That turns the delivery worker into a proxy sitting inside the
trust boundary, with reach into the cloud metadata service and anything else protected only by
being "internal".

The guard resolves the hostname and refuses the URL if **any** resolved address is in a blocked
range. The blocklist is data (`BlockedAddressRanges`), not branching.

### Blocked ranges

**IPv4:** `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`,
`172.16.0.0/12`, `192.0.0.0/24`, `192.0.2.0/24`, `192.168.0.0/16`, `198.18.0.0/15`,
`198.51.100.0/24`, `203.0.113.0/24`, `224.0.0.0/4`, `240.0.0.0/4`

**IPv6:** `::/128`, `::1/128`, `64:ff9b::/96`, `100::/64`, `2001:db8::/32`, `fc00::/7`,
`fe80::/10`, `ff00::/8`

Wider than "loopback, link-local, RFC1918" on purpose — each extra entry closes a real
technique. `::ffff:127.0.0.1` is unwrapped before matching, because otherwise it misses every
IPv4 rule (not an IPv4 address) *and* every IPv6 rule (not inside a blocked v6 range) while the
network stack connects it to localhost.

Also refused: non-HTTP schemes, and credentials in the URL — the host of
`http://expected.test@169.254.169.254/` *is* the metadata address, but a human skim-reading the
registration sees `expected.test`.

### Why registration-time validation alone is insufficient

1. **DNS is not a constant.** Register a name pointing at a real public server, pass the check,
   then repoint the A record at `169.254.169.254`. Nothing about the stored subscription
   changed, so nothing re-triggers validation.
2. **Redirects are a second, unvalidated URL.** A URL that passes can answer
   `302 Location: http://169.254.169.254/`. With auto-redirect on, the request you validated is
   not the request that gets made.
3. **There is a TOCTOU gap between validate and connect.** Even validating immediately before
   dispatch, the guard resolves DNS, approves the addresses, then hands the *hostname* to
   `HttpClient`, which resolves it **again**. A short-TTL resolver alternating records gives a
   public address to the validator and a private one to the connection.

`IOutboundUrlValidator` therefore has two methods: `ValidateAsync(Uri)` for registration and
pre-dispatch, and `IsAddressAllowed(IPAddress, host)` for the connect-time callback — the only
check that sees the address the socket will actually use.

### ⚠️ Enforcement status

| Call site | Status |
|---|---|
| Subscription registration (`POST`) | ✅ Enforced |
| Subscription update (`PATCH`, when the URL changes) | ✅ Enforced |
| Before every dispatch | ❌ **Phase 3** |
| After every redirect hop | ❌ **Phase 3** |
| At socket connect time (`ConnectCallback`) | ❌ **Phase 3** |

The last three live in the HTTP dispatch pipeline, which does not exist yet. The validator API
is already shaped for all of them; nothing needs redesigning.

---

## Configuration

`appsettings.json`:

```json
{
  "ConnectionStrings": { "Hooksmith": "Host=localhost;Port=5432;Database=hooksmith;Username=hooksmith;Password=hooksmith" },
  "Database": { "MigrateOnStartup": false },
  "Cors": { "AllowedOrigins": [] },
  "Ssrf": {
    "AllowLoopback": false,
    "AllowPrivateNetworks": false,
    "RequireHttps": false,
    "AllowedHosts": [],
    "MaxRedirects": 3
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `Database:MigrateOnStartup` | `false` | `true` in Development. Production should run `dotnet ef database update` as its own step, so a failed schema change is not also a failed rollout. |
| `Ssrf:AllowLoopback` | `false` | **Development only.** Lets you point a subscription at a receiver on your own machine. |
| `Ssrf:AllowPrivateNetworks` | `false` | Opens RFC1918 and IPv6 unique-local only. **Never opens link-local** — nobody's receiver lives on `169.254.169.254`. |
| `Ssrf:RequireHttps` | `false` | Worth turning on for an internet-facing deployment. |
| `Ssrf:AllowedHosts` | `[]` | A genuine hole in the guard, which is why it is an explicit list of names rather than a pattern. Intended for container-network service names. |
| `Ssrf:MaxRedirects` | `3` | Declared; **consumed in Phase 3**. |

Every relaxation defaults to the safe value, so a production deployment that says nothing gets
the strict policy. `appsettings.Development.json` enables loopback, private networks,
migrate-on-startup, CORS for `http://localhost:4200`, and EF command logging.

---

## Database schema

Four tables: `subscriptions`, `events`, `deliveries`, `attempts`. Created by migration
`InitialSchema` — **migrations, never `EnsureCreated`**, which would build the current model
with no migration history and leave the database unable to accept any later change.

Indexes that matter:

| Index | Why |
|---|---|
| `ix_deliveries_status_next_attempt_at` on `(status, next_attempt_at)` | The Phase 3 worker's hot query, run on every poll by every instance. Without it, that is a sequential scan of a delivery history that grows without bound. |
| `ix_subscriptions_event_types` **GIN** on `event_types` | Fan-out is `event_types @> ARRAY[$1]`. Verified using the index at 50k rows via `EXPLAIN ANALYZE`. |
| `ux_events_idempotency_key` **UNIQUE PARTIAL** `WHERE idempotency_key IS NOT NULL` | The actual arbiter of idempotency. |

Column choices worth defending:

- `deliveries.status` is **text, not the enum's integer**. This is an operations tool; being able
  to type `where status = 'Retrying'` in psql at 3am beats the bytes saved.
- `events.payload` is **jsonb** — parsed on write, so malformed JSON is rejected at the database.
- `subscriptions.event_types` is a **native `text[]`**, which is what makes the GIN index
  possible.

---

## Testing and verification

```bash
dotnet test Hooksmith.slnx
```

**175 unit tests, ~190 ms, zero sleeps.** Coverage:

- **Transition table** — all 25 status pairs, legal and illegal, enumerated rather than
  hand-picked.
- **Retry policy** — bounds across all attempt numbers, monotonicity, and jitter *spread*.
- **Signature** — round-trip, tampered body, single flipped byte, wrong secret, timestamp
  rewrite, unmodified replay, future timestamp, forged signature reporting mismatch rather than
  expiry, 15 malformed-header cases, unicode bodies.
- **SSRF guard** — table-driven over 25 hostile URLs, each a distinct technique.
- **Truncation** — UTF-8 byte counting and surrogate-pair safety.

### Verified against real Postgres

Phase 2 was additionally exercised against Postgres 17 with the API running:

- 22/22 functional checks — registration, SSRF rejection, fan-out, idempotency, PATCH
  re-validation.
- **GIN index confirmed in use** at 50,000 subscriptions:
  `Bitmap Index Scan on ix_subscriptions_event_types`, `Index Cond: (event_types @> '{…}')`.
- **EF's generated SQL confirmed** from the command log, not assumed:
  `WHERE s.is_active AND s.event_types @> ARRAY[@normalisedType]::text[]`.
- **Idempotency race confirmed**: 32 concurrent requests, one key, exactly one event, 11 losers
  taking the unique-violation recovery path.
- **Cross-language signature interop confirmed**: the TypeScript snippet in this README verifies
  signatures produced by the C# `WebhookSigner`, across 10 cases including a unicode body.

> A note on that last one, because it nearly went wrong. The first attempt showed every loser
> taking the *read* path — the right outcome for the wrong reason. PowerShell 5.1 runs on .NET
> Framework, where `DefaultConnectionLimit` is 2, so the requests queued two at a time and the
> first committed before the rest read. A concurrency test that does not achieve concurrency
> passes while proving nothing.

### Not covered by tests

There is **no automated integration test suite yet** — the Postgres verification above was
manual. Testcontainers-based tests are Phase 6, and are the right place for the fan-out,
retry-count, dead-letter and double-dispatch scenarios.

---

## What is not implemented

### Phase 3 — Delivery worker ❌

Nothing dispatches. Specifically missing:

- The `BackgroundService` host inside `Hooksmith.Api`.
- Claiming due deliveries with `SELECT … FOR UPDATE SKIP LOCKED`, so multiple instances do not
  double-send.
- Bounded parallelism via `System.Threading.Channels` — one producer reading due deliveries, N
  consumers dispatching.
- `IHttpClientFactory` named client, 10s timeout, **no Polly retry policy** (retries are the
  state machine's job; layering Polly on top double-counts attempts).
- Outcome classification: `2xx` → `Succeeded`; `408/429/5xx` → `Retrying`; other `4xx` →
  `DeadLettered` immediately, because a 400 from a subscriber will still be a 400 in thirty
  seconds.
- Writing `Attempt` records with status code, truncated body, duration and error.
- Honouring `Retry-After` on 429 in preference to the computed backoff.
- The three remaining SSRF call sites (per-dispatch, per-redirect, connect-time).
- Actually attaching `X-Hooksmith-Signature` to an outbound request. **The signing code exists
  and is tested, but nothing calls it yet.**

### Phase 4 — Query, replay, live feed ❌

- `GET /api/v1/deliveries` with status/subscription/date filters and pagination.
- `GET /api/v1/deliveries/{id}` with the attempt timeline.
- `POST /api/v1/deliveries/{id}/replay` — creating a *new* delivery for the same event so the
  original attempt history stays intact, legal only from `DeadLettered` or `Succeeded`.
- SignalR hub at `/hubs/deliveries`, with groups keyed by subscription id.

### Phase 5 — Angular dashboard ❌

The `hooksmith-ui` directory does not exist. No subscription editor, delivery list, delivery
detail, or live feed panel.

### Phase 6 — Tests and packaging ❌

- Testcontainers integration tests (fan-out; fail-twice-then-succeed producing exactly 3
  attempts; always-500 dead-lettering after 6; two workers dispatching one delivery exactly
  once).
- `docker-compose.yml` currently starts **only Postgres** — the API service is not in it.
- No GitHub Actions workflow.

---

## Known gaps and deliberate decisions

**The `DeadLettered → Pending` edge is currently unreachable.** It is implemented as specified
(`Delivery.RestoreForReplay`), but Phase 4's replay creates a *new* delivery rather than
transitioning the old one, so nothing will use this edge. It is either dead weight to remove or
a second "requeue in place" operation to expose — a decision for Phase 4.

**`decimal` has nothing to apply to yet.** Nothing in the domain is money-shaped; payloads are
opaque `JsonDocument`. The constraint stands for when something is.

**Secret masking is hygiene, not a security boundary.** Authentication is out of scope, so
anyone who can reach the API can create a subscription and read its secret at creation. Masking
still keeps secrets out of list views, browser history and screenshots.

**`DELETE` on a subscription destroys its delivery history** via `ON DELETE CASCADE`. Deliberate,
and the reason `PATCH isActive=false` exists.

**Event types are lowercased on the way in**, so `Event.Type` is not byte-identical to what the
producer sent. This is what makes fan-out an indexed lookup.

**PATCH cannot distinguish "absent" from "explicitly null".** Acceptable because no field on a
subscription is legitimately nullable, so "null means leave it alone" loses no expressiveness.

**Two analyzer suppressions**, both documented in `.editorconfig`: CA1716 for the `Event` domain
type (collides with a Visual Basic keyword this solution will never be consumed from), and
CA1707 in the test project only (underscored test names are the sentence the runner prints on
failure). EF's generated migrations are marked `generated_code = true` rather than hand-edited
to satisfy style rules.

**`Microsoft.AspNetCore.OpenApi` was removed** from the Web API template. Its transitive
`Microsoft.OpenApi 2.0.0` carries GHSA-v5pm-xwqc-g5wc, and pinning the fixed 3.x forward breaks
the 2.x surface the 10.0 host binds against. An OpenAPI document is not in the spec, so the
dependency came out rather than shipping a known-vulnerable package.

---

## Out of scope

Not built, and not planned: auth/multi-tenancy, per-subscriber rate limiting, a Kafka or RabbitMQ
transport, Kubernetes manifests, an admin RBAC model, and metrics beyond basic logging.

The delivery worker is deliberately hosted **inside `Hooksmith.Api`** as a `BackgroundService`,
not split into a separate deployable.
