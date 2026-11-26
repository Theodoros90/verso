# Sample: account holders by upload (File upload, XML, PDF per "C" account, archive)

A signed-in Operator uploads `Account.xml` from the workflow list. Each account holder whose `AccountID` starts with `C` (C001, C002, C003, C004) gets a PDF that is archived; the others (1001, 1002, ...) are skipped. This is the upload version of [account-holders.md](account-holders.md): same file, same template, same steps, but the run starts from the File upload step ([ADR 0023](../adr/0023-file-upload-step.md)) instead of a watched folder. Synthetic data only.

Files in [tests/fixtures/account-holders](../../tests/fixtures/account-holders): `Account.xml` (the file to upload), `layout.json` and `sample-data.json` (the template), `workflow-upload.json` (the workflow).

```
File upload (XML, up to 1 MB, staff) ─ Parse (XML → JSON) ─ For each AccountHolder ─ If AccountID starts with C ── yes ─ Generate PDF ─ Archive
                                                                                                                  └─ no ─ (nothing; that holder is finished)
```

## Before you start

1. The migration `FileUploads` is applied to your dev database (`dotnet tool run dotnet-ef database update --project src/Verso.Infrastructure --startup-project src/Verso.Api`).
2. The API (`Auth:Mode=Development`, default `http://localhost:5122`), the **Worker** and the web app are running. The Worker takes the file and runs the workflow; without it a run stays Queued.

## Load it

```powershell
powershell -ExecutionPolicy Bypass -File tools/samples/load-account-holders-upload.ps1     # template + workflow, published
```

It prints the workflow's key and its name, "Account holder PDFs (upload <number>)". Sign in as the Operator ("Omar Operator"), open **Workflows**, open the card's **⋯** menu and choose **Upload file…**.

## Test cases

"Run page" means the page opened from the history list or the "Open the run" link. Cases 1 to 3 and 5 to 8 are also covered by automated tests (the happy path of this sample by `AccountHoldersSampleTests.UploadedAccountFile_GetsTheSameFourArchivedPdfs`; the checks by `UploadRulesTests`, `UploadServiceTests`, `UploadApiTests` and the browser E2E `FileUploadTests`), but only with a fake PDF printer and a fake `XMLHttpRequest`: **run them by hand once** with the real Worker, Chromium and a real browser.

First make the test files (a scratch folder, nothing in the repo):

