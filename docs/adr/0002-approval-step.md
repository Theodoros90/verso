# ADR 0002: Approval step (a run that waits for a person)

- Status: accepted (implemented 2026-11-12)
- Requested by the product owner on 2026-11-12: build the prototype's *Approval* step ("Pauses until someone approves") for real.

## Context

Every step so far finishes by itself. The prototype's Approval step stops the run until a person decides, then continues down **Approved** or **Declined**. That needs a state the run can wait in, a way for a person to decide, and a check of who may decide. The execution states in plan §6.1 had no waiting state.

## Decision

1. **New execution state `AwaitingApproval` and step state `Waiting`.** The run is *not finished* and *not locked*: no Worker holds it, the dispatcher ignores it (it only re-queues `Queued`/`Running`), and a redelivered job does nothing (`TryClaim` refuses it). Plan §6.1 and the `CK_Executions_Completed` check constraint (`CompletedAt` is null while awaiting) were updated; migration `AddApprovalWaiting` only replaces that constraint (no data change).
2. **Pause and resume use the existing engine.** The Worker marks the step `Waiting`, the execution `AwaitingApproval`, writes `StepStarted` and `ApprovalRequested` events and an audit entry, and stops. A decision (`POST /api/v1/executions/{id}/approvals/{nodeId}`) finishes the step by its `approved` or `rejected` output, puts the execution back to `Queued` and enqueues the job. The run resumes at the first unfinished step, exactly as a retry does, so nothing is re-run and loops resume in the right item (`iteration` selects the row).
3. **Who may decide: role-based.** The step's *Approver* setting names one of `operator`, `workflow-author`, `template-author`, `auditor` (mapped to the existing Verso roles); anyone holding that role may decide, and `Admin` always may. No new role, so no change to the Entra app roles. The API checks this in the service (the route is open to every reader), so a refusal is `403` and the run keeps waiting. The person who submitted the run may approve it (no four-eyes rule; see plan D7).
4. **Declined.** `rejected` is the Declined output. With nothing connected, a declined approval ends the run as **Rejected** (`approval.declined`), like the other data problems; with a path connected, the run continues down it.
5. **Escalation is advisory.** *Escalate after* (24 hours, 48 hours, 5 days) sets a due time stored on the request and shown as "Due by / Overdue". Nothing is sent, reassigned or timed out; a run may wait indefinitely (the 5-minute run limit counts active time only).
6. **Privacy.** The step result holds the approver role, due time, decision, who decided and the comment (max 500 characters, typed by the approver). The audit entry records the decision but not the comment. Neither holds payload values.

## Consequences

- API clients that list or filter executions must accept the new `AwaitingApproval` status.
- An approval cannot be withdrawn or reassigned in this version, and there is no inbox beyond filtering the execution list by *Awaiting approval*.
- Real e-mail or chat notifications stay out of scope (no integrations are approved).
