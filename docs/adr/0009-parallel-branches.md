# ADR 0009: Parallel branches (sequential fan-out)

- Status: accepted (implemented 2026-11-14, awaiting acceptance)
- Requested by the product owner on 2026-11-14 (the "Parallel branches" step of the v11 prototype). The product owner chose real execution over a designer-only preview, accepting that the two paths run one after the other, not at the same time.

## Context

The engine followed exactly one path per run: every step chose one output port and each port has at most one edge (`ExecutionPlan`). The prototype's Parallel branches step fans out to two paths ("Branch A" and "Branch B") and says its preview visits them sequentially. A step with two outputs taken at once is new engine behavior, so it needs a decision.

## Decision

1. **New step `parallel-branches`** (category `logic`): input `in`, outputs `branch-a` and `branch-b` (both required: a fork with one path is pointless). One setting, `name` (the branch group's name, required, up to 100 characters). No new package, no schema change.
2. **Execution is sequential.** After the step succeeds the engine runs Branch A to the end of its path, then Branch B. Each branch starts from the data and the current document as they were at the fork, so one branch doesn't see what the other produced. The run is over when both paths have ended, and it Succeeds. "Parallel" describes the design (two independent paths), not the timing.
3. **Failure ends the run.** A step in either branch that ends the run Rejected (such as a failed validation, or a declined approval with no path) or Fails (a technical error after its retries) ends the whole run, as in a For each item. If Branch A does that, Branch B never runs and its steps show as Skipped.
4. **Resume uses the existing step rows.** The walk is deterministic and steps that already succeeded are restored, not run again, so a retry, a re-delivered job or an Approval inside a branch resumes in the right branch with no new state.
5. **Rules the validator enforces** (errors, so a workflow that breaks them can't be published). They keep this version small and the paths independent:
   - at most one Parallel branches step per workflow (`parallel.multiple`);
   - not in a workflow that has a For each (`parallel.with_loop`);
   - no step reachable from both branches (`parallel.shared_step`): each branch ends on its own. A join ("Wait for branches" in the prototype) is a separate, later step.
6. **History and live progress** need no change: steps are listed in plan order and each has its own row and events. A branch's steps are listed with the steps of the other branch in the order of the plan, which is not always the order they ran in.

## Consequences

- Nothing runs at the same time, so a slow step in Branch A delays Branch B. The run's five-minute limit covers both.
- Two branches each end in their own place (for example one archives a PDF and the other sends an email), and nothing merges their results.
- Parallel branches can't be used with a For each in this version. Nested forks are not allowed.
- If true concurrency or a join is wanted later, it is a further decision: it needs per-branch state, a join step and a way to show concurrent steps in history.
