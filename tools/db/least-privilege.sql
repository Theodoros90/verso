/*
  Verso: least-privilege database roles for the two application identities (plan §7, M9).

  Run as a DBA (or the deploy identity) after every `dotnet ef database update`; it is idempotent.
  It creates two database roles and their permissions. It does NOT create logins or users: the DBA maps the
  service accounts, for example:

      CREATE USER [CORP\svc-verso-api]    FOR LOGIN [CORP\svc-verso-api];
      CREATE USER [CORP\svc-verso-worker] FOR LOGIN [CORP\svc-verso-worker];
      ALTER ROLE verso_api    ADD MEMBER [CORP\svc-verso-api];
      ALTER ROLE verso_worker ADD MEMBER [CORP\svc-verso-worker];

  Neither role may change the schema: migrations run under a separate deploy identity, never the applications.

  Write-once data (enforced here, in addition to the application):
    - audit.AuditEntries      INSERT/SELECT only, for both roles
    - documents.ArchiveRecords INSERT/SELECT only (Worker); SELECT only (API)
  Nothing is ever deleted from templates, workflows, executions or documents by the applications.
  Verified by tests/Verso.Api.Tests/LeastPrivilegeTests.cs, which runs the whole flow under these roles.
*/
SET NOCOUNT ON;

IF DATABASE_PRINCIPAL_ID(N'verso_api') IS NULL CREATE ROLE verso_api;
IF DATABASE_PRINCIPAL_ID(N'verso_worker') IS NULL CREATE ROLE verso_worker;

-- Both: read the migrations history (health checks report pending migrations).
GRANT SELECT ON OBJECT::dbo.__EFMigrationsHistory TO verso_api, verso_worker;

-- ----- API: authoring, submissions, reads; never writes documents -----
GRANT SELECT, INSERT, UPDATE ON SCHEMA::templates  TO verso_api;  -- drafts, publishing, PDF preview requests
GRANT SELECT, INSERT, UPDATE ON SCHEMA::workflows  TO verso_api;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::executions TO verso_api;  -- submit, retry, events
GRANT SELECT                 ON SCHEMA::documents  TO verso_api;  -- archive search, downloads
GRANT SELECT, INSERT         ON SCHEMA::audit      TO verso_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::hangfire TO verso_api;  -- the Hangfire client queues jobs
GRANT SELECT                 ON SCHEMA::ingestion  TO verso_api;  -- approved locations, folder status (reported by the Worker)
GRANT INSERT, UPDATE         ON OBJECT::ingestion.FolderAccessTests TO verso_api;  -- "Test access" requests

-- ----- Worker: runs executions and previews; never authors templates or workflows -----
GRANT SELECT                 ON SCHEMA::templates  TO verso_worker;
GRANT UPDATE, DELETE         ON OBJECT::templates.PdfPreviews TO verso_worker;  -- render result; 24 h cleanup
GRANT SELECT                 ON SCHEMA::workflows  TO verso_worker;
GRANT SELECT, INSERT, UPDATE ON SCHEMA::executions TO verso_worker;
GRANT SELECT, INSERT, UPDATE ON OBJECT::documents.Documents      TO verso_worker;
GRANT SELECT, INSERT         ON OBJECT::documents.ArchiveRecords TO verso_worker;
GRANT SELECT, INSERT         ON SCHEMA::audit      TO verso_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::hangfire TO verso_worker;  -- the Hangfire server
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ingestion  TO verso_worker;  -- folder input: locations, scans, files, access tests
GRANT DELETE                 ON OBJECT::ingestion.FolderFiles       TO verso_worker;  -- releasing a claim on a file still being written
GRANT DELETE                 ON OBJECT::ingestion.FolderAccessTests TO verso_worker;  -- 24 h cleanup

-- ----- Write-once and no-delete rules (DENY wins over any GRANT, including future ones) -----
DENY UPDATE, DELETE ON OBJECT::audit.AuditEntries        TO verso_api, verso_worker;
DENY UPDATE, DELETE ON OBJECT::documents.ArchiveRecords  TO verso_api, verso_worker;
DENY DELETE         ON OBJECT::documents.Documents       TO verso_api, verso_worker;
DENY DELETE         ON SCHEMA::executions                TO verso_api, verso_worker;
DENY DELETE         ON SCHEMA::workflows                 TO verso_api, verso_worker;
DENY DELETE         ON OBJECT::templates.Templates       TO verso_api, verso_worker;
DENY DELETE         ON OBJECT::templates.TemplateVersions TO verso_api, verso_worker;
DENY DELETE         ON OBJECT::ingestion.FolderLocations  TO verso_api, verso_worker;  -- withdrawn locations are disabled, never deleted
DENY UPDATE         ON OBJECT::ingestion.FolderFiles      TO verso_api;               -- only the Worker changes ingestion records
