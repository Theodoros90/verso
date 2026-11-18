# Sample: customer XML, look-up in a database, PDF (Query database step)

A customer XML file arrives in a folder. For each customer the workflow reads the customer's `segment` and `preferred_name` from a database, merges them with the XML data and generates a PDF that uses both. Synthetic data only; the table, column and customer names are invented. This sample is a recipe to build in the designer, there is no loader script for it. Uses the Query database step ([ADR 0019](../adr/0019-query-database-step.md)).

## The XML (individual and corporate)

```xml
<?xml version="1.0" encoding="utf-8"?>
<customers>
  <customer type="individual">
    <number>C-1001</number>
    <name>Maria Example</name>
    <email>maria@example.com</email>
  </customer>
  <customer type="corporate">
    <number>C-2001</number>
    <name>Example Trading Ltd</name>
    <email>accounts@example.org</email>
  </customer>
</customers>
```

## The database

A table the connection's login can read (synthetic):

```sql
-- customers(customer_no, segment, preferred_name)
-- ('C-1001', 'retail',    'Maria')
-- ('C-2001', 'corporate', 'Example Trading')
```

Create a Database connection in Configuration, Connections with a **read-only login** that has SELECT on this table only ([setup.md, section 7](../setup.md)), and try Test connection.

## The workflow

```
Folder input (*.xml) ─ Parse (XML → JSON) ─ For each customer ─ Query database ── found ─ Generate PDF ─ Archive
                                                                                └─ not-found ─ (only this customer ends: no PDF for an unknown customer; the others carry on)
```

The steps run one after the other: Parse, then the query, then the document. That is the form to use (see "Why not Parallel branches" below).

Settings of the steps:

| Step | Setting | Value |
|---|---|---|
| Parse | Format | `xml` (no record path: the data becomes `{ "customers": { "customer": [ ... ] } }`) |
| For each | List field | `customers.customer` |
| For each | Item name | `customer` (so `customer.number` is the current customer's number) |
| Query database | Connection | the read-only connection |
| Query database | SQL | `SELECT segment, preferred_name FROM customers WHERE customer_no = @no` |
| Query database | Parameters | `no = customer.number` |
| Query database | Result name | `db` |
| Query database | Max rows | 1 (default): one row becomes the object `db` |

After the step the data holds the customer and `db`, so the template can use `{{customer.name}}` and `{{db.segment}}` / `{{db.preferred_name}}`. A top-level field that is already called `db` would be replaced. If you want the database fields on the customer record itself, add a **Map fields** step after the query (rows such as `db.segment` → `customer.segment`). Merge data does not do this: it matches against a lookup table typed into that step, not against another step's result.

The XML attribute `type` becomes the field `@type` (Parse turns attributes into `@name`), so a branch on it reads `customer.@type`.

## Why not Parallel branches

Parallel branches (ADR 0009) run their two paths one after the other, and after Wait for branches the run continues with the data **as the second path (Branch B) left it**: the two branches' data do not combine. A Parse on one path and a Query database on the other therefore cannot meet in one document, and Merge data cannot join them (it only uses the lookup table typed into it). Put the query after the Parse, as above.

## Things to know

- **Values are always parameters** (`@no`). The SQL is checked when you save, when you publish (the SQL text and connection key are written to the audit trail) and again when the run executes. Refused, among others: a second statement, a write, DDL, transaction or session keyword as a bare word (`COMMIT`, `SET`, `WAITFOR`, ... ; quote a column with such a name), `SELECT ... INTO`, `FOR UPDATE`, a backslash, `#`, a `--` comment without a space after it, and anything the four databases would read differently (ADR 0019, section 2).
- **A missing `customer.number` rejects the run** (`data.query_invalid`): the one case that takes the step's `failed` port. Every database or SQL failure fails the run instead (retried first when it may pass). An unknown customer takes the `not-found` port; connect it, or that customer stops there without a document.
- The step keeps only the number of rows, the column names, whether rows were cut, the time and the connection key in the run's history, never the values. Values can still reach the history if you put them there yourself, for example in an Archive metadata pattern such as `{{db.preferred_name}}`. After an approval wait or a restart the query runs again, so the values may differ.
- **SMS to individuals and email to corporate customers:** Send email is a real step, so the corporate branch can email the PDF (If / else on `customer.@type` equal to `corporate`). Texting individuals needs a **Send SMS step, which is not built yet** (the SMS connections of ADR 0018 only support Test connection); it needs its own ADR.
- Only SQL Server has been verified against a live server; try PostgreSQL, MySQL / MariaDB or Oracle on a non-production database first.
