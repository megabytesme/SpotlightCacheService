# SpotlightCacheService

SpotlightCacheService is a .NET 10 API which gets the latest Windows Spotlight images directly from Microsoft's V4 API and caches them locally. It provides the original landscape and portrait images, along with smaller JPEG variants, so they can be used on a website or in an app without downloading them from Microsoft every time.

I use this for the Spotlight backgrounds on [my website](https://megabytesme.ddns.net).

## Features

- **Local Image Caching:** Downloads both landscape and portrait images, and serves them directly through the API.
- **Compressed Image Variants:** Creates smaller JPEG copies using SkiaSharp, with configurable compression quality. Original images are kept as they are.
- **Image Information:** Returns the title, copyright, original URLs, cached filenames and cache date for each image.
- **Automatic Updates:** Fetches the latest images on startup, then checks again at the configured interval.
- **Persistent Cache:** Loads the existing cache on startup. If a refresh fails or returns no usable images, the previous cache is kept.
- **Docker Support:** Includes a Dockerfile using the .NET 10 SDK and ASP.NET Core runtime images.

## Build Guide

### Prerequisites

- .NET 10 SDK
- Windows, Linux or macOS
- Docker (Optional, for running in a container)

### Running the API

1. Clone the repository:

   ```sh
   git clone https://github.com/megabytesme/SpotlightCacheService.git
   cd SpotlightCacheService
   ```

2. Build the solution:

   ```sh
   dotnet build SpotlightCache.sln -c Release --maxcpucount
   ```

3. Start the API:

   ```sh
   dotnet run --project SpotlightCacheService --launch-profile http
   ```

The API will be available at `http://localhost:5202`. Swagger UI is available at `/swagger` when running in Development, with the OpenAPI document at `/openapi/v1.json`. The existing `/swagger/v1/swagger.json` URL also works.

The first refresh starts after a five second delay. Production checks for updates every hour by default; the Development settings use a 24 hour interval.

### Running with Docker

```sh
docker build -t spotlight-cache-service .
docker run -d --name spotlight-cache-service \
  --restart unless-stopped \
  -p 8080:8080 \
  -v spotlight-cache-data:/app/cache \
  spotlight-cache-service
```

The API will be available at `http://localhost:8080`. The named volume keeps downloaded images and metadata between container replacements. If replacing an existing deployment, use its existing cache volume.

### Publishing for Linux

```sh
dotnet publish SpotlightCacheService/SpotlightCacheService.csproj \
  -c Release -r linux-arm64 --self-contained false \
  --maxcpucount -o artifacts/publish
```

Use `linux-x64` instead for an x64 server. Install the .NET 10 ASP.NET Core runtime, then run `dotnet SpotlightCacheService.dll` from the publish directory. The SkiaSharp Linux native library is included in the publish output.

## Configuration

Settings are in `SpotlightCacheService/appsettings.json`, with Development overrides in `appsettings.Development.json`.

| Setting | Default | Description |
|---------|---------|-------------|
| `SpotlightSettings:ApiUrl` | Microsoft V4 selection API, using `en-GB` | The URL used to request Spotlight data. |
| `SpotlightSettings:CacheBasePath` | `cache` | Cache directory, relative to the application's content root unless an absolute path is used. |
| `SpotlightSettings:UpdateIntervalHours` | `1` | Time between refreshes, in hours. Development overrides this to `24`. |
| `SpotlightSettings:CompressionQuality` | `35` | JPEG quality for compressed variants. Higher values produce larger files with less compression. |

Settings can also be overridden with environment variables, using two underscores in place of the colon. For example, `SpotlightSettings__UpdateIntervalHours=6` changes the refresh interval to six hours.

Metadata is stored in `cache/data/spotlight_cache.json`, and images are stored in `cache/images`. The metadata lists the latest successful batch. Older downloaded images are kept on disk, and existing files are reused rather than downloaded or compressed again.

## API Usage

| Endpoint | Description |
|----------|-------------|
| `GET /` | Returns `Spotlight Cache Service is running.` |
| `GET /api/spotlight-data` | Returns a JSON array containing the current cached images. Returns an empty array if no images have been cached yet. |
| `GET /api/cached-images/{filename}` | Serves an original or compressed cached image. |

Each image includes `id`, `landscapeUrl`, `portraitUrl`, `landscapePath`, `portraitPath`, `landscapePathCompressed`, `portraitPathCompressed`, `copyright`, `title` and `cachedAt`.

The path fields contain filenames. Simply add `/api/cached-images/` before the filename to request the image. For example, `landscapePathCompressed` might be `example_q35.jpg`, which can be requested at `/api/cached-images/example_q35.jpg`. Compressed paths can be `null` if compression failed, so use the original path as a fallback.

```sh
curl http://localhost:8080/api/spotlight-data
```

CORS allows any origin, so the API can be called from a separate frontend. In production, HTTPS can be handled by a reverse proxy in front of the container.

## Testing

```sh
dotnet test SpotlightCache.sln -c Release --maxcpucount
```

Tests cover the existing cache and API response format, original and compressed image downloads, invalid and duplicate JSON, cancelled refreshes, and Development-only OpenAPI documentation. They use a local HTTP handler and temporary cache directories, so Microsoft's API is not needed to run them.

## Contact

For any questions, suggestions or bug reports, please [open an issue](https://github.com/megabytesme/SpotlightCacheService/issues).
