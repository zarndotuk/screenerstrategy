using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace KlseBacktester.Models;

public class StockInfo
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("ticker")]
    public string Ticker { get; set; } = string.Empty;

    /// <summary>Human-readable company name e.g. "Maybank"</summary>
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("sector")]
    public string Sector { get; set; } = string.Empty;

    [BsonElement("last_updated")]
    public DateTime LastUpdated { get; set; }
}
