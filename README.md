# Factory Traffic System


A small event-driven control system for the internal roads of a garment factory (Junction A, extensible to B, C, D).
ASP.NET Core 8 · SQL Server · plain HTML/JS dashboard.

> **How to read this repo:** the hard part  is *safe behaviour under concurrency and failure*, not data storage.
> So the traffic rules live in a pure, deterministic Domain engine with its own tests; everything else (HTTP, SQL, controller transport, UI) is a replaceable adapter around it.

---

## 1. Quick start

**Prerequisites:** .NET 8 SDK, SQL Server (LocalDB or full SQL Server).

1. Open `src/TrafficControl.Api/appsettings.json` and set `ConnectionStrings:Traffic` for your SQL Server.
   The default targets LocalDB: `Server=localhost\\SQLEXPRESS;Database=FactoryTraffic;Trusted_Connection=True;TrustServerCertificate=True`.
2. Run the API project .
   On first start the app creates the database and tables and seeds **Junction A**. No manual SQL is needed.
3. Open the URL printed on startup:
   - `/` → the dashboard
   - `/swagger` → interactive API documentation

Run the tests: `dotnet test` (or Test Explorer). They need no database, no browser and no HTTP.

---

## 2. Solution structure

```
FactoryTraffic.sln
├─ src/TrafficControl.Domain          Pure traffic engine. No I/O, no clock, no threads. References nothing.
├─ src/TrafficControl.Application     Use cases + PORTS (IClock, IControllerGateway, IJunctionStore), per-junction actor
├─ src/TrafficControl.Infrastructure  Adapters: EF Core/SQL Server store, simulated controller (REST), clock
├─ src/TrafficControl.Api             Controllers, validation, error mapping, background services, dashboard (wwwroot)
├─ tests/TrafficControl.Domain.Tests  22 xUnit tests + simulation harness
├─ database/schema.sql                Table definitions (also created automatically at startup)
└─ docs/                              api-requests.http, concurrency-demo.ps1
```

```
  Browser / sensors / controllers (REST today, MQTT later)
                 │
        API layer (controllers, validation, status codes)
                 │
        Application layer: TrafficService ──► one JunctionActor per junction (serialised queue)
                 │                                   │
        ┌────────┴─────────┐                         ▼
   IJunctionStore     IControllerGateway     Domain: JunctionEngine (state machine, pure)
   (SQL Server)       (REST simulator / MQTT)
```

Dependencies point inward only: Domain knows nothing; Application knows Domain; Infrastructure and Api know Application.
The engine never sees HTTP, SQL, MQTT or the frontend.

---

## 3. Major architectural decisions

| Decision | Why | Alternative rejected |
|---|---|---|
| **Pure domain engine: `Handle(message, now) → effects`** | Deterministic and testable without HTTP/DB/hardware. A full 60 s cycle runs in milliseconds with a fake clock. | Logic inside services that call a repository: needs a DB or heavy mocks to test, rules scatter across layers. |
| **One actor per junction (`Channel<T>`, single reader)** | All inputs (sensor events, commands, ACKs, timer ticks, even reads) become messages processed one at a time. Two requests can never run engine code concurrently, so the engine needs no locks. | DB locking (safety rules leak into SQL), optimistic concurrency (commands fail under load, retry storms). |
| **Signals only change inside the stage machine** | Manual/emergency/sensor input only changes the *target phase*. The engine alone walks GREEN → YELLOW → ALL RED → GREEN. Manual/emergency therefore **cannot** bypass the safe sequence. | Letting API code write signal states. |
| **GREEN only after the controller confirms ALL RED** | "Sent" never means "executed". A phase may turn green only when the *actual* (confirmed) state shows all red. | Timer-only transitions (unsafe if a controller missed a command). |
| **Desired state and actual state stored separately** | Required by the spec, and it is the basis of mismatch detection and degraded mode. | A single `signalState`. |
| **Write-ahead persistence** | For every state change: engine → save snapshot + audit rows in **one transaction** → only then send the command to the controller. | Send first, save later (a crash would lose what the controller was told). |
| **State stored as one JSON snapshot per junction + append-only audit table** | Engine state is one consistent unit (queues, signals, mode, pending command, processed event ids). One row = atomic save and trivial recovery. | A table per concept (many writes per message, partial-write risk). |
| **Queues hold vehicle IDs, not counters** | Duplicate or repeated events are harmless, and the count can never go negative. | `int queue++ / queue--`. |
| **Ports for the controller and the store** | MQTT can replace/supplement the REST simulator by implementing `IControllerGateway`; the engine does not change. | Direct HTTP/MQTT calls inside traffic logic. |
| **Polling every second for the dashboard** | Simple, robust, nothing to reconnect after a restart, easy to explain. SSE/WebSocket would be the next step (see §14). | SSE/WebSocket (more moving parts for a demo dashboard). |
| **Time is injected (`now`)** | No `Thread.Sleep` anywhere; timing is driven by a 500 ms background ticker and unit tests control time exactly. | `Task.Delay` inside request handlers. |

