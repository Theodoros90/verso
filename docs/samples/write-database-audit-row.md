# Sample: customer XML, PDF, audit row written to a database (Write to database step)

A customer XML file arrives in a folder. For each customer the workflow generates a PDF and then writes one row to an audit table (`document_log`) saying which statement was produced for which customer. Synthetic data only; the table, column and customer names are invented. This sample is a recipe to build in the designer, there is no loader script for it. Uses the Write to database step ([ADR 0020](../adr/0020-write-database-step.md)); the reading counterpart is [query-database-xml-customer.md](query-database-xml-customer.md).

## The XML

```xml
<?xml version="1.0" encoding="utf-8"?>
<customers>
  <customer type="individual">
    <number>C-1001</number>
    <name>Maria Example</name>
    <statement>Statement 2026-11</statement>
  </customer>
  <customer type="corporate">
    <number>C-2001</number>
    <name>Example Trading Ltd</name>
    <statement>Statement 2026-11</statement>
  </customer>
</customers>
```

## The database and the connection

A table that only this workflow writes (synthetic):

```sql
-- document_log(customer_no, document_name, created_at)
```

1. Create a login that can **only** change that table (`SELECT, INSERT, UPDATE, DELETE` on `document_log`; the `WHERE NOT EXISTS` below reads it): [setup.md, section 8](../setup.md).
2. An **administrator** creates a Database connection with that login in Configuration, Connections (for example key `audit-db`) and **switches on "Allow workflows to change data"**, confirming the warning. Without the switch the step fails with `db.writes_not_allowed` before anything is sent. Use a connection of its own for this purpose and keep the switch off on connections that are only for reading.
3. Try Test connection.

## The workflow

```
Folder input (*.xml) ─ Parse (XML → JSON) ─ For each customer ─ Generate PDF ─ Write to database ── written ─ Archive
                                                                                 └─ failed (a missing value: the whole run is rejected)
```

Archive has no output port, so the audit row is written **before** the Archive step: if the write fails, the run stops at the write and the PDF is not archived until the run is retried. (Generate PDF comes first so that the row is only written for a document that was made.)

**Inside a For each, any failure ends the whole run, not only that customer.** If the write's `failed` port is taken for one customer (a missing value), the run ends **Rejected** (`data.write_invalid`) whether or not the port is connected, and the later customers get no PDF and no row. A Rejected run cannot be retried: the sender **submits the file again**, and the new run writes **every** customer again, including those already written. The `WHERE NOT EXISTS` SQL below makes that harmless; a plain `INSERT ... VALUES` would duplicate them. Any database failure of the write ends the run **Failed** in the same way (customers before it keep their rows).

| Step | Setting | Value |
|---|---|---|
| Parse | Format | `xml` |
| For each | List field | `customers.customer` |
| For each | Item name | `customer` |
| Write to database | Connection | `audit-db` (the one with writes on) |
| Write to database | SQL | see below |
| Write to database | Parameters | `no = customer.number` and `doc = customer.statement` (one per line) |
| Write to database | Most rows to change | 1 (default) |
| Write to database | Time limit | 15 s (default) |

The SQL, one statement, idempotent:

```sql
INSERT INTO document_log (customer_no, document_name, created_at)
SELECT @no, @doc, CURRENT_TIMESTAMP
WHERE NOT EXISTS (SELECT 1 FROM document_log WHERE customer_no = @no AND document_name = @doc)
```

This text was run through `SqlGuard.CheckWrite` and is accepted (parameters `no` and `doc`). It is valid as written on SQL Server and PostgreSQL. **MySQL / MariaDB and Oracle need a table in the SELECT when it has a WHERE: add `FROM DUAL` after `CURRENT_TIMESTAMP`** (`... SELECT @no, @doc, CURRENT_TIMESTAMP FROM DUAL WHERE NOT EXISTS (...)`), and Oracle statements must name the table's schema (`app.document_log`).
## What the step does

- Values are always parameters (`@no`, `@doc`), bound from the run's data at `customer.number` and `customer.statement`. A path that is missing, null, or holds a group or a list takes the **`failed` port**; the database is not called. Outside a loop the run then ends **Rejected** (`data.write_invalid`) unless the port is connected; inside this For each it ends the whole run Rejected either way (see above). Nothing but these two parameters is ever sent as data.
- The statement runs in one transaction; it is committed only when it reports at most "Most rows to change" rows (1 here). Zero rows changed (the row already exists, so `NOT EXISTS` is false) is **not** an error: the step still succeeds with `rowsAffected` 0.
- The step records only `rowsAffected`, `durationMs` and `connection` (the key) in the run's history: never the values and never the SQL. The SQL text and connection key are written to the audit trail when you **publish**, and each committed write adds an audit entry `DatabaseWriteExecuted` (the run, step, item number, connection key and row count).
- The data of the run is not changed by the step.

## Things to know

- **Reruns and unknown outcomes.** A write is **never retried automatically**, and never repeated when a run resumes after an approval or a Worker restart. If a run fails with `write.outcome_unknown`, or a message "The outcome is unknown", the row may or may not be in the table: **look in `document_log` for that customer and statement first**, then decide to retry. A person's retry runs the write again. That is why the SQL above is idempotent: if the row was in fact written, the retry inserts nothing.
- The list here comes from the submitted file, so it is the same whenever the run resumes. If the loop's list came from a **Query database** step instead, and that query returned other rows when the run resumed (after an automatic retry of a later step, an approval or a restart), the run fails with **`loop.items_changed`** rather than guess which customer is which (runbook).
- A duplicate is still possible with non-idempotent SQL (a plain `INSERT ... VALUES`) when a person retries a run whose write was applied, or when the file is submitted again after a rejected or failed run. Prefer `WHERE NOT EXISTS` or a unique key on (`customer_no`, `document_name`): a duplicate key then fails as a known `db.query_failed` and changes nothing.
- **Refused by the guard, among others:** a second statement, `UPDATE` or `DELETE` without a `WHERE` (`sql.no_where`), `MERGE`, `INSERT ... ON DUPLICATE KEY UPDATE`, `SELECT ... INTO`, a `SELECT` after the write, DDL, and anything the four databases would read differently (ADR 0019 section 2, ADR 0020 section 3). The guard is a token filter, not a parser; the login's permissions and the switch are the protection.
- Writing a `created_at` that the database sets itself (`CURRENT_TIMESTAMP`) means no date value needs to leave the run's data.
- **SMS to individuals and email to corporate customers:** Send email is a real step, so the corporate branch can email the PDF (If / else on `customer.@type`). Texting individuals still needs the **Send SMS step, which is not built yet** (the SMS connections of ADR 0018 only support Test connection); it needs its own ADR, as do an API call step and SFTP.
- Only SQL Server has been verified against a live server; try PostgreSQL, MySQL / MariaDB or Oracle on a non-production database first. MySQL tables must be InnoDB.
