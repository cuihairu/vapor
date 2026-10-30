# Architecture implementation review — 2026-10-30

This is a source inspection of the checked-out implementation, not a new runtime,
coverage, or real Steam validation. The [overview SVG](../assets/architecture/overview.en.svg)
depicts current behavior; outstanding work is tracked as atomic items in
[`todo.md`](https://github.com/cuihairu/vapor/blob/main/todo.md), section “架构核对”.

## State and deployment boundaries

Vapor separates orchestration from Steam execution. One ASP.NET Core control-plane
process owns the SQLite stores; regional agents initiate outbound connections.
There is no external message broker, cross-instance lock, leader election, or
supported active/active control plane. Region is a routing attribute in this path;
the connected agent registry is rebuilt in memory, not a persistent region/agent database.

| Boundary | Implementation evidence | Meaning |
|---|---|---|
| Composition | `src/Vapor.ControlPlane/Program.cs` service registration | Job, audit, config, crawl, script/flow, webhook delivery stores; background schedulers and reconciler |
| Durable dispatch | `TaskSchedulerService.DispatchOnce`, `SqliteJobStore.ClaimNextQueuedTask`, `HeartbeatTask`, `SetTaskResult` | SQLite task rows are the queue of record; claim increments attempt, heartbeat renews lease, stale results are fenced |
| Agent placement | `AgentRegistry.Pick`, `HostTaskTarget`, `DesiredStateReconciler` | Region/capability/load matching; host actions route to `agent:{id}`; declarative account placement is a separate loop |
| Tunnel | Both `Program.cs` files; `Vapor.Protocol/Models.cs` | WebSocket carries hello, tasks, cancellation, results and heartbeats; agents connect to the CP |
| Session events / manual auth | `PublishSessionEventAsync`, `PollAuthChallengesAsync` in agent `Program.cs`; CP session/auth endpoints | Separate HTTP POST and SSE paths; the WebSocket is not the only inter-process connection |
| Execution | Agent `RunOnce`; `AgentTaskExecutor`; `BotSession.HandleExecuteAction` | One dispatched task awaited at a time per agent; long-lived account sessions coexist; each session serializes its actions |
| Extensions | Agent `LoadPluginsAsync`; `src/Vapor.Plugins.Core/`; `HostActions/` | Collectible ALC isolates assembly loading, not CPU, memory, or process failure; host actions bypass bot session lookup |
| Secrets | `FileCredentialStore`, `AgentTaskExecutor`, CP jobs/auth endpoints | Agent local store encrypts tokens; submitted task payloads and manual codes also pass through the CP |
| Optional services | Agent cache registration; `docker-compose.yml`; `NotificationService` | Memory cache by default, opt-in Redis, Prometheus/Grafana profile, configured webhook sinks |

The state loop is `spec → reconcile → job → task → agent → session → Steam →
result/event → observed state → reconcile`. API submissions, recurring jobs,
script flows, crawls and warehouse collection converge on the task model rather
than creating independent execution infrastructures.

## Delivery and recovery

The scheduler polls SQLite, claims queued tasks and hands them to an agent's
in-memory send channel. Heartbeats only begin when the agent starts consuming a
task. Lease expiry requeues work. Attempt fencing protects the recorded outcome;
it cannot undo a trade or other external effect already performed by an old attempt.
The existing action safety classes and retry caps reduce that exposure, without
providing exactly-once execution.

A control-plane restart preserves SQLite state but loses agent connections,
challenge prompts, live SSE subscriptions and mirrors. An agent restart preserves
its local credential file, but not in-flight results. There is no durable agent
result outbox. SSE subscriber channels hold 256 events and drop oldest on pressure;
they are live notifications, not replayable history. Webhook delivery records do
not turn the source event stream into a durable outbox.

## Confirmed discrepancies and gaps

1. **Secret boundary (high priority).** `POST /v1/jobs` passes the request to
   `CreateJob`; `InsertTasksAsync` JSON-serializes the payload into `payload_json`
   without secret filtering. `AgentTaskExecutor` accepts passwords, refresh/access
   tokens and auth codes from that payload. Agent encryption and audit redaction
   do not encrypt the control-plane task payload. The old “credentials never
   reach the control plane” claim therefore does not hold for this API path.
2. **Manual-code scope (high priority).** The auth SSE endpoint accepts any valid
   agent key; the optional account filter is caller-selected. Agents receive
   `code_provided_*` events with the raw code, while admins receive a projection
   with `Code = null`. There is no server-side binding from key identity to the
   account's assigned agent in this endpoint. Webhooks use `codeSupplied`, but
   that projection does not describe the agent channel.
3. **Queue pressure and leases (high priority).** `ConnectedAgent._send` uses a
   bounded 1,024-message channel with `DropOldest`, so successful enqueue is not
   proof all earlier tasks/cancels remain queued. The agent uses an unbounded
   incoming task channel and consumes tasks serially. Tasks waiting behind a long
   action have already been claimed but have no running heartbeat. Lease recovery
   can therefore redispatch them while an old dispatch is still queued.
4. **Queued cancellation (high priority).** The agent receiver cancels only the
   current `(taskId, attempt)`; it does not remove or invalidate an already queued
   task. Cancellation/fencing of the database record alone does not prevent that
   queued dispatch from later reaching an external action.
5. **Timeout containment (conditional follow-up).** `CancelAfter` requests
   cancellation; session `WaitAsync` bounds the caller's wait. A non-cooperative
   plugin may retain the session action lock or, for a host action, stall the
   serial executor. There is no process sandbox or forced thread termination.
   The prior process-boundary decision deliberately defers poisoned-session
   reclamation/resource quotas until third-party plugin demand warrants it.
6. **Unused event channels (maintenance).** `EventBroker` writes every session
   and auth event to `_globalSessionChannel` / `_globalAuthChannel`, both unbounded,
   but no reader exists in the class/interface. Notifications use separate
   `Subscribe*` channels. The unused channels retain data, including code events.
7. **Documentation drift.** The old architecture introduction recommends external
   MQ and active/active CP while later decisions reject them; the Chinese home
   page collapses Markdown blocks into one long line. This documentation change
   fixes those statements, restores the home page and embeds localized SVGs.
8. **Evidence boundary.** Mock inventory/case tests are complete; real Steam
   validation remains blocked on operator-provided credentials. Historical green
   test counts are not evidence that the failure cases above are covered.

## Scope decisions

HA, external MQ, tenant identities, KMS integration, durable event replay and a
plugin process sandbox are not silently added as implementation commitments.
Redis remains optional. Real Steam verification remains a distinct unchecked
item. Follow-up code work is recorded with explicit acceptance criteria in the
root TODO; this change itself updates documentation and SVG assets only.
