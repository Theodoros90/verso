namespace Verso.Application.Templates;

// Request and response shapes of the document category API. Documented in docs/api.md; changes must update it.

public sealed record CreateCategoryRequest(string? Name);

public sealed record RenameCategoryRequest(string? Name);

public sealed record DocumentCategoryDto(Guid Id, string Name, bool IsActive, int TemplateCount);
