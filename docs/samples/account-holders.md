# Sample: account holders (XML, PDF per "C" account, archive)

Drop `Account.xml` in a folder; each account holder whose `AccountID` starts with `C` (C001, C002, C003, C004) gets a PDF that is archived. The others (1001, 1002, ...) are skipped. Synthetic data only. Uses existing steps only.

Files in [tests/fixtures/account-holders](../../tests/fixtures/account-holders): `Account.xml` (input), `layout.json` (template), `sample-data.json`, `workflow.json`.

```
Folder input (*.xml) ─ Parse (XML → JSON) ─ For each AccountHolder ─ If AccountID starts with C ── yes ─ Generate PDF ─ Archive
                                                                                                  └─ no ─ (nothing; that holder is finished)
```

## Load it

With the API running (`Auth:Mode=Development`) and the Worker running:

```powershell
powershell -ExecutionPolicy Bypass -File tools/samples/load-account-holders.ps1     # template + workflow, published
```

Then copy `Account.xml` into `C:\LN\DocGen\test\Input` (write it under a temporary name such as `Account.xml.tmp` and rename it, or just copy: the Worker waits until the file has been unchanged for 10 seconds). Within a minute the Worker picks it up, starts one run and moves the file to `Processed` (or `Failed`, with a `.error.json` beside it).

## Things to know

- **Folder input reads `.json`, `.xml` and `.csv`** ([ADR 0001](../adr/0001-folder-input.md), addendum 3). An XML file is passed on as `{ "content": "<xml>" }` and the Parse step turns it into data. The pattern of this sample is `*.xml`. Other extensions go to `Failed`.
- Identical content never starts a second run (`file.duplicate`): change the file to run it again.
- The folder path must be allowed (`FolderInput:AllowedRoots`, if set) and the Worker's service account needs Modify access.
- The "starts with" test ignores case: `c` would match as well. Numbers such as 1001 are compared as text.
- Archive writes to `accounts/<AccountType>/` in the archive (local file store), not to a folder you name.
- One PDF per holder, each its own document in the run's history. The first failure ends the whole file.
- `-WrapXml <file>` of the load script still writes a JSON envelope for the older `*.json` setup; it is no longer needed.