---

## 4. Traffic-state transitions

Phases: `NORTH_SOUTH` and `EAST_WEST`. Any two different phases conflict.

```
                 ┌─────────────────────────── target phase changes (auto / manual / emergency) ───────────────────────────┐
                 ▼                                                                                                         │
   GREEN(P) ──► YELLOW(P) ──(5 s)──► ALL_RED ──(≥2 s AND controller confirmed all red)──► GREEN(Q) ───────────────────────┘
```

Rules enforced in code (and covered by tests):

1. Conflicting phases never receive GREEN at the same time. After every message the engine asserts this; a violation throws `SafetyViolationException` instead of returning an unsafe state.
2. A GREEN phase never goes directly to a conflicting GREEN. A GREEN command may only follow an ALL RED command (checked by the random-storm test).
3. YELLOW is never skipped or aborted, even if the wish changes mid-transition. At ALL RED the engine re-reads the newest wish, which is safe because everything is red.
4. Manual and emergency requests only name a target phase, they never write signals.
5. Invalid or unexpected commands are rejected (400/409/422) and leave state untouched.

### Operating modes

| Mode | Meaning | Leaves when |
|---|---|---|
| `AUTOMATIC` | Scheduler chooses phases | - |
| `MANUAL` | An admin pinned a phase | `RETURN_TO_AUTOMATIC`, 5 min timeout, or an emergency |
| `EMERGENCY` | An emergency vehicle is being served | The emergency vehicle clears, or 60 s without updates |
| `DEGRADED` | Controller/signal not trusted | The controller confirms a clean ALL RED |

---

## 5. Traffic-control algorithm (automatic mode)

**Score of a phase** = sum over waiting vehicles of `type weight + 0.1 × seconds waited`.
Weights: EMERGENCY 100, TRUCK 5, FORKLIFT 3, EMPLOYEE_VEHICLE 1 (all in `JunctionConfig`, not hard-coded in logic).

While a phase is green, the engine decides whether to switch:

1. Before `MinGreen` (10 s): hold. This avoids flip-flopping.
2. Nobody is waiting on the other phase: hold. This avoids needless switching.
3. **Starvation guard:** someone on the other phase has waited ≥ `MaxWait` (60 s) → switch, whatever the scores.
4. The current phase has nobody waiting (green is wasted) → switch.
5. Before `GreenDuration` (30 s): switch only if the other score is **1.5× higher** (hysteresis).
6. Between 30 s and `MaxGreen` (60 s): switch unless the current phase is still **1.5× heavier** (green extension).
7. At `MaxGreen` (60 s): switch.

Why it behaves well: bigger queues and higher-priority vehicles win scheduling, waiting time slowly raises everyone's score,
and the starvation guard gives a hard upper bound so employee vehicles can never wait indefinitely.

