# ADR 0010: Configuration drawer and packages

- Status: accepted (implemented 2026-11-14, awaiting acceptance)
- Requested by the product owner on 2026-11-14: implement the gear (settings) button of the design reference `Verso-prototype-v14-Dynamic-Data-and-PDF.html`, front end and back end, and **move Global variables and Connections into it**.

## Context

The v14 prototype puts a gear in the top bar. It opens a drawer titled *Configuration* with three tabs, **Connections**, **Global variables** and **Export / import**, plus a label for the current environment (Development, Test, Production). Export / import moves a JSON package between environments: the user ticks what to include, secrets are never in it, and an import shows a summary before it changes anything. In the prototype everything is in memory. Here, templates and workflows are real records, while connections and global variables are still frontend-only and simulated (no backend stores them, no secret exists).

## Decision

1. **The gear replaces two pages.** The Connections and Global variables sidebar entries and routes are removed; both screens are tabs of the drawer (`features/configuration`). They keep their behaviour, share one small store (remembered in the browser's local storage, still simulated: no secret, no backend) so the Export / import tab sees the same data, and the old `/connections` and `/variables` URLs are gone.
2. **One file, two owners.** A package is one JSON document, `{ product: "Verso configuration package", schemaVersion: 1, environment, exportedAt, connections, variables, templates, workflows }`. The server owns `templates` and `workflows`; the SPA owns `connections` and `variables` and strips them before calling the server. A sensitive variable is exported without its value and a connection has no secret field.
3. **Templates and workflows are real and matched by key.**
   - `POST /api/v1/configuration/export` (any Verso role) returns the selected templates (their latest published version, or the draft when nothing is published) and workflows (the same rule). Template ids differ between environments, so a workflow's document steps name their template by `config.templateKey` instead of `templateId`.
   - `POST /api/v1/configuration/import?dryRun=` (**Administrator only**, because it writes both kinds) validates the whole package, then applies it in one transaction: any invalid item rejects the package and nothing changes. `dryRun=true` runs the same logic without saving, so the summary shown before the import is exactly what it does.
4. **Import never destroys.** A template that doesn't exist is created, and published as version 1 when the package carried a published version. An existing template whose content hash equals its latest published version is *unchanged*; otherwise a draft is started, filled and published as the next version (or left as a draft when the package carried a draft). An existing item that has an **unpublished draft** is *skipped* and reported, so no one's work is overwritten. Workflows are always imported as **drafts** (created, or saved into a new draft of an existing workflow) and never published: a workflow author reviews and publishes them here, which also runs the usual validation and folder path registration. Names and descriptions of existing items are not changed. Nothing is ever deleted; *Replace* applies only to connections and variables and never removes a template or workflow.
5. **References are remapped.** On import, `templateKey` becomes the local template's id (a template from the same package, or one that already exists). An unknown key is reported as a warning and the step is left without a template for the author to choose; the workflow still imports.
6. **Audit.** Every created or changed item writes its usual entry (`TemplateCreated`, `TemplateDraftCreated`, `TemplateDraftSaved`, `TemplatePublished`, `Workflow…`) with `viaImport: true`, plus one `ConfigurationExported` / `ConfigurationImported` entry (`entityType=Configuration`) with counts only.
7. **Limits.** At most 200 templates and 200 workflows per package, inside the usual 2 MB request limit.

## Consequences

- Connections and variables are not real configuration yet: exporting from one browser and importing into another moves synthetic data, and nothing uses them at run time. They will move to the server when the integrations they describe are approved.
- Moving a workflow between environments needs the templates it uses to be in the package or already present; otherwise its document steps need a template chosen after import.
- A template's name and description are set when it is created and can't be changed by import; this matches the rest of the API, which has no endpoint to rename.
- The package is not signed or encrypted; it holds layout and workflow definitions, no data and no secrets.
