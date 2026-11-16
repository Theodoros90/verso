# ADR 0013: Document categories

- Status: accepted (implemented 2026-11-16, awaiting acceptance)
- Requested by the product owner on 2026-11-16: templates should be organised by document category (for example Invoices, Statements, Letters), with the categories maintained in the Configuration drawer.

## Context

Templates were a flat list ordered by key. With more than a handful of templates there was no way to group or find them by kind of document. `Template.DocumentType` exists, but it is a fixed value (`Invoice` in the MVP) and not something users maintain. As elsewhere, nothing that executions, documents or audit entries refer to may be deleted (`tools/db/least-privilege.sql` denies DELETE on the `templates` schema).

## Decision

1. **A new entity, `DocumentCategory`**, in the Templates module (schema `templates`): `Id`, `Name` (1 to 100 characters), `IsActive`, `CreatedAt/By`. Names are unique case-insensitively (after trimming). `Template` gets a nullable `CategoryId`. One additive migration (`AddDocumentCategories`); existing templates stay uncategorised.
2. **Flat, optional, one per template.** No nesting, no per-category permissions, no multiple categories on one template. A template without a category is valid.
3. **Never deleted, only deactivated.** A deactivated category can't be assigned to a template any more (a template that already has it keeps it and still shows it), is left out of the default list and the pickers, and can be activated again. Its name stays reserved.
4. **API.** `GET /api/v1/document-categories` (`?includeInactive=true`), `POST`, `PUT /{id}` (rename), `POST /{id}/deactivate`, `POST /{id}/activate`; reads for every role, changes need the TemplateAuthoring role. `PUT /api/v1/templates/{id}/category` assigns or clears (`{ "categoryId": null }`); `POST /api/v1/templates` takes an optional `categoryId`; template DTOs carry `categoryId` and `categoryName`; `GET /api/v1/templates` takes `?categoryId=<guid>|none`. Errors: 400 `validation` (`name`, `categoryId`), 409 `category.name_taken`, 404 `category.not_found`. Repeating deactivate / activate, or setting the category a template already has, changes nothing and writes no audit entry.
5. **Audit.** `CategoryCreated`, `CategoryRenamed`, `CategoryDeactivated`, `CategoryActivated` (`entityType=DocumentCategory`) and `TemplateCategoryChanged` (`entityType=Template`, from and to), each written in the same save as the change.
6. **Category changes are metadata, not content.** Assigning a category does not touch the draft or any published version, so no new version, ETag or content hash is involved. In the designer the picker saves immediately, separately from the draft.
7. **Not carried by export / import.** Packages (ADR 0010) don't include categories: a category is an environment-local list, and an imported template arrives uncategorised.
8. **UI.** A "Document categories" tab, first in the Configuration drawer (add, rename, deactivate, activate, template counts). The New template dialog has an optional Category select; the template list shows a category chip and has a category filter (including "No category"); the designer has a category picker.

## Consequences

- There is no permanent delete. A mistaken category is deactivated, and its name can't be reused for a different category.
- The list filter can only select active categories, so templates in a deactivated category can't be listed by category; activate the category to filter on it. (Deferred.)
- The `TemplateCreated` audit entry does not record the initial `categoryId`; only later changes are audited. (Deferred.)
- Assigning a category and deactivating it at the same moment can race: the assignment is checked, then saved. The worst outcome is one template holding a just-deactivated category, which is harmless. (Accepted.)
- Categories are not in export / import packages, and are not on workflows, executions or documents. Reporting by category is out of scope.