A sensor that is offline contributes a fixed assumed demand, so its direction is still served (fixed-time fallback).

---

## 6. Concurrency strategy

`JunctionActor` owns one junction's engine and a single in-memory queue. Every operation is a message:
sensor event, admin command, controller ACK, device status, 500 ms tick, and even status reads (so a read never sees a half-finished update).
One loop processes one message at a time. Callers `await` a result through a `TaskCompletionSource`.

Consequence for the PDF's example (truck at 0 ms, emergency at 4 ms, manual at 8 ms, duplicate emergency at 12 ms, ACK at 17 ms):
whatever order the web server delivers them in, they are processed strictly one after another, so there is no race and no conflicting GREEN.
`docs/concurrency-demo.ps1` fires exactly this sequence.

Different junctions have different actors and run in parallel.
If the app is ever scaled to several instances, `RowVersion` on the `Junctions` table makes a stale writer fail (optimistic concurrency) rather than corrupt state.

---

## 7. Failure handling and recovery

**Command lifecycle:** every command has a unique `command_id`.
`Pending → Acknowledged | Failed | TimedOut | Superseded | Unknown`.

| Situation | Behaviour |
|---|---|
| ACK received | Actual state updated from the ACK; mismatch with desired is detected |
| ACK delayed | Waits up to 5 s |
| ACK never received | Retries twice with the **same** `command_id` (a late first ACK still correlates). Then **DEGRADED**: desired = all red, actual = UNKNOWN, no GREEN is ever issued |
| Duplicate / late ACK | Ignored (200 DUPLICATE), audited |
| ACK contradicts the request (desired GREEN, actual RED) | Mismatch alert, retried, then degraded |
| ACK reports conflicting physical lamps | Immediately degraded (the controller is external input, so it degrades instead of throwing) |
| Controller OFFLINE | Degraded, no commands sent (nobody to talk to) |
| Controller reconnects (ONLINE) | Engine requests a confirmed ALL RED, then resumes normal operation |
| Degraded and silent | Probes with an ALL RED command every 15 s |
| Sensor OFFLINE | Alert; the direction gets assumed demand |
| Signal lamp failure | Degraded; recovery is blocked until the signal is reported ONLINE |

**Restart recovery** (`TrafficService.InitializeAsync` → `JunctionEngine.Start`):
the app never trusts what was requested before it stopped. On every start:
1. Junctions, queues, processed event ids, emergencies, manual state and history are loaded from SQL Server.
2. The pending command is marked `Unknown`; actual signal state becomes `UNKNOWN`.
3. The stage is forced to ALL RED and an ALL RED command is sent.
4. GREEN is issued only after that ALL RED is confirmed. This is true whether the crash happened during GREEN, YELLOW, ALL RED, or while waiting for an ACK.

**Persisted:** config, queue state, processed event ids, mode, desired/actual signals, emergency and manual state, pending command history, timestamps, audit log.
**Rebuilt / discarded:** in-memory actors, the controller outbox, in-flight timers (they restart from the recovery step).

---

## 8. Sensor event semantics

| Question | Decision |
|---|---|
| Which field makes an event unique? | `event_id` is authoritative for idempotency. Repeats return 200 `DUPLICATE`. |
| What is `sequence_no` for? | Detecting replays and out-of-order delivery (audited as `SEQUENCE_ANOMALY`). It is **not** used to drop events, because queue updates are idempotent per vehicle. |
| Which timestamp is authoritative? | Sensor time only measures *waiting time* (clamped so it cannot be in the future) and rejects absurd events. Server time orders emergencies and drives all timers, because sensor clocks drift. |
| Delayed events | Older than 10 min → 422 rejected and audited (dead-lettered). Otherwise applied. |
| Timestamps from the future | More than 1 min ahead → 422. |
| `VEHICLE_CLEARED` without arrival | Accepted, queue unchanged, a *tombstone* is stored so a late `ARRIVED` for that vehicle is ignored. |
| Same vehicle, different `event_id` | Still one vehicle in the queue. |
| Unknown junction / vehicle type / malformed fields | 404 / 400 / 400 with field-level messages. |

