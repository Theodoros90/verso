using Microsoft.Extensions.Configuration;
using Verso.Infrastructure;

namespace Verso.Api.Tests;

/// <summary>
/// <c>Uploads:Retention</c> (ADR 0023) as the processes really read it: the shipped appsettings.json files through
/// <see cref="DependencyInjection.UploadOptionsFrom"/>. A value like <c>24:00:00</c> means 24 days to <see cref="TimeSpan"/>, not 24 hours.
/// </summary>
public sealed class UploadRetentionSettingsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Verso.slnx not found above the test output.");
    }

    private static IConfiguration Settings(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Theory]
    [InlineData("Verso.Worker")]
    [InlineData("Verso.Api")]
    public void Shipped_appsettings_keep_unused_uploads_for_24_hours(string project)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "src", project, "appsettings.json"), optional: false, reloadOnChange: false)
            .Build();

        Assert.Equal(TimeSpan.FromHours(24), DependencyInjection.UploadOptionsFrom(configuration).Retention);
    }

    [Fact]
    public void Default_retention_is_24_hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), new Verso.Application.Uploads.UploadOptions().Retention);
        Assert.Equal(TimeSpan.FromHours(24), DependencyInjection.UploadOptionsFrom(Settings()).Retention);
    }

    [Theory]
    [InlineData("24:00:00")] // 24 days
    [InlineData("8.00:00:00")]
    [InlineData("00:00:30")]
    [InlineData("00:00:00")]
    [InlineData("-01:00:00")]
    public void Retention_outside_1_minute_to_7_days_refuses_to_start(string value)
    {
        var e = Assert.Throws<InvalidOperationException>(() => DependencyInjection.UploadOptionsFrom(Settings(("Uploads:Retention", value))));
        Assert.Contains("Uploads:Retention", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:01:00", 0, 1)]
    [InlineData("1.00:00:00", 24, 0)]
    [InlineData("7.00:00:00", 168, 0)]
    public void Retention_within_range_is_used(string value, int hours, int minutes)
    {
        Assert.Equal(new TimeSpan(hours, minutes, 0), DependencyInjection.UploadOptionsFrom(Settings(("Uploads:Retention", value))).Retention);
    }
}
