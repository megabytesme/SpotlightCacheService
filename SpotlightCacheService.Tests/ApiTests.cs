using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpotlightCacheService.Services;
using Xunit;

namespace SpotlightCacheService.Tests;

public class ApiTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ExistingCacheAndImagesAreServedWithTheSameApiContract(string environment)
    {
        using var cache = new TestCache();
        cache.Seed();
        Directory.CreateDirectory(Path.Combine(cache.Path, "images"));
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(
            Path.Combine(cache.Path, "images", "existing.jpg"),
            imageBytes
        );
        using var factory = CreateFactory(cache, environment);
        using var client = factory.CreateClient(
            new() { BaseAddress = new Uri("https://localhost") }
        );

        Assert.Equal(
            "existing-image",
            Assert
                .Single(
                    factory
                        .Services.GetRequiredService<SpotlightCacheService.Services.SpotlightCacheService>()
                        .GetCachedData()
                )
                .Id
        );
        Assert.Equal("Spotlight Cache Service is running.", await client.GetStringAsync("/"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/spotlight-data");
        request.Headers.Add("Origin", "https://example.test");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var image = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("existing-image", image.GetProperty("id").GetString());
        Assert.Equal("existing.jpg", image.GetProperty("landscapePath").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("portraitPathCompressed").ValueKind);
        Assert.Equal(
            new[]
            {
                "cachedAt",
                "copyright",
                "id",
                "landscapePath",
                "landscapePathCompressed",
                "landscapeUrl",
                "portraitPath",
                "portraitPathCompressed",
                "portraitUrl",
                "title",
            },
            image.EnumerateObject().Select(p => p.Name).Order().ToArray()
        );
        Assert.Equal(imageBytes, await client.GetByteArrayAsync("/api/cached-images/existing.jpg"));
    }

    [Fact]
    public async Task DevelopmentUsesOpenApi31AndKeepsSwaggerUiAndDocumentRoute()
    {
        using var cache = new TestCache();
        using var factory = CreateFactory(cache, "Development");
        using var client = factory.CreateClient(
            new() { BaseAddress = new Uri("https://localhost") }
        );
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/v1/swagger.json" })
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(path));
            Assert.StartsWith("3.1.", document.RootElement.GetProperty("openapi").GetString());
            Assert.True(
                document
                    .RootElement.GetProperty("paths")
                    .TryGetProperty("/api/spotlight-data", out _)
            );
        }
        Assert.Contains("swagger-ui", await client.GetStringAsync("/swagger/index.html"));
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task ProductionDoesNotExposeDevelopmentDocumentation(string path)
    {
        using var cache = new TestCache();
        using var factory = CreateFactory(cache, "Production");
        using var client = factory.CreateClient(
            new() { BaseAddress = new Uri("https://localhost") }
        );
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        TestCache cache,
        string environment
    ) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["SpotlightSettings:CacheBasePath"] = cache.Path,
                            ["SpotlightSettings:ApiUrl"] = "https://spotlight.test/selection",
                        }
                    )
            );
            builder.ConfigureServices(services =>
                services
                    .AddHttpClient("SpotlightClient")
                    .ConfigurePrimaryHttpMessageHandler(() => new TestHandler("{}"))
            );
        });
}