---

## 9. Database

SQL Server via EF Core. Two tables (see `database/schema.sql`):

- `Junctions(Id, Name, ConfigJson, StateJson, Version, UpdatedAt, RowVersion)`
- `AuditLog(Id, JunctionId, EventType, Direction, PreviousState, NewState, CommandId, Message, Timestamp)`

At startup `EnsureCreated()` creates them. To use real EF migrations instead:

```
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate -p src/TrafficControl.Infrastructure -s src/TrafficControl.Api
dotnet ef database update -p src/TrafficControl.Infrastructure -s src/TrafficControl.Api
```
then replace `EnsureCreatedAsync()` in `Program.cs` with `MigrateAsync()`.

---

## 10. API documentation

Interactive docs: `/swagger`. Ready-made requests: `docs/api-requests.http`. JSON is snake_case, enum values are UPPER_SNAKE_CASE like the PDF.

| Method & path | Purpose | Success | Errors |
|---|---|---|---|
| `GET /api/junctions` | All junctions with live status | 200 | |
| `GET /api/junctions/{id}` | Config (phases, timings) + status | 200 | 404 |
| `POST /api/junctions` | Create a junction (default two-phase plan) | 201 | 400, 409 |
| `GET /api/junctions/{id}/status` | Desired/actual signals, queues, mode, phase, alerts, pending command | 200 | 404 |
| `POST /api/sensor-events` | Vehicle arrived / cleared | 200 `ACCEPTED` or `DUPLICATE` | 400 malformed, 404 unknown junction, 422 rejected |
| `POST /api/junctions/{id}/commands` | `MANUAL_GREEN_REQUEST` (+`direction`), `RETURN_TO_AUTOMATIC` | 202 | 400, 404, 409 (emergency/degraded active) |
| `POST /api/controller-events` | Controller ACK/NACK, optionally with `actual_state` | 200 | 400, 404, 422 (unknown command) |
| `POST /api/device-status` | Controller / signal / sensor ONLINE, OFFLINE, ... | 200 | 400, 404 |
| `GET /api/junctions/{id}/history?limit=50` | Audit trail, newest first | 200 | 404 |
| `GET/POST /api/simulator[/auto-ack]` | Demo helper: stop/start automatic ACKs | 200 | |

**Changes to the PDF's API (and why):**
- Added `POST /api/device-status`: the PDF defines a status event (`status-301`) but no endpoint for it.
- Added `/api/simulator/auto-ack`: needed to demonstrate "ACK never received" without hardware.
- Commands are **junction-level** (one command carries all four signals) instead of one per direction, so a phase change is atomic. ACK `actual_state` therefore accepts an optional `direction` (see assumptions).
- `GET /api/junctions` returns live status so the overview needs one call.

---

## 11. Demonstrating the scenarios (dashboard at `/`)

The dashboard's *Traffic simulation* card sends the events. Start the app and wait ~3 s: Junction A turns green on NORTH/SOUTH.

