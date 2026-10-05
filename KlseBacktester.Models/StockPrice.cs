using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace KlseBacktester.Models;

public class StockPrice
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? Id { get; set; }

    [BsonElement("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [BsonElement("date")]
    public DateTime Date { get; set; }

    [BsonElement("open")]
    public decimal Open { get; set; }

    [BsonElement("high")]
    public decimal High { get; set; }

    [BsonElement("low")]
    public decimal Low { get; set; }

    [BsonElement("close")]
    public decimal Close { get; set; }

    [BsonElement("volume")]
    public long Volume { get; set; }

    [BsonElement("adjusted_close")]
    public decimal AdjustedClose { get; set; }
}
