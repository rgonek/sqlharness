using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

using SqlHarness.Dashboard;

namespace SqlHarness.Tests.Dashboard;

public sealed class DashboardAssetsTests
{
    private static readonly byte[] Placeholder = Encoding.UTF8.GetBytes("<html>placeholder</html>");

    private static DashboardAssets WithUi() => new(
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["index.html"] = Encoding.UTF8.GetBytes("<html><div id=\"root\"></div></html>"),
            ["assets/index-abc.js"] = Encoding.UTF8.GetBytes("console.log(1)"),
            ["assets/index-abc.css"] = Encoding.UTF8.GetBytes("body{}"),
            ["favicon.svg"] = Encoding.UTF8.GetBytes("<svg/>"),
        },
        Placeholder);

    [Fact]
    public void Existing_files_are_served_with_their_content_type()
    {
        var js = Assert.IsType<FileContentHttpResult>(WithUi().Resolve("assets/index-abc.js"));
        var css = Assert.IsType<FileContentHttpResult>(WithUi().Resolve("/assets/index-abc.css"));
        var svg = Assert.IsType<FileContentHttpResult>(WithUi().Resolve("favicon.svg"));

        Assert.Equal("text/javascript; charset=utf-8", js.ContentType);
        Assert.Equal("text/css; charset=utf-8", css.ContentType);
        Assert.Equal("image/svg+xml", svg.ContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sessions")]
    [InlineData("operations/42")]
    public void Client_routes_serve_the_spa_entry(string? path)
    {
        var index = Assert.IsType<FileContentHttpResult>(WithUi().Resolve(path));

        Assert.Equal("text/html; charset=utf-8", index.ContentType);
        Assert.Contains("id=\"root\"", Encoding.UTF8.GetString(index.FileContents.ToArray()));
    }

    [Theory]
    [InlineData("assets/index-old.js")]
    [InlineData("missing.css")]
    [InlineData("assets/nested/thing.png")]
    public void Missing_file_paths_are_404_not_the_spa_entry(string path) =>
        Assert.IsType<NotFound>(WithUi().Resolve(path));

    [Fact]
    public void Without_a_ui_the_placeholder_is_the_entry()
    {
        var assets = new DashboardAssets(new Dictionary<string, byte[]>(), Placeholder);

        Assert.False(assets.HasUi);
        var index = Assert.IsType<FileContentHttpResult>(assets.Resolve("sessions"));
        Assert.Equal(Placeholder, index.FileContents.ToArray());
    }

    [Fact]
    public void Windows_resource_separators_are_normalized()
    {
        var assets = new DashboardAssets(
            new Dictionary<string, byte[]> { [DashboardAssets.Normalize("assets\\x.js")] = [1] },
            Placeholder);

        Assert.IsType<FileContentHttpResult>(assets.Resolve("assets/x.js"));
    }

    [Fact]
    public void Shipped_build_embeds_the_ui()
    {
        // Fails when the Dashboard project was built with -p:SkipDashboardUi=true: releases must ship the UI.
        Assert.True(DashboardAssets.Default.HasUi, "SqlHarness.Dashboard.dll was built without the UI (SkipDashboardUi).");
    }
}