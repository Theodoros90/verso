using Verso.Application;

namespace Verso.Api.Common;

/// <summary>
/// Stand-in identity until Entra ID sign-in is built: every request acts as the configured development
/// user (<c>DevUser:Id</c>, <c>DevUser:Name</c>). Program.cs refuses to start outside Development/Testing,
/// so this can never serve a real environment.
/// </summary>
public sealed class DevelopmentCurrentUser(IConfiguration configuration) : ICurrentUser
{
    public string Id { get; } = configuration["DevUser:Id"] is { Length: > 0 } id ? id : "dev.user";
    public string Name { get; } = configuration["DevUser:Name"] is { Length: > 0 } name ? name : "Development User";
}
