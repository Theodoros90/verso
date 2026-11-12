using System.Text.Json;
using Verso.Application;
using Verso.Application.Executions;

namespace Verso.UnitTests.Executions;

public sealed class ApprovalTests
{
    private sealed class FakeUser(params string[] roles) : ICurrentUser
    {
        public string Id => "u";
        public string Name => "U";
        public IReadOnlyCollection<string> Roles { get; } = roles;
    }

    [Theory]
    [InlineData("operator", Roles.Operator, true)]
    [InlineData("operator", Roles.Auditor, false)]
    [InlineData("operator", Roles.Viewer, false)]
    [InlineData("auditor", Roles.Auditor, true)]
    [InlineData("operator", Roles.Admin, true)]
    [InlineData("auditor", Roles.Admin, true)]
    [InlineData("nobody", Roles.Operator, false)]
    public void CanDecide_NeedsTheApproverRole_OrAdmin(string approver, string role, bool expected) =>
        Assert.Equal(expected, Approval.CanDecide(new FakeUser(role), approver));

    [Fact]
    public void CanDecide_RoleNamesIgnoreCase() =>
        Assert.True(Approval.CanDecide(new FakeUser("operator"), "operator"));

    [Fact]
    public void Settings_FallBackToTheDefaults()
    {
        using var empty = JsonDocument.Parse("{}");
        using var set = JsonDocument.Parse("""{ "approver": "auditor", "escalateAfter": "5d" }""");

        Assert.Equal((Approval.DefaultApprover, Approval.DefaultEscalation), Approval.Settings(empty.RootElement));
        Assert.Equal(("auditor", "5d"), Approval.Settings(set.RootElement));
    }
}
