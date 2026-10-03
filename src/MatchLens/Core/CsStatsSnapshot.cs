using System.Globalization;
using System.Text.RegularExpressions;

namespace MatchLens;
public sealed class CsStatsSnapshot
{
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string Url {get;set;}="";
    public string Text {get;set;}="";
    public DateTimeOffset ReceivedAt {get;set;}
    public List<Metric> Metrics {get;set;}=[];
    public List<CsStatsTable> Tables {get;set;}=[];
    public List<MapRecord> Maps {get;set;}=[];
    public string Status {get;set;}="";
    public bool Validate()
    {
        if(!SteamIds.Valid(Id)||!Uri.TryCreate(Url,UriKind.Absolute,out var u)||u.Scheme!="https"||u.Host!="csstats.gg"||u.AbsolutePath.TrimEnd('/')!="/player/"+Id)return false;
        if(Text==null||Text.Length>120000||string.IsNullOrWhiteSpace(Text)||Name==null||Name.Length>200||Tables==null||Tables.Count>30||Metrics==null||Metrics.Count>100)return false;
        if(Metrics.Any(m=>m==null||m.Label==null||m.Value==null||m.Label.Length>200||m.Value.Length>500))return false;
        if(Tables.Any(t=>t==null||t.Headers==null||t.Rows==null||t.Headers.Count>40||t.Rows.Count>300||t.Headers.Any(h=>h==null||h.Length>500)||t.Rows.Any(r=>r==null||r.Count>40||r.Any(v=>v==null||v.Length>1000))))return false;
        ReceivedAt=DateTimeOffset.Now;
        // Rebuild derived fields from visible table cells; never trust supplied maps.
        Maps=ParseMaps(Tables);
        Status=Regex.IsMatch(Text,@"please login to view player stats|confirm you're human to load|verify you're human|too many requests",RegexOptions.IgnoreCase)
            ?"Часткові дані · відкрийте статистику на сайті":"Знімок відкритої сторінки";
        return true;
    }
    static string Key(string s)=>Regex.Replace(s.ToLowerInvariant(),@"[\s_%]+","").Trim();
    public static List<MapRecord> ParseMaps(IEnumerable<CsStatsTable> tables)
    {
        foreach(var table in tables)
        {
            var h=table.Headers.Select(Key).ToList();
            int map=h.FindIndex(x=>x is "map" or "карта" or "мапа"),wins=h.FindIndex(x=>x is "wins" or "won" or "перемоги"),matches=h.FindIndex(x=>x is "matches" or "played" or "матчі");
            if(map<0||wins<0||matches<0)continue;
            var result=new List<MapRecord>();
            foreach(var row in table.Rows)
            {
                if(row.Count<=Math.Max(map,Math.Max(wins,matches)))continue;
                var name=Regex.Replace(row[map].Trim().ToLowerInvariant(),@"^(de_|cs_)","");
                if(!Regex.IsMatch(name,@"^[a-z][a-z0-9_ -]{1,30}$"))continue;
                if(!int.TryParse(row[wins].Replace(",","").Replace(" ",""),NumberStyles.None,CultureInfo.InvariantCulture,out var w)||!int.TryParse(row[matches].Replace(",","").Replace(" ",""),NumberStyles.None,CultureInfo.InvariantCulture,out var n)||n<=0||n>1000000||w<0||w>n)continue;
                result.Add(new(name,w,n));
            }
            // Duplicate map rows mean mixed scopes; don't invent one aggregate.
            if(result.Count>0&&result.Select(m=>m.Map).Distinct().Count()==result.Count)return result;
        }
        return [];
    }
    public SourceCard Card()=>new(){Name="CSSTATS",Accent="#E9B66B",Url=Url,Status=Status,FetchedAt=ReceivedAt,Metrics=Metrics,Note="Імпорт видимої сторінки браузера. Повний текст — нижче."};
}
public sealed class CsStatsTable
{
    public List<string> Headers {get;set;}=[];
    public List<List<string>> Rows {get;set;}=[];
}
public record MapRecord(string Map,int Wins,int Matches)
{
    public string Summary=>$"{(double)Wins/Matches:P0} · {Wins}/{Matches} перемог";
}
