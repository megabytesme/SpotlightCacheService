using Microsoft.Extensions.FileProviders;
using SpotlightCacheService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("SpotlightClient", client => { });
builder.Services.AddSingleton<SpotlightCacheService.Services.SpotlightCacheService>();
builder.Services.AddHostedService<CacheUpdateService>();

var AllowAnyOriginPolicy = "_allowAnyOriginPolicy";

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        name: AllowAnyOriginPolicy,
        policy =>
        {
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }
    );
});

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
    app.MapOpenApi("/swagger/{documentName}/swagger.json");
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/openapi/v1.json", "Spotlight Cache Service")
    );
}

app.UseHttpsRedirection();

app.UseCors(AllowAnyOriginPolicy);

var cacheBasePath =
    builder.Configuration.GetValue<string>("SpotlightSettings:CacheBasePath") ?? "cache";

var imageCachePath = Path.Combine(app.Environment.ContentRootPath, cacheBasePath, "images");

Directory.CreateDirectory(imageCachePath);

app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(imageCachePath),
        RequestPath = "/api/cached-images",
    }
);

app.MapGet("/", () => "Spotlight Cache Service is running.")
    .WithName("GetServiceStatus")
    .WithSummary("Check whether the Spotlight Cache Service is running.");

app.MapGet(
        "/api/spotlight-data",
        (
            SpotlightCacheService.Services.SpotlightCacheService cacheService,
            ILogger<Program> logger
        ) =>
        {
            try
            {
                var data = cacheService.GetCachedData();
                if (data == null || !data.Any())
                {
                    logger.LogWarning("Cache data requested but is empty or null.");
                    return Results.Ok(new List<CachedSpotlightImage>());
                }
                return Results.Ok(data);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error retrieving spotlight cache data.");
                return Results.Problem(
                    "An error occurred while retrieving cache data.",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        }
    )
    .WithName("GetSpotlightData")
    .Produces<List<CachedSpotlightImage>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithSummary("Get the latest cached Windows Spotlight images.");

app.Run();

public partial class Program { }
