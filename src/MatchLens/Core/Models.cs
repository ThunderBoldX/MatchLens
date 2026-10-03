using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchLens;

public class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public record Metric(string Label, string Value);
public record NumericMetric(string Key,string Label,double Value,string Unit="",string Scope="")
{
    public string Display=>Value.ToString(Key=="leetify_rating"?"+0.00;-0.00;0.00":"0.##",System.Globalization.CultureInfo.InvariantCulture)+Unit;
}
public record HtmlElement(string Tag,Dictionary<string,string> Attributes,int Offset,int TokenIndex);
public record MatchPoint(string Label,double Value,string Unit,string Scope);
public record MapRate(string Map,double WinRate,int Matches,string Scope);
public class SourceCard
{
    public string AvatarUrl {get;set;}="";
    public string Name { get; set; } = "";
    public string Accent { get; set; } = "#E9B66B";
    public string Status { get; set; } = "Очікування";
    public string Note { get; set; } = "";
    public string Diagnostic { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset? FetchedAt { get; set; }
    public List<Metric> Metrics { get; set; } = [];
    public List<NumericMetric> Numbers {get;set;}=[];
    public List<MapRecord> Maps {get;set;}=[];
    public List<MapRate> MapRates {get;set;}=[];
    public List<MatchPoint> History {get;set;}=[];
    public List<TrustAdjustment> TrustAdjustments {get;set;}=[];
    public List<ReviewRule> ReviewRules {get;set;}=[];
    public List<string> ReviewFlags {get;set;}=[];
    public List<RecentMatch> RecentMatches {get;set;}=[];
    public DateTimeOffset? TrustUpdated {get;set;}
    public string Scope {get;set;}="";
    public int? SampleCount {get;set;}
    public string EmptyMessage => Metrics.Count==0?Status:"";
    public int MetricCount=>Metrics.Count;
    public string Footer => L.T( FetchedAt is {} t ? $"Оновлено {t.ToLocalTime():HH:mm} · {Note}" : Note);
}
public class Player : Notify
{
    public System.Windows.Media.ImageSource? Avatar {get;set;}
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Гравець";
    public string Team { get; set; } = "?";
    public string Evidence { get; set; } = "Steam · непідтверджений";
    public bool Confirmed { get; set; }
    public bool IsLocal { get; set; }
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string Subtitle => L.T(IsLocal?"ВИ · ":"")+(Team is "CT" or "T"?Team+" · ":"")+L.T(Evidence);
    public string ShortId => Id.Length > 6 ? "…" + Id[^6..] : Id;
    public List<SourceCard> Sources { get; set; } = [];
    public CsStatsSnapshot? Snapshot {get;set;}
    public LiveMatchStats? Live {get;set;}
    public string StatsSummary => L.T( Sources.SelectMany(s=>s.Numbers).FirstOrDefault(n=>n.Key=="kd") is {} kd?$"K/D {kd.Display} · {Sources.Sum(s=>s.Metrics.Count)} показників":Sources.Any(s=>s.Metrics.Count>0)?$"{Sources.Sum(s=>s.Metrics.Count)} відкритих показників":Sources.Any(s=>s.Status.StartsWith("Завантаження")||s.Status.StartsWith("Пошук"))?"Статистика завантажується…":"Публічна статистика недоступна");
    public string IdentityLabel=>L.T(IsLocal?"ЦЕ ВИ":"STEAM-ПРОФІЛЬ");
    public string TrustLabel=>L.T(Sources.FirstOrDefault(c=>c.Name=="CSTRACKER")?.Numbers.FirstOrDefault(n=>n.Key=="trust") is {} trust?$"Траст cstracker: {trust.Display}":"Траст cstracker: немає даних");
    public void Refresh() { Changed(nameof(Name)); Changed(nameof(Initial)); Changed(nameof(Subtitle)); Changed(nameof(Sources)); Changed(nameof(Avatar)); Changed(nameof(StatsSummary));Changed(nameof(IdentityLabel));Changed(nameof(TrustLabel)); }
}
public record LiveMatchStats(int? Kills,int? Deaths,int? Assists,int? Mvps,int? Score)
{
    public static LiveMatchStats? Read(JsonElement player)
    {
        var d=player.At("match_stats");if(d.ValueKind!=JsonValueKind.Object)return null;
        int? N(string key)=>d.At(key).Num() is {} n&&n>=0&&n<=100000&&(int)n==n?(int)n:null;
        return new(N("kills"),N("deaths"),N("assists"),N("mvps"),N("score"));
    }
}
public record CoplayEntry(string Id, string Name, long Timestamp);
public record RosterEntry(string Id, string Name, string Team, bool Confirmed, string Evidence);
public class MatchState
{
    public bool Active { get; set; }
    public bool Demo { get; set; }
    public string Map { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Phase { get; set; } = "";
    public string LocalId { get; set; } = "";
    public int CtScore { get; set; }
    public int TScore { get; set; }
    public int? RoundNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastGsi { get; set; }
    public int Generation { get; set; }
    public List<RosterEntry> Roster { get; set; } = [];
    public Dictionary<string,LiveMatchStats> LivePlayers {get;set;}=[];
    public string ModeLabel => L.T(Mode switch { "competitive" => "Змагальний / Premier", "casual" => "Casual", "deathmatch" => "Deathmatch", "scrimcomp2v2" => "Wingman", "gungameprogressive" => "Arms Race", "custom" => "Community", "" => "Режим не вказано", _ => Mode });
    public MatchState Copy() => new() { Active=Active, Demo=Demo, Map=Map, Mode=Mode, Phase=Phase, LocalId=LocalId, CtScore=CtScore, TScore=TScore, RoundNumber=RoundNumber, StartedAt=StartedAt, LastGsi=LastGsi, Generation=Generation, Roster=[..Roster],LivePlayers=new(LivePlayers) };
}
public static class J
{
    public static JsonElement At(this JsonElement node, params string[] path) { foreach(var key in path) { if(node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key,out node)) return default; } return node; }
    public static string Str(this JsonElement node, string fallback="") => node.ValueKind is JsonValueKind.String ? node.GetString() ?? fallback : node.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? node.ToString() : fallback;
    public static double? Num(this JsonElement node) => double.TryParse(node.Str(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    public static int Int(this JsonElement node, int fallback=0) => node.Num() is {} n ? (int)n : fallback;
    public static IEnumerable<JsonElement> Items(this JsonElement node) => node.ValueKind==JsonValueKind.Array ? node.EnumerateArray() : [];
}
public static class SteamIds
{
    public const ulong Base = 76561197960265728UL;
    public static bool Valid(string id) => ulong.TryParse(id,out var n) && n>Base && n<=Base+uint.MaxValue;
    public static List<string> Parse(string text)
    {
        var ids = new List<string>();
        foreach(Match m in Regex.Matches(text,@"(?<!\d)7656119\d{10}(?!\d)")) if(Valid(m.Value)) ids.Add(m.Value);
        foreach(Match m in Regex.Matches(text,@"STEAM_[0-5]:([01]):(\d+)")) if(ulong.TryParse(m.Groups[2].Value,out var account) && account<=uint.MaxValue/2) ids.Add((Base+account*2+ulong.Parse(m.Groups[1].Value)).ToString());
        foreach(Match m in Regex.Matches(text,@"\[U:1:(\d+)\]")) if(uint.TryParse(m.Groups[1].Value,out var account) && account>0) ids.Add((Base+account).ToString());
        return ids.Distinct().Take(64).ToList();
    }
}
