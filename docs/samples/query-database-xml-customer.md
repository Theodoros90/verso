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
                                                                                └─ not-found ─ (the run ends; no PDF for an unknown customer)
```

Settings of the Query database step:

| Setting | Value |
|---|---|
| Connection | the read-only connection |
| SQL | `SELECT segment, preferred_name FROM customers WHERE customer_no = @no` |
| Parameters | `no = customer.number` (inside For each; the data path of the current customer) |
| Result name | `db` |
| Max rows | 1 (default): one row becomes the object `db` |

After the step the run data holds the XML fields and `db`, so the template can use `{{customer.name}}` and `{{db.segment}}` / `{{db.preferred_name}}`. Use [`Merge data`](../api.md) afterwards only when you want the database fields on the customer record itself.

## The parallel variant

To read the XML and the database at the same time, use Parallel branches: one path Parse (XML), the other Query database (its parameter from the submitted data), then Wait for branches, Merge data, Generate PDF. The two paths run one after the other (ADR 0009), and the step works the same inside a branch.

## Things to know

- **Values are always parameters** (`@no`). The SQL is checked when you save, when you publish (the SQL text and connection key are written to the audit trail) and again when the run executes. A query with a second statement, a write keyword, `SELECT ... INTO`, `FOR UPDATE` or a backslash in a string literal is refused.
- **A missing `customer.number` rejects the run** (`data.query_invalid`). An unknown customer takes the `not-found` port; connect it, or the run stops there without a document.
- The step keeps only the number of rows, the column names and the time in the run's history, never the values. After an approval wait or a restart the query runs again, so the values may differ.
- **SMS to individuals and email to corporate customers:** Send email is a real step, so the corporate branch can email the PDF (If / else on `customer.type`). Texting individuals needs a **Send SMS step, which is not built yet** (the SMS connections of ADR 0018 only support Test connection); it needs its own ADR.
- Only SQL Server has been verified against a live server; try PostgreSQL, MySQL / MariaDB or Oracle on a non-production database first.
