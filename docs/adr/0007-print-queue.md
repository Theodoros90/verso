# ADR 0007: Print queue step (PDF to a spool folder)

- Status: accepted (implemented 2026-11-12, awaiting acceptance)
- Requested by the product owner on 2026-11-12: build the prototype's *Print queue* output ("Sends documents for printing and posting"). When asked how it should print, the product owner chose a **spool folder**. CLAUDE.md listed print among the integrations that stay simulated until approved; this ADR records that approval, scoped to writing files into folders the administrator configured.

## Context

The prototype's step picks a print queue (a print house or an in-house printer), an envelope (DL window or C4) and double-sided printing. Verso cannot talk to printers or a print house; the bank's print system (or the print house's import job) can pick files up from a folder.

## Decision

1. **Queues are configured, never typed.** The Worker's `Print:Queues` configuration maps a queue **key** (lower-case letters, digits and hyphens, 2 to 64 characters) to a folder (`Path`) and a display `Name`. Workflow authors type only the key in the step. They never type a path, so a workflow can't make the Worker write anywhere the administrator didn't allow. An unknown key fails the run with `print.queue_unknown` (technical, not retried).
2. **Step.** `print-queue`, category *output*, port `queued`. Settings: `queue` (key), `envelope` (`dl-window`, `c4`) and `duplex` (default on). It prints the run's current document, which must be a PDF (`print.no_document`, `print.not_pdf`, neither retried). The document is read back from storage and its SHA-256 is checked first (`storage.integrity`, retried).
3. **Job format.** In the queue's folder the Worker writes `{jobId}.pdf` and then `{jobId}.json`, each first as `.tmp` and renamed when complete, so the print system never sees a partial file and sees the job file only when the PDF is already there. The JSON holds `jobId`, `queue`, `file`, `fileName`, `sizeBytes`, `sha256`, `envelope`, `duplex` and `createdAt`. It carries no customer data besides the document's file name. The print system should delete both files after taking the job.
4. **Idempotent.** `jobId` is derived from (execution, step, iteration), so a retry or a resumed run rewrites the same two files instead of queueing a second job. The print system must not take a job whose JSON isn't there yet and must remove files it has taken, so a rewrite after it took the job would queue it again; in practice this needs the Worker to stop between writing and saving the step.
5. **Layering.** `IPrintSpooler` (with `PrintJob`, `PrintSpoolException`, `PrintQueues`) in `Verso.Application/Executions`; `FolderPrintSpooler` in `Verso.Infrastructure/Print`; registered by the Worker only (`AddVersoPrintSpooler`). No new project or package.
6. **Failures.** A folder that doesn't exist or can't be written is retryable (`print.queue_unavailable`; the message names the queue key, never the path). The step result holds the job id, queue key, file name, size, envelope and duplex, never the content.
7. **No credentials.** The Worker writes as its own service account, which needs Modify on each queue folder. Nothing is stored in Verso.

## Consequences

- `queued` means the files are in the folder, not that anything was printed or posted. There is no status coming back from the print system.
- The designer can't offer a list of queues: the API doesn't know the Worker's configuration, so the author types the key (the description says so). A wrong key shows up on the first run.
- Not done: copies, paper trays, PCL or AFP conversion (the file is the PDF), a queue list in the designer, a print-status screen, SFTP delivery to a print house (a transfer job could move the folder's files).
