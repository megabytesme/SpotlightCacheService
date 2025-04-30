using System.Text.Json.Serialization;

public class BatchResponse
{
    [JsonPropertyName("batchrsp")]
    public BatchRsp? Batchrsp { get; set; }
}

public class BatchRsp
{
    [JsonPropertyName("items")]
    public List<ItemContainer>? Items { get; set; }
}

public class ItemContainer
{
    [JsonPropertyName("item")]
    public string? Item { get; set; }
}

public class InnerItem
{
    [JsonPropertyName("ad")]
    public AdData? Ad { get; set; }
}

public class AdData
{
    [JsonPropertyName("landscapeImage")]
    public ImageData? LandscapeImage { get; set; }

    [JsonPropertyName("portraitImage")]
    public ImageData? PortraitImage { get; set; }

    [JsonPropertyName("copyright")]
    public string? Copyright { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("iconHoverText")]
    public string? IconHoverText { get; set; }
}

public class ImageData
{
    [JsonPropertyName("asset")]
    public string? Asset { get; set; }
}

public class CachedSpotlightImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? LandscapeUrl { get; set; }
    public string? PortraitUrl { get; set; }
    public string? LandscapePath { get; set; }
    public string? PortraitPath { get; set; }
    public string? LandscapePathCompressed { get; set; }
    public string? PortraitPathCompressed { get; set; }
    public string? Copyright { get; set; }
    public string? Title { get; set; }
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;
}