1. **Normal traffic:** send a few arrivals (NORTH, EAST, WEST, type EMPLOYEE_VEHICLE, new vehicle ids). Watch the queues and the phase change after the minimum green.
2. **Priority traffic:** 3 × EMPLOYEE_VEHICLE on NORTH, then 1 × TRUCK on EAST. Within roughly 10-15 s the junction switches to EAST/WEST because the truck outweighs the employee cars.
3. **Emergency preemption:** while NORTH/SOUTH is green, send `EMERGENCY` from EAST. The banner appears and the stage shows YELLOW → ALL RED → GREEN for EAST/WEST. EAST does **not** turn green immediately.
4. **Manual override:** *Request manual green* for WEST, watch the safe sequence and the MANUAL banner, then *Return to automatic*.
5. **Duplicate event:** send an arrival, then *Re-send last event (duplicate)*. Notice: `DUPLICATE`, the queue does not grow, and the history shows `DUPLICATE_EVENT_REJECTED`.
6. **Vehicle clearance:** send an arrival, then *Vehicle cleared* (or the *clear* button in the queue). The queue shrinks. Clearing an unknown vehicle never makes it negative.
7. **Controller failure:** untick *Simulated controller auto-ACKs*. After a few seconds you see the timeout, retries, then the DEGRADED banner. Signals are held at ALL RED and no GREEN is issued. Re-tick auto-ACK and send controller status `ONLINE`: the junction recovers through a confirmed ALL RED. Alternatively send controller `OFFLINE`, or use *Send for pending command* with actual state `RED` to trigger a mismatch.
8. **Restart:** add some vehicles, stop the app (Ctrl+C), start it again. Queues and history are still there, and the log and history show `RECOVERY`: the junction goes to ALL RED first, then resumes.
9. **Concurrent events:** run `docs/concurrency-demo.ps1` and read the status: no conflicting GREENs, the duplicate emergency returns `DUPLICATE`.

---

## 12. Tests

`tests/TrafficControl.Domain.Tests` - 22 tests, no database, no HTTP, no sleeping (fake clock + fake controller in `Sim.cs`):

- startup forces ALL RED before any GREEN; GREEN is never requested without a confirmed ALL RED
- **random event storm** (4,000 mixed events): no unsafe command sequence, no conflicting GREEN
- duplicate event, repeated vehicle, clear / orphan clear / out-of-order clear, invalid / stale / future events
- emergency preemption goes through YELLOW and ALL RED; competing emergencies are first-come-first-served; stale emergency times out
- manual uses the safe sequence, expires, is refused during emergency; emergency overrides manual
- starvation guard vs. heavy opposite queue; no needless switching
- duplicate ACK ignored; contradictory ACK degrades; controller offline → online recovers via confirmed ALL RED
- restart recovery does not trust old physical state; queue and processed event ids survive a snapshot round trip

---

## 13. Assumptions / Questions / Requirement Issues

