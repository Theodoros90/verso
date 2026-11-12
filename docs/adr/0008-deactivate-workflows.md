# ADR 0008: Deactivate workflows

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12 ("I should be able to deactivate the flows and remove"). A first version had two actions, Deactivate and Remove; the product owner then asked for **one action, called Deactivate**: it stops new runs and takes the workflow out of the main list, with "Show deactivated" to bring it back. The plan (§4) had reserved a `Retired` status that was never built; this is the smaller, one-flag version of that idea.

## Context

Workflows could only be created, edited and published. Demo and test workflows piled up in the list, and there was no way to stop a published workflow from accepting runs or scanning its folder. Deleting is not an option: executions, documents, archive records and audit entries refer to workflows and their versions, and published versions are immutable (plan §4; `tools/db/least-privilege.sql` denies DELETE on the `workflows` schema).

## Decision

1. **One flag on `Workflow`**: `IsActive` (default true). Versions are untouched, so the Draft/Published lifecycle and content hashes don't change. One additive migration (`AddWorkflowIsActive`); existing workflows stay active.
2. **Inactive** means: `POST /api/v1/workflows/{key}/executions` answers 409 `workflow.inactive`; the Worker's Folder input scan skips the workflow (files stay in the folder); the workflow is left out of the default list. The workflow can still be opened, edited and published (so a deactivated workflow can be prepared before it is activated), runs already started carry on, and Retry and Approval still work on existing runs. A file that was claimed just before the workflow was deactivated is moved to the failed folder with `workflow.inactive` in its error file, like any refused submission.
3. **Nothing is deleted.** Versions, runs, documents, archive and audit stay, and the key stays reserved (`workflow.key_taken`).
4. **API.** `POST /api/v1/workflows/{id}/deactivate` and `…/activate` (WorkflowAuthoring), each returning the `Workflow`; repeating one is a no-op (200, no second audit entry). `GET /api/v1/workflows` lists the active workflows; `?inactive=true` lists the deactivated ones. `WorkflowSummary` and `Workflow` gain `isActive`. Errors: 404 `workflow.not_found`; 409 `workflow.inactive` (submission).
5. **Audit.** `WorkflowDeactivated` and `WorkflowActivated` (`entityType=Workflow`), written in the same save as the change.
6. **UI.** Each card has a "⋯" menu: *Deactivate…* (asks first) or, in the deactivated list, *Activate*. "Show deactivated" / "Show active list" switches the list; cards of deactivated workflows carry an Inactive pill. The designer toolbar has *Deactivate* (asks, then returns to the list) or, for an inactive workflow, an Inactive pill and *Activate*.

## Consequences

- There is no permanent delete. A mistaken or obsolete workflow is hidden, not erased, and its key can't be reused. Permanent deletion of never-run workflows could be added later as a deliberate decision, because the database login can't delete.
- Deactivation is not a schedule or maintenance window: nothing re-activates automatically.
- Each list is limited to the API's page size (100), like before.
- Other screens (History filters, the archive) still show a deactivated workflow's runs and documents.
