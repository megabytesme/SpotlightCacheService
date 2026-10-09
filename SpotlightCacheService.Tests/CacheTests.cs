using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using SpotlightCacheService.Services;
using Xunit;
using CacheService = SpotlightCacheService.Services.SpotlightCacheService;

namespace SpotlightCacheService.Tests;

public class CacheTests
{
    [Fact]
    public async Task FetchDownloadsCompressesAndPersistsImagesWithUnknownUpstreamFields()
    {
        using var cache = new TestCache();
        var service = cache.CreateService(
            TestCache.Batch(
                """
                {"ad":{"landscapeImage":{"asset":"https://spotlight.test/landscape.jpg"},
                "portraitImage":{"asset":"https://spotlight.test/portrait.jpg"},
                "title":"A Spotlight image","copyright":"© Test","newUpstreamField":true}}
                """
            )
        );

        await service.FetchAndCacheSpotlightDataAsync();

        var image = Assert.Single(service.GetCachedData());
        Assert.Equal("A Spotlight image", image.Title);
        foreach (
            var filename in new[]
            {
                image.LandscapePath,
                image.PortraitPath,
                image.LandscapePathCompressed,
                image.PortraitPathCompressed,
            }
        )
        {
            Assert.NotNull(filename);
            using var decoded = SKBitmap.Decode(Path.Combine(cache.Path, "images", filename));
            Assert.NotNull(decoded);
            Assert.Equal(16, decoded.Width);
        }
        var saved = JsonSerializer.Deserialize<List<CachedSpotlightImage>>(
            await File.ReadAllTextAsync(cache.MetadataPath)
        );
        Assert.Equal(image.Id, Assert.Single(saved!).Id);
        Assert.Equal(image.Id, Assert.Single(cache.CreateService("{}").GetCachedData()).Id);
    }

    [Theory]
    [InlineData("{\"batchrsp\":{\"items\":[]},\"batchrsp\":{\"items\":[]}}")]
    [InlineData("{\"batchrsp\":{\"items\":[]}}")]
    [InlineData("invalid JSON")]
    public async Task UnusableBatchKeepsExistingMemoryAndDiskCache(string batch)
    {
        using var cache = new TestCache();
        cache.Seed();
        var original = await File.ReadAllTextAsync(cache.MetadataPath);
        var service = cache.CreateService(batch);

        await service.FetchAndCacheSpotlightDataAsync();

        Assert.Equal("existing-image", Assert.Single(service.GetCachedData()).Id);
        Assert.Equal(original, await File.ReadAllTextAsync(cache.MetadataPath));
    }

    [Fact]
    public async Task DuplicateInnerPropertiesKeepExistingCache()
    {
        using var cache = new TestCache();
        cache.Seed();
        var service = cache.CreateService(
            TestCache.Batch(
                """
                {"ad":{"title":"First","title":"Second",
                "landscapeImage":{"asset":"https://spotlight.test/landscape.jpg"},
                "portraitImage":{"asset":"https://spotlight.test/portrait.jpg"}}}
                """
            )
        );

        await service.FetchAndCacheSpotlightDataAsync();

        Assert.Equal("existing-image", Assert.Single(service.GetCachedData()).Id);
        Assert.Empty(Directory.GetFiles(Path.Combine(cache.Path, "images")));
    }

    [Fact]
    public async Task CancellationDuringDownloadKeepsExistingCache()
    {
        using var cache = new TestCache();
        cache.Seed();
        using var cancellation = new CancellationTokenSource();
        var service = cache.CreateService(
            TestCache.Batch(
                """
                {"ad":{"landscapeImage":{"asset":"https://spotlight.test/landscape.jpg"},
                "portraitImage":{"asset":"https://spotlight.test/portrait.jpg"}}}
                """
            ),
            () => cancellation.Cancel()
        );

        await service.FetchAndCacheSpotlightDataAsync(cancellation.Token);

        Assert.Equal("existing-image", Assert.Single(service.GetCachedData()).Id);
        Assert.DoesNotContain(
            Directory.GetFiles(Path.Combine(cache.Path, "images")),
            filename => filename.EndsWith(".tmp")
        );
    }
}

internal sealed class TestCache : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "spotlight-tests",
            Guid.NewGuid().ToString("N")
        );
    public string MetadataPath => System.IO.Path.Combine(Path, "data", "spotlight_cache.json");

    public void Seed()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(MetadataPath)!);
        File.WriteAllText(
            MetadataPath,
            JsonSerializer.Serialize(
                new[]
                {
                    new CachedSpotlightImage
                    {
                        Id = "existing-image",
                        Title = "Existing image",
                        LandscapePath = "existing.jpg",
                        Copyright = "© Existing",
                    },
                }
            )
        );
    }

    public CacheService CreateService(string batch, Action? cancelDownload = null) =>
        new(
            new TestClientFactory(batch, cancelDownload),
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["SpotlightSettings:ApiUrl"] = "https://spotlight.test/selection",
                        ["SpotlightSettings:CacheBasePath"] = Path,
                        ["SpotlightSettings:CompressionQuality"] = "35",
                    }
                )
                .Build(),
            NullLogger<CacheService>.Instance,
            new TestEnvironment()
        );

    public static string Batch(string innerItem) =>
        JsonSerializer.Serialize(
            new { batchrsp = new { items = new[] { new { item = innerItem } } }, extra = true }
        );

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, true);
    }
}

internal sealed class TestClientFactory(string batch, Action? cancelDownload) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new TestHandler(batch, cancelDownload));
}

internal sealed class TestHandler(string batch, Action? cancelDownload = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (request.RequestUri!.AbsolutePath == "/selection")
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(batch, Encoding.UTF8, "application/json"),
                }
            );
        cancelDownload?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        using var image = new SKBitmap(16, 16);
        image.Erase(SKColors.CornflowerBlue);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(encoded.ToArray()),
            }
        );
    }
}

internal sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "SpotlightCacheService";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = System.IO.Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = System.IO.Path.GetTempPath();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