| # | Issue | Type | Decision |
|---|---|---|---|
| 1 | "Emergency vehicles should immediately begin preemption" vs. "never bypass the safe sequence" | Contradictory | Preemption starts immediately but always goes YELLOW → ALL RED → GREEN. Minimum green is skipped for emergencies, yellow and all-red never are. |
| 2 | What counts as a conflicting movement? | Missing | Any two different phases conflict. No left/right turn or pedestrian movements are modelled. The phase plan is configuration, so it can be extended. |
| 3 | Is `event_id`, `sequence_no`, or the timestamp authoritative? | Unclear | `event_id` for idempotency, `sequence_no` for anomaly detection, sensor time for waiting time and freshness, server time for ordering emergencies and for all timers (see §8). |
| 4 | `VEHICLE_CLEARED` without an arrival | Missing | Accepted; tombstone prevents a late arrival from re-queueing the vehicle; queue never negative. |
| 5 | Delayed / future events | Unclear | > 10 min old or > 1 min in the future are rejected with 422 and audited. |
| 6 | The spec says vehicles may be "cleared", but nothing says who sends the clearance | Missing | Assumed sensors send `VEHICLE_CLEARED`. A vehicle stuck in a queue would otherwise block forever, so stale **emergencies** expire after 60 s (and their queue entry is removed). Non-emergency stale vehicles are not expired (documented limitation). |
| 7 | Higher priority gets "faster clearance" vs. "no starvation" | Contradictory | Priority drives scoring and green extension (up to 60 s); the starvation guard (60 s max wait) always wins. |
| 8 | How long does manual control last? | Missing | 5 minutes (configurable), then automatic. This also covers an administrator who disconnects. |
| 9 | Two administrators at once | Missing | Commands are serialised by the actor; the last command wins and the earlier request is mentioned in the audit entry. |
| 10 | Does emergency override manual? | Missing | Yes. Manual is cancelled and must be re-issued. Manual requests during an emergency get 409. |
| 11 | Multiple / conflicting emergencies | Missing | First come, first served by server time. Same-phase emergencies share a green. The next one is served when the first clears. Repeated sightings refresh a 60 s timeout. Emergencies during DEGRADED are recorded but cannot be served. |
| 12 | When is an emergency cleared? | Missing | On `VEHICLE_CLEARED` for that vehicle, or after 60 s without a new sighting. |
| 13 | ACK timeout, retry, duplicate ACK | Missing | 5 s timeout, 2 retries with the same `command_id`, then DEGRADED. Duplicate/late ACKs are ignored. |
| 14 | Commands are per direction in the PDF, but a phase change needs several directions to change together | Technically problematic | One command carries all four signals (atomic phase change). ACK `actual_state` without `direction` applies to the directions the command asked to be non-RED (or all, for an all-red command); `actual_signals` gives a full report. |
| 15 | The ACK example has no direction and a single `actual_state` | Unclear | See #14. |
| 16 | An unacknowledged YELLOW | Unsafe | We do not require a YELLOW ACK (the yellow phase is time-based), but GREEN is only issued after a **confirmed** ALL RED, which covers a missed YELLOW. |
| 17 | What does the controller do if the backend dies? | Missing / out of scope | Real controllers should have their own hardware fail-safe (flashing red). The backend can only ask for ALL RED on restart. Documented as a real-world requirement. |
| 18 | Status events (`status-301`) have no endpoint and no `command_id` | Missing | Added `POST /api/device-status`. Controller reconnect triggers a confirmed ALL RED before resuming. |
| 19 | Manual control has no authentication | Unsafe | The spec says auth is not required. Anyone who can reach the API can request manual green. It is still safe (cannot bypass transitions) but should be protected before production. Listed under next steps. |
| 20 | Offline sensor | Missing | Alert plus assumed demand for that direction (fixed-time fallback). |
| 21 | Phase plan hard-coded? | Spec concern | Phases, timings and weights are per-junction configuration (`JunctionConfig`). New junctions reuse the same engine. `POST /api/junctions` currently creates the default two-phase plan; custom plans would need an API field. |
| 22 | Processed event ids kept in the snapshot | Technical trade-off | Capped at 10,000 ids per junction (oldest dropped). A table with a unique index would scale better (next steps). |
| 23 | `EnsureCreated` vs. migrations | Technical trade-off | `EnsureCreated` for zero-setup demos; migration commands are in §9. |

---

## 14. Known limitations and next steps

Not implemented because of the time limit (in priority order):

1. **MQTT adapter** implementing `IControllerGateway` (+ an MQTT subscriber calling `TrafficService.ProcessControllerAckAsync`). The port exists, so the engine does not change.
2. **Authentication / authorization** for manual commands (role-based, plus an `admin` identity in the audit log).
3. **SSE or WebSocket** live updates instead of 1 s polling.
4. **Processed event ids in a table** with a unique index (second dedupe layer, no 10,000 cap).
5. **EF migrations** committed to the repo, Docker / Docker Compose, CI.
6. API integration tests (`WebApplicationFactory`) and per-adapter tests.
7. Custom phase plans per junction through the API, left/right turns and pedestrians as movements.
8. Metrics (queue length, wait time, degraded duration), event replay from the audit log, dead-letter table for rejected events.
9. Expiring stale non-emergency vehicles that never send `VEHICLE_CLEARED`.
10. A dashboard multi-junction overview page (the API already supports it).

---

## 15. AI / Tool Usage

I used **Claude (Anthropic, claude.ai)** as an assistant for design discussion (architecture, safety rules, failure and recovery policies), for drafting code, and for drafting this README.
I created the projects and typed/pasted each file myself, ran the tests, and reviewed the code so that I can explain, debug and modify it.
Tools: Visual Studio, SQL Server, xUnit, Swagger UI.
