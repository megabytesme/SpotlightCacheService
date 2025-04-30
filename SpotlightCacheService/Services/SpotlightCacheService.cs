using System.Net.Http.Headers;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace SpotlightCacheService.Services;

public class SpotlightCacheService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SpotlightCacheService> _logger;
    private readonly string _imageCachePath;
    private readonly string _metadataCachePath;
    private readonly string _spotlightApiUrl;
    private readonly int _compressionQuality;
    private List<CachedSpotlightImage> _cachedData = new();
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    public SpotlightCacheService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<SpotlightCacheService> logger,
        IWebHostEnvironment env
    )
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;

        _compressionQuality = _configuration.GetValue<int>(
            "SpotlightSettings:CompressionQuality",
            75
        );
        _logger.LogInformation("Image compression quality set to {Quality}", _compressionQuality);

        var cacheBasePath =
            _configuration.GetValue<string>("SpotlightSettings:CacheBasePath") ?? "cache";
        var contentRoot = env.ContentRootPath;
        var absoluteCacheBasePath = Path.Combine(contentRoot, cacheBasePath);

        _imageCachePath = Path.Combine(absoluteCacheBasePath, "images");
        _metadataCachePath = Path.Combine(absoluteCacheBasePath, "data", "spotlight_cache.json");
        _spotlightApiUrl =
            _configuration.GetValue<string>("SpotlightSettings:ApiUrl")
            ?? throw new InvalidOperationException("SpotlightSettings:ApiUrl not configured.");

        Directory.CreateDirectory(Path.GetDirectoryName(_metadataCachePath)!);
        Directory.CreateDirectory(_imageCachePath);

        LoadCacheFromDisk();
    }

    private void LoadCacheFromDisk()
    {
        _cacheLock.Wait();
        try
        {
            if (File.Exists(_metadataCachePath))
            {
                var json = File.ReadAllText(_metadataCachePath);
                _cachedData =
                    JsonSerializer.Deserialize<List<CachedSpotlightImage>>(json)
                    ?? new List<CachedSpotlightImage>();
                _logger.LogInformation(
                    "Loaded {Count} items from spotlight cache.",
                    _cachedData.Count
                );
            }
            else
            {
                _logger.LogInformation(
                    "Spotlight cache file not found. Will create on next fetch."
                );
                _cachedData = new List<CachedSpotlightImage>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading spotlight cache from disk.");
            _cachedData = new List<CachedSpotlightImage>();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task SaveCacheToDiskAsync()
    {
        await _cacheLock.WaitAsync();
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_cachedData, options);
            await File.WriteAllTextAsync(_metadataCachePath, json);
            _logger.LogInformation("Saved {Count} items to spotlight cache.", _cachedData.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving spotlight cache to disk.");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public List<CachedSpotlightImage> GetCachedData()
    {
        _cacheLock.Wait();
        try
        {
            return new List<CachedSpotlightImage>(_cachedData);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task FetchAndCacheSpotlightDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Spotlight data fetch...");
        var httpClient = _httpClientFactory.CreateClient("SpotlightClient");
        var newImageData = new List<CachedSpotlightImage>();

        try
        {
            var response = await httpClient.GetAsync(_spotlightApiUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var batchResponse = await response.Content.ReadFromJsonAsync<BatchResponse>(
                cancellationToken: cancellationToken
            );
            if (batchResponse?.Batchrsp?.Items == null || !batchResponse.Batchrsp.Items.Any())
            {
                _logger.LogWarning("Received empty or invalid item list from Spotlight API.");
                return;
            }

            foreach (var itemContainer in batchResponse.Batchrsp.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                if (string.IsNullOrWhiteSpace(itemContainer.Item))
                    continue;

                try
                {
                    var innerItem = JsonSerializer.Deserialize<InnerItem>(itemContainer.Item);
                    var ad = innerItem?.Ad;
                    var landscapeUrl = ad?.LandscapeImage?.Asset;
                    var portraitUrl = ad?.PortraitImage?.Asset;

                    if (
                        string.IsNullOrWhiteSpace(landscapeUrl)
                        || string.IsNullOrWhiteSpace(portraitUrl)
                    )
                    {
                        _logger.LogWarning("Skipping item due to missing image asset URL(s).");
                        continue;
                    }

                    var landscapeFilename = GenerateFilenameFromUrl(landscapeUrl);
                    var portraitFilename = GenerateFilenameFromUrl(portraitUrl);

                    var landscapeLocalPath = Path.Combine(_imageCachePath, landscapeFilename);
                    var portraitLocalPath = Path.Combine(_imageCachePath, portraitFilename);

                    var landscapeCompressedFilename = GetCompressedImageFilename(landscapeFilename);
                    var portraitCompressedFilename = GetCompressedImageFilename(portraitFilename);

                    var landscapeCompressedLocalPath = Path.Combine(
                        _imageCachePath,
                        landscapeCompressedFilename
                    );
                    var portraitCompressedLocalPath = Path.Combine(
                        _imageCachePath,
                        portraitCompressedFilename
                    );

                    bool landscapeDownloaded = await DownloadImageIfNotExistsAsync(
                        httpClient,
                        landscapeUrl,
                        landscapeLocalPath,
                        cancellationToken
                    );
                    bool portraitDownloaded = await DownloadImageIfNotExistsAsync(
                        httpClient,
                        portraitUrl,
                        portraitLocalPath,
                        cancellationToken
                    );

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    bool landscapeCompressed = false;
                    bool portraitCompressed = false;

                    if (File.Exists(landscapeLocalPath))
                    {
                        landscapeCompressed = await CompressImageAsync(
                            landscapeLocalPath,
                            landscapeCompressedLocalPath,
                            cancellationToken
                        );
                    }
                    if (File.Exists(portraitLocalPath))
                    {
                        portraitCompressed = await CompressImageAsync(
                            portraitLocalPath,
                            portraitCompressedLocalPath,
                            cancellationToken
                        );
                    }

                    if (File.Exists(landscapeLocalPath) && File.Exists(portraitLocalPath))
                    {
                        string copyrightInfo = ad?.Copyright ?? "";
                        if (
                            string.IsNullOrWhiteSpace(copyrightInfo)
                            && !string.IsNullOrWhiteSpace(ad?.IconHoverText)
                        )
                        {
                            var lines = ad.IconHoverText.Split(
                                new[] { "\\r\\n", "\r\n", "\n", "\r" },
                                StringSplitOptions.RemoveEmptyEntries
                            );
                            if (lines.Length > 1 && lines[1].Trim().StartsWith("©"))
                            {
                                copyrightInfo = lines[1].Trim();
                            }
                        }

                        newImageData.Add(
                            new CachedSpotlightImage
                            {
                                LandscapeUrl = landscapeUrl,
                                PortraitUrl = portraitUrl,

                                LandscapePath = landscapeFilename,
                                PortraitPath = portraitFilename,

                                LandscapePathCompressed = landscapeCompressed
                                    ? landscapeCompressedFilename
                                    : null,
                                PortraitPathCompressed = portraitCompressed
                                    ? portraitCompressedFilename
                                    : null,
                                Copyright = copyrightInfo,
                                Title = ad?.Title,
                            }
                        );
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Skipping item metadata for {LandUrl} / {PortUrl} as original file(s) not found after download attempt.",
                            landscapeUrl,
                            portraitUrl
                        );
                    }
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogWarning(jsonEx, "Failed to parse inner JSON item. Skipping.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing spotlight item.");
                }
            }

            await _cacheLock.WaitAsync(cancellationToken);
            try
            {
                _cachedData = newImageData;
            }
            finally
            {
                _cacheLock.Release();
            }
            await SaveCacheToDiskAsync();
            _logger.LogInformation(
                "Spotlight data fetch and cache update complete. Cached {Count} items.",
                newImageData.Count
            );
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "HTTP error fetching Spotlight data.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Spotlight data fetch cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Spotlight data fetch.");
        }
    }

    private string GetCompressedImageFilename(string originalFilename)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(originalFilename);
        var extension = Path.GetExtension(originalFilename);
        return $"{nameWithoutExtension}_q{_compressionQuality}{extension}";
    }

    private async Task<bool> CompressImageAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken
    )
    {
        if (File.Exists(outputPath))
        {
            _logger.LogDebug(
                "Compressed image {OutputPath} already exists. Skipping compression.",
                Path.GetFileName(outputPath)
            );
            return true;
        }

        _logger.LogInformation(
            "Compressing {Input} to {Output} (Quality: {Quality})...",
            Path.GetFileName(inputPath),
            Path.GetFileName(outputPath),
            _compressionQuality
        );

        try
        {
            using var image = await Image.LoadAsync(inputPath, cancellationToken);

            var encoder = new JpegEncoder { Quality = _compressionQuality };

            await image.SaveAsJpegAsync(outputPath, encoder, cancellationToken);

            _logger.LogDebug(
                "Successfully compressed {Input} to {Output}",
                Path.GetFileName(inputPath),
                Path.GetFileName(outputPath)
            );
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to compress image {Input} to {Output}",
                Path.GetFileName(inputPath),
                Path.GetFileName(outputPath)
            );

            if (File.Exists(outputPath))
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(
                        deleteEx,
                        "Failed to delete partially compressed file {Output}",
                        outputPath
                    );
                }
            }
            return false;
        }
    }

    private string GenerateFilenameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var filename = Path.GetFileName(uri.LocalPath);
            filename = string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
            return filename;
        }
        catch
        {
            return Guid.NewGuid().ToString() + ".jpg";
        }
    }

    private async Task<bool> DownloadImageIfNotExistsAsync(
        HttpClient client,
        string url,
        string localPath,
        CancellationToken cancellationToken
    )
    {
        if (File.Exists(localPath))
        {
            return true;
        }

        bool success = false;
        try
        {
            _logger.LogInformation(
                "Downloading image: {Url} to {Path}",
                url,
                Path.GetFileName(localPath)
            );
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

            using var response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            string tempPath = localPath + ".tmp";
            using (
                var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                await stream.CopyToAsync(fileStream, cancellationToken);
            }

            File.Move(tempPath, localPath, true);

            _logger.LogDebug("Successfully downloaded {FileName}", Path.GetFileName(localPath));
            success = true;
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogWarning(
                httpEx,
                "Failed to download image {Url}. HTTP Status: {StatusCode}",
                url,
                httpEx.StatusCode
            );
            success = false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download cancelled for {Url}", url);
            success = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download image {Url}", url);
            success = false;
        }
        finally
        {
            client.DefaultRequestHeaders.Accept.Clear();

            string tempPath = localPath + ".tmp";
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch { }
            }

            if (!success && File.Exists(localPath))
            {
                try
                {
                    File.Delete(localPath);
                }
                catch { }
            }
        }

        return success;
    }
}

public class CacheUpdateService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheUpdateService> _logger;
    private readonly TimeSpan _updateInterval;

    public CacheUpdateService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<CacheUpdateService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _updateInterval = TimeSpan.FromHours(
            configuration.GetValue<int>("SpotlightSettings:UpdateIntervalHours", 24)
        );
        _logger.LogInformation(
            "Cache Update Service configured with {Hours} hour interval.",
            _updateInterval.TotalHours
        );
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache Update Service starting.");

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await UpdateCache(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Cache Update Service waiting for next update cycle ({Interval})...",
                _updateInterval
            );
            await Task.Delay(_updateInterval, stoppingToken);

            if (!stoppingToken.IsCancellationRequested)
            {
                await UpdateCache(stoppingToken);
            }
        }
        _logger.LogInformation("Cache Update Service stopping.");
    }

    private async Task UpdateCache(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cache Update Service triggering cache update.");
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var cacheService = scope.ServiceProvider.GetRequiredService<SpotlightCacheService>();
            await cacheService.FetchAndCacheSpotlightDataAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during scheduled cache update.");
        }
    }
}
