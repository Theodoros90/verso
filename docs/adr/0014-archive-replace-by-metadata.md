# 0014. Archive replace by metadata

Date: 2026-11-16. Status: accepted (implemented, awaiting acceptance).

## Context
Re-running a workflow for the same subject (for example the same AccountID and Name) archived a second copy next to the first. Authors want the newer document to replace the older one, decided by metadata values.

## Decision
- Each row of the Archive step's Metadata setting has an optional boolean `match` ("Replace when this matches"). Absent or false = not part of the match.
- When at least one row is ticked, the step computes a **match key**: SHA-256 of the workflow id and the ticked (key, value) pairs ordered by key. It is stored on the new `ArchiveRecord` (`MatchKey`).
- Archiving then **supersedes** every unsuperseded record with the same key: `SupersededAt` and `SupersededByDocumentId` are set. Nothing is deleted: the record, the file and its SHA-256 stay, the download still works, and a `DocumentSuperseded` audit entry is written per replaced document.
- Scope is the workflow, so two workflows never replace each other's documents.
- If a ticked value is missing or blank for the run, nothing is replaced and the step result carries a warning.
- Archive search leaves superseded documents out unless `includeSuperseded=true`; the Archive page offers "Show replaced documents" and marks them.
- Migration `ArchiveRecordMatchKey` adds three nullable columns and a filtered index. No new package.

## Consequences
Existing workflows are unchanged (no row ticked). Storage is not reclaimed by replacing; retention handling stays as in the MVP.
