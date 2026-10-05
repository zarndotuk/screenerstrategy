namespace KlseBacktester.Models;

/// <summary>
/// Curated list of actively-traded KLSE (Bursa Malaysia) tickers.
/// Yahoo Finance uses the ".KL" suffix for all Bursa stocks.
/// Add or remove tickers as needed.
/// </summary>
public static class KlseTickers
{
    public static readonly List<(string Ticker, string Name, string Sector)> All = new()
    {
        // ── Banking & Finance ──
        ("1155.KL", "Malayan Banking (Maybank)",       "Finance"),
        ("1295.KL", "Public Bank",                     "Finance"),
        ("1023.KL", "CIMB Group",                      "Finance"),
        ("5819.KL", "Hong Leong Bank",                 "Finance"),
        ("1066.KL", "RHB Bank",                        "Finance"),
        ("5258.KL", "Bursa Malaysia",                  "Finance"),

        // ── Telecoms ──
        ("6888.KL", "Axiata Group",                    "Telecommunications"),
        ("4863.KL", "Telekom Malaysia",                "Telecommunications"),
        ("6012.KL", "Maxis",                           "Telecommunications"),
        ("5G.KL",   "CelcomDigi",                      "Telecommunications"),

        // ── Energy & Oil ──
        ("5183.KL", "Petronas Chemicals",              "Energy"),
        ("5681.KL", "Petronas Gas",                    "Energy"),
        ("0078.KL", "Petron Malaysia",                 "Energy"),
        ("5218.KL", "Dialog Group",                    "Energy"),

        // ── Utilities ──
        ("5264.KL", "Tenaga Nasional",                 "Utilities"),
        ("6033.KL", "Malakoff",                        "Utilities"),

        // ── Consumer / FMCG ──
        ("4197.KL", "Fraser & Neave Holdings",         "Consumer"),
        ("2836.KL", "Nestle Malaysia",                 "Consumer"),
        ("3026.KL", "Dutch Lady Milk",                 "Consumer"),
        ("5292.KL", "Heineken Malaysia",               "Consumer"),

        // ── Industrials / Conglomerate ──
        ("3816.KL", "MISC Berhad",                     "Industrial"),
        ("1961.KL", "IOI Corporation",                 "Plantation"),
        ("2445.KL", "Kuala Lumpur Kepong (KLK)",       "Plantation"),
        ("4065.KL", "PPB Group",                       "Conglomerate"),
        ("4863.KL", "Sime Darby",                      "Conglomerate"),

        // ── Technology ──
        ("0138.KL", "MY E.G. Services (MyEG)",         "Technology"),
        ("7084.KL", "Inari Amertron",                  "Technology"),
        ("0082.KL", "Frontken",                        "Technology"),
        ("5216.KL", "Vitrox",                          "Technology"),
        ("7113.KL", "Datasonic",                       "Technology"),

        // ── Healthcare ──
        ("5285.KL", "IHH Healthcare",                  "Healthcare"),
        ("7090.KL", "KPJ Healthcare",                  "Healthcare"),

        // ── Property / REIT ──
        ("5105.KL", "KLCC Property",                   "Property"),
        ("1651.KL", "IGB REIT",                        "REIT"),
        ("5227.KL", "Pavilion REIT",                   "REIT"),
        ("5235.KL", "Sunway REIT",                     "REIT"),
    };

    /// <summary>Just the ticker symbols, e.g. ["1155.KL", "1295.KL", ...]</summary>
    public static IEnumerable<string> Symbols => All.Select(x => x.Ticker);
}
