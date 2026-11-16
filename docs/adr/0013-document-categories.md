# ADR 0013: Document categories

- Status: accepted (implemented 2026-11-16, awaiting acceptance)
- Requested by the product owner on 2026-11-16: templates should be organised by document category (for example Invoices, Statements, Letters), with the categories maintained in the Configuration drawer.

## Context

Templates were a flat list ordered by key. With more than a handful of templates there was no way to group or find them by kind of document. `Template.DocumentType` exists, but it is a fixed value (`Invoice` in the MVP) and not something users maintain. As elsewhere, nothing that executions, documents or audit entries refer to may be deleted (`tools/db/least-privilege.sql` grants no DELETE on the `templates` schema to `verso_api` and denies it on the template tables, `templates.DocumentCategories` included).

## Decision

1. **A new entity, `DocumentCategory`**, in the Templates module (schema `templates`): `Id`, `Name` (1 to 100 characters), `IsActive`, `CreatedAt/By`. Names are unique case-insensitively (after trimming): the column carries an explicit `Latin1_General_100_CI_AS` collation (migration `DocumentCategoryNameCollation`) so this does not depend on the database default, and the unique index `UX_DocumentCategories_Name` follows it. `Template` gets a nullable `CategoryId`. One additive migration (`AddDocumentCategories`); existing templates stay uncategorised.
2. **Flat, optional, one per template.** No nesting, no per-category permissions, no multiple categories on one template. A template without a category is valid.
3. **Never deleted, only deactivated.** A deactivated category can't be assigned to a template any more (a template that already has it keeps it and still shows it), is left out of the default list and the pickers, and can be activated again. Its name stays reserved.
4. **API.** `GET /api/v1/document-categories` (`?includeInactive=true`), `POST`, `PUT /{id}` (rename), `POST /{id}/deactivate`, `POST /{id}/activate`; reads for every role, changes need the TemplateAuthoring role. `PUT /api/v1/templates/{id}/category` assigns or clears (`{ "categoryId": null }`); `POST /api/v1/templates` takes an optional `categoryId`; template DTOs carry `categoryId` and `categoryName`; `GET /api/v1/templates` takes `?categoryId=<guid>|none`. Errors: 400 `validation` (`name`, `categoryId`), 409 `category.name_taken`, 404 `category.not_found`. Repeating deactivate / activate, or setting the category a template already has, changes nothing and writes no audit entry.
5. **Audit.** The initial category is recorded in the `TemplateCreated` entry (`details.categoryId`, null when none). `CategoryCreated`, `CategoryRenamed`, `CategoryDeactivated`, `CategoryActivated` (`entityType=DocumentCategory`) and `TemplateCategoryChanged` (`entityType=Template`, from and to), each written in the same save as the change.
6. **Category changes are metadata, not content.** Assigning a category does not touch the draft or any published version, so no new version, ETag or content hash is involved. In the designer the picker saves immediately, separately from the draft.
7. **Not carried by export / import** (reversed by addendum 2). Packages (ADR 0010) don't include categories: a category is an environment-local list, and an imported template arrives uncategorised.
8. **UI.** A "Document categories" tab, first in the Configuration drawer (add, rename, deactivate, activate, template counts). The New template dialog has an optional Category select; the template list shows a category chip and has a category filter (including "No category"); the designer has a category picker.

## Consequences

- There is no permanent delete. A mistaken category is deactivated, and its name can't be reused for a different category.
- The list filter can only select active categories, so templates in a deactivated category can't be listed by category; activate the category to filter on it. (Deferred.)
- Assigning a category and deactivating it at the same moment can race: the assignment is checked, then saved. The worst outcome is one template holding a just-deactivated category, which is harmless. (Accepted.)
- Categories are not in export / import packages, and are not on workflows or executions. Reporting by category is out of scope. (Archived documents carry one since addendum 1.)

## Addendum 1: archived documents carry the template's category (2026-11-16)

Requested by the product owner on 2026-11-16: documents should land in the archive under their template's category, and the Archive page should show and filter by it.

1. **Copied when archived.** `documents.ArchiveRecords` gets a nullable `CategoryId` (by value, no foreign key across schemas; indexed `IX_ArchiveRecords_CategoryId`). The Archive step copies the category of the template behind the document's template version at that moment. No template (HTML email) or a template without a category means null, shown as "Uncategorized". Like the rest of the archive record it is written once: changing a template's category later does not re-file documents already archived.
2. **Backfill.** Migration `ArchiveRecordCategory` adds the column and, once, fills it for records archived earlier from their template's category as it was when the migration ran.
3. **API.** `GET /api/v1/documents` takes `?categoryId=<guid>|none` (anything else is 400 `validation`, `categoryId`); `ArchivedDocument` carries `categoryId` and `categoryName` (the category's current name). No new endpoint, no new permission, no new package.
4. **UI.** The Archive page has a Category column (a chip, or "Uncategorized") and a Category filter: All categories, Uncategorized, then every category, deactivated ones marked "(inactive)" because archived documents keep them.

## Addendum 2: categories travel in the configuration package (2026-11-16)

Requested by the product owner on 2026-11-16. This replaces decision 7 ("Not carried by export / import") for the category list itself.

1. **By name.** The package file gets a `categories` section: `{ name, isActive }` per category, no ids (ids are environment-local). The section is optional when reading, so packages made earlier still import. Names are 1–100 characters; blank, too long and repeated (ignoring case) entries are dropped and counted, like invalid connections and variables.
2. **Export.** The Export / import tab lists "Document categories" (active and inactive) as a group, ticked by default.
3. **Import only adds.** A name this environment doesn't have is created (through the existing create endpoint), then deactivated if the package had it inactive. A name it already has, in any letter case, is left exactly as it is: no rename, no activation, no removal (categories are never deleted). It is part of the frontend, like connections and variables: no new endpoint, no API or OpenAPI change, no new package. Import stays administrator-only.
4. **Still not carried:** which template has which category. An imported template arrives uncategorised, as before.

## Addendum 2: categories travel in export / import packages (2026-11-16)

Requested by the product owner on 2026-11-16. This reverses decision 7 (and the consequence above): the Export / import tab of the Configuration drawer now carries document categories.

1. **By name.** The package file gets a `categories` section of `{ name, isActive }`. Ids are environment-local and never exported. Templates still don't carry their category, so an imported template arrives uncategorised.
2. **Export.** A "Document categories" group (active and inactive) sits first in the export list, ticked like the others.
3. **Import.** Names the environment lacks (compared ignoring case) are created; one exported as inactive is created and then deactivated. Categories already here are left exactly as they are: import never renames, activates, deactivates or removes one, whatever "How to apply" says. A name taken meanwhile (409) is skipped. Blank, over-long (100) and repeated names in the file are ignored and counted.
4. **Older packages.** `categories` is optional when reading, so packages made before this addendum still import.
5. **Where it lives.** Frontend only, like connections and variables: it uses the existing document-category endpoints. No API change, no migration, no package. Importing remains limited to administrators.