```powershell
$d = "$env:TEMP\verso-upload-tests"; New-Item -ItemType Directory -Force $d | Out-Null
Set-Content "$d\fake.xml" '%PDF-1.4 fake' -Encoding ascii                       # starts like a PDF, named .xml
New-Item "$d\empty.xml" -ItemType File -Force | Out-Null                         # zero bytes
Set-Content "$d\customers.csv" "id,name`n1,Synthetic" -Encoding ascii            # a CSV: this workflow accepts XML only
[IO.File]::WriteAllText("$d\big.xml", '<AccountHolders>' + ('x' * 1200000) + '</AccountHolders>')   # over 1 MB
Set-Content "$d\notxml.xml" 'hello, this is not xml' -Encoding ascii             # passes the upload checks, is not XML
$x = (Get-Content -Raw tests\fixtures\account-holders\Account.xml).Replace('ABC Technologies Ltd', 'ABC Technologies Ltd (edited)')
[IO.File]::WriteAllText("$d\Account-edited.xml", $x, (New-Object Text.UTF8Encoding($false)))      # one value changed
```

| # | What you do | What you should see |
|---|---|---|
| 1 | Upload `tests\fixtures\account-holders\Account.xml`. | The dialog says "Allowed file types: XML, up to 1 MB." After Upload: "The file was uploaded and a new run has started." with an **Open the run** link. Within a minute the run is **Succeeded**. The run page shows the File upload step with "Uploaded file: Account.xml (<size> B)", four documents `C001.pdf` to `C004.pdf` (each archived under `accounts/Corporate/`), and no `1001`, `1002` or `1003` PDF. The file's content is not shown anywhere on the page. |
| 2 | Upload the **same** `Account.xml` again. | "This file was already uploaded; showing the first run", with a link to the run of case 1. No new run in history. |
| 3 | Upload `Account-edited.xml`. | A **new** run (different content), Succeeded, four PDFs again. |
| 4 | Stop the Worker. Upload `Account-edited.xml` after changing one more value (or use a copy with another name edit). Look at history, then start the Worker. | The run waits as **Queued** while the Worker is stopped, then runs to Succeeded after it starts. The file was kept safely until then. |
| 5 | Upload `fake.xml`. | Refused before any run starts, with a plain message that the content does not match the file type. No new run. |
| 6 | Choose `customers.csv`. | The dialog refuses it before sending anything: "“customers.csv” can’t be uploaded: files of type .csv are not accepted. Allowed: XML." No new run. |
| 7 | Choose `empty.xml`. | The dialog refuses it: "“empty.xml” is empty." No new run. |
| 8 | Choose `big.xml`. | The dialog refuses it: "“big.xml” is too large (1.1 MB). The limit is 1 MB." No new run. |
| 9 | Upload `notxml.xml`. | The upload is accepted (it is valid text), a run starts, and the run does **not** succeed: it stops at the **Read the XML** (Parse) step and the run page shows the reason. Check what the run page says and whether the run is Rejected or Failed, then tell me: this case was not run by me. |
| 10 | Start an upload of a large file (for example make `big.xml` 20 MB and raise the workflow's size to 25 MB first) and press **Cancel** while it is sending. | The dialog closes without an error message and no run appears for it. Opening **Upload file…** again works normally. |
| 11 | Sign in as the Viewer ("Vera Viewer") and open **Workflows**. | The sample workflow is listed, with no actions menu and no **Upload file…**. |
| 12 | The checks of cases 6 to 8 also happen on the server, for a caller that skips the dialog: with the `curl.exe` command of case 13 and `dev.operator`, send `"$env:TEMP\verso-upload-tests\customers.csv"`, `...\empty.xml` and `...\big.xml` instead of `Account.xml` (use `-F "file=@$env:TEMP\verso-upload-tests\big.xml"`). | Each answers `400` or `413` with a plain problem message (`upload.type_not_allowed`, `upload.empty`, `upload.too_large`), and no run starts. |
| 13 | In PowerShell, replace `<key>` with the workflow key printed by the loader: `curl.exe -s -o NUL -w "%{http_code}" -H "Authorization: Bearer dev:dev.viewer" -F "file=@tests\fixtures\account-holders\Account.xml" http://localhost:5122/api/v1/workflows/<key>/uploads`, then the same with `dev.operator`, then with no `-H` header. | Viewer: `403`. Operator: `200` (it is the same file as case 1, so the first run again) or `202` for a new file. No header: `401`. |

## Check the inside (optional)

After case 1 or 3, in SQL Server (your dev database):

```sql
SELECT TOP 5 Status, Size, DATALENGTH(Content) AS ContentBytes, ExecutionId, CreatedAt
FROM ingestion.Uploads ORDER BY CreatedAt DESC;
```

Expected: `Status = Taken` and `ContentBytes` NULL once the run has taken the file (the bytes now live in the document store under `Documents:RootPath\staging\<execution id>\trigger.xml`). A row still `Staged` with bytes means the Worker has not run that step yet. Rows no run took expire after 24 hours (`Uploads:Retention`), which is not practical to test by hand; `UploadRetentionTests` covers it.

## Things to know

- The staged copies under `Documents:RootPath\staging\` are **not deleted** yet (open question 5 of the ADR): they stay after the run.
- Uploading the same bytes twice to the same workflow never starts a second run (case 2), even if the first run failed. Change the file to run it again.
- One file per upload, no `.docx`, no malware scanning, and only signed-in Operators and Admins can upload.
- The "starts with" test ignores case, as in the folder sample; the Archive step writes under `accounts/<AccountType>/` in the archive store, not to a folder you name.
