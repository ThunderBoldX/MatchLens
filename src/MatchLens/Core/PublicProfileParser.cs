using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.IO;

namespace MatchLens;

// Parse only page content delivered to an ordinary public HTTP request. Script
// payloads, login forms and hidden blocks are not sources of player metrics.
public sealed class PublicHtml
{
    public List<string> Tokens {get;}=[];
    public List<HtmlElement> Elements {get;}=[];
    public List<(string Src,string Alt,string Class)> Images {get;}=[];
    public List<string> Links {get;}=[];
    public string Name {get;private set;}="";
    static readonly Regex chunks=new(@"<!--[\s\S]*?-->|<(script|style|noscript)\b[^>]*>[\s\S]*?</\1\s*>|<[^>]+>|[^<]+",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    static string Clean(string s)=>Regex.Replace(WebUtility.HtmlDecode(s),@"\s+"," ").Trim();
    static Dictionary<string,string> Attributes(string tag)=>Regex.Matches(tag,@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))").Cast<Match>()
        .GroupBy(m=>m.Groups[1].Value.ToLowerInvariant()).ToDictionary(g=>g.Key,g=>WebUtility.HtmlDecode(g.First().Groups[2].Success?g.First().Groups[2].Value:g.First().Groups[3].Success?g.First().Groups[3].Value:g.First().Groups[4].Value));
    public PublicHtml(string html)
    {
        var stack=new Stack<(string Tag,bool Hidden)>();bool inHeading=false;
        foreach(Match match in chunks.Matches(html))
        {
            var token=match.Value;
            if(token.StartsWith("<!--")||Regex.IsMatch(token,@"^<(script|style|noscript)\b",RegexOptions.IgnoreCase))continue;
            if(token.StartsWith('<'))
            {
                var tagMatch=Regex.Match(token,@"^<(/?)\s*([\w:-]+)");if(!tagMatch.Success)continue;
                var tag=tagMatch.Groups[2].Value.ToLowerInvariant();var closing=tagMatch.Groups[1].Value=="/";
                if(closing)
                {
                    if(stack.Any(x=>x.Tag==tag)){while(stack.Count>0){var item=stack.Pop();if(item.Tag==tag)break;}}
                    if(tag=="h1")inHeading=false;continue;
                }
                var attrs=Attributes(token);
                var hidden=(stack.Count>0&&stack.Peek().Hidden)||Regex.IsMatch(token,@"\shidden(?:\s|>|=)",RegexOptions.IgnoreCase)||attrs.GetValueOrDefault("aria-hidden")=="true"||Regex.IsMatch(attrs.GetValueOrDefault("style")??"",@"display\s*:\s*none|visibility\s*:\s*hidden",RegexOptions.IgnoreCase);
                var isVoid=tag is "img" or "br" or "hr" or "meta" or "link" or "input" or "source" or "wbr" or "area" or "base" or "embed" or "param" or "col";
                if(!isVoid&&!token.EndsWith("/>"))stack.Push((tag,hidden));
                if(hidden)continue;
                Elements.Add(new(tag,attrs,match.Index,Tokens.Count));
                if(tag=="h1")inHeading=true;
                if(tag=="a"&&attrs.TryGetValue("href",out var href))Links.Add(href);
                if(tag=="img")
                {
                    Images.Add((attrs.GetValueOrDefault("src")??"",attrs.GetValueOrDefault("alt")??"",attrs.GetValueOrDefault("class")??""));
                    if(attrs.GetValueOrDefault("alt") is {Length:>0} alt)Tokens.Add(Clean(alt));
                }
                continue;
            }
            if(stack.Count>0&&stack.Peek().Hidden)continue;
            var text=Clean(token);if(text.Length==0)continue;
            Tokens.Add(text);if(inHeading)Name=Name.Length==0?text:Name+" "+text;
        }
    }
}

public static class PublicProfileParser
{
    static string Key(string s)=>Regex.Replace(s.ToLowerInvariant(),@"[\s_:]+","");
    public static double? Number(string text)
    {
        // Labels are matched separately; no numeric guessing inside tooltips.
        var s=text.Trim().Replace("−","-").Replace("\u00a0","");
        if(!Regex.IsMatch(s,@"^[+-]?\d[\d,.]*(?:\s*(?:%|ms|мс|°|h|hrs|hours))?$",RegexOptions.IgnoreCase))return null;
        s=Regex.Replace(s,@"\s*(?:%|ms|мс|°|h|hrs|hours)$","",RegexOptions.IgnoreCase);
        if(Regex.IsMatch(s,@"^[+-]?\d{1,3}(?:,\d{3})+(?:\.\d+)?$"))s=s.Replace(",","");
        else if(s.Contains(',')&&!s.Contains('.'))s=s.Replace(',','.');
        return double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:null;
    }
    static double? After(IReadOnlyList<string> tokens,string label)
    {
        for(int i=0;i<tokens.Count;i++)
        {
            if(Key(tokens[i])!=Key(label))continue;
            for(int j=i+1;j<Math.Min(tokens.Count,i+4);j++)
            {
                if(Number(tokens[j]) is {} n)return n;
                if(tokens[j].Length>35||Regex.IsMatch(tokens[j],@"better than|average|player|rating|rate|kast|kills|matches",RegexOptions.IgnoreCase))break;
            }
        }
        return null;
    }
    static void Add(SourceCard c,string key,string label,double? value,string unit="",double min=0,double max=1000000)
    {
        if(value is not {} n||n<min||n>max||c.Numbers.Any(m=>m.Key==key))return;
        var metric=new NumericMetric(key,label,n,unit);c.Numbers.Add(metric);c.Metrics.Add(new(label,metric.Display));
    }
    static List<string> Section(List<string> tokens,string start,string? end=null)
    {
        var i=tokens.FindIndex(t=>Key(t)==Key(start));if(i<0)return [];
        var stop=end==null?-1:tokens.FindIndex(i+1,t=>Key(t)==Key(end));
        return tokens.Skip(i+1).Take(stop<0?tokens.Count-i-1:stop-i-1).ToList();
    }
    public static SourceCard Leetify(string id,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="LEETIFY");var page=new PublicHtml(html);
        c.AvatarUrl=page.Images.Select(i=>i.Src).FirstOrDefault(Avatars.TrustedUrl)??"";
        var body=Section(page.Tokens,"Last 30 Matches","Top Highlights");
        if(body.Any(t=>t.Contains("All 5v5 sources",StringComparison.OrdinalIgnoreCase)))c.Scope="Останні 30 матчів · усі джерела 5v5";
        var sample=After(body,"Matches");c.SampleCount=sample is >=1 and <=30&&sample==(int)sample?(int)sample:null;
        Add(c,"leetify_rating","Рейтинг Leetify",After(body,"Leetify Rating"),"",-50,50);
        Add(c,"winrate","Перемоги",After(body,"Win Rate"),"%",0,100);
        Add(c,"kd","K/D",After(body,"K/D"),"",0,20);
        Add(c,"kast","KAST",After(body,"KAST"),"%",0,100);
        Add(c,"aim","Прицілювання",After(body,"Aim")," / 100",0,100);
        Add(c,"utility","Гранати",After(body,"Utility")," / 100",0,100);
        Add(c,"positioning","Позиціонування",After(body,"Positioning")," / 100",0,100);
        Add(c,"opening","Відкриття раунду",After(body,"Opening"),"",-50,50);
        Add(c,"clutch","Клатчі",After(body,"Clutch"),"",-100,100);
        Add(c,"ttd","Час до шкоди",After(body,"Time to Damage")," мс",0,10000);
        Add(c,"crosshair","Розміщення прицілу",After(body,"Crosshair Placement"),"°",0,180);
        Add(c,"kills_match","Убивства / матч",After(body,"Kills per Match"),"",0,200);
        Add(c,"trade_success","Успішність розмінів",After(body,"Trade Kill Success"),"%",0,100);
        Add(c,"accuracy_spotted","Влучність по видимій цілі",After(body,"Accuracy (Enemy Spotted)"),"%",0,100);
        Add(c,"adr","ADR",After(body,"ADR"),"",0,1000);
        Add(c,"headshots","Хедшоти",After(body,"HS%"),"%",0,100);
        Add(c,"headshots","Хедшоти",After(body,"Headshot Kills"),"%",0,100);
        Add(c,"headshot_accuracy","Точність у голову",After(body,"Headshot Accuracy"),"%",0,100);
        Add(c,"counter_strafe","Правильний контрстрейф",After(body,"Proper Counter-Strafing"),"%",0,100);
        Add(c,"trade_attempts","Спроби розміну смерті",After(body,"Traded Death Attempts"),"%",0,100);
        Add(c,"smoke_kills","Кіли через смок / матч",After(body,"Kills through Smokes per Match"),"",0,200);
        var party=Section(page.Tokens,"Party Size","Operation Unearthed");
        Add(c,"solo","Соло-матчі",After(party,"Solo"),"%",0,100);
        Add(c,"party_small","Група 2–4 гравців",After(party,"2-4 stack"),"%",0,100);
        Add(c,"party_full","Повна група з 5 гравців",After(party,"5 stack"),"%",0,100);
        var ranks=Section(page.Tokens,"Ranks","Platforms");
        Add(c,"premier","Premier",After(ranks,"Premier"),"",0,50000);
        Add(c,"faceit_elo","FACEIT Elo",After(ranks,"FACEIT"),"",0,10000);
        var level=page.Images.Select(i=>Regex.Match(i.Alt,@"^FACEIT Level (\d{1,2})$",RegexOptions.IgnoreCase)).FirstOrDefault(m=>m.Success);
        if(level!=null)Add(c,"faceit_level","FACEIT рівень",Number(level.Groups[1].Value),"",1,10);
        var meta=Section(page.Tokens,"Steam","Show More");
        Add(c,"account_level","Рівень Steam",After(meta,"Account Level"),"",0,10000);
        for(var i=0;i<meta.Count-1;i++)if(Key(meta[i])==Key("Account Age")&&Regex.IsMatch(meta[i+1],@"^\d+ years?$"))c.Metrics.Add(new("Вік акаунта",meta[i+1].Replace("years","років").Replace("year","рік")));
        c.Maps=MapTables(html);
        c.Status=c.Metrics.Count>0?"Публічні дані отримано":Regex.IsMatch(html,@"profile.{0,30}(hidden|private)|profile not found",RegexOptions.IgnoreCase)?"Профіль прихований або не знайдений":"Відкрита сторінка не містить доступних показників";
        c.Note=c.Scope.Length>0?c.Scope+" · деталізація може вимагати входу":"Публічна сторінка Leetify";
        return c;
    }
    public static string? LeetifySubpage(string html,string section)
    {
        if(section is not ("maps" or "matches"))return null;
        foreach(var link in new PublicHtml(html).Links)
        {
            if(!Uri.TryCreate(new Uri("https://leetify.com"),link,out var u)||u.Host!="leetify.com"||u.Scheme!="https")continue;
            if(Regex.IsMatch(u.AbsolutePath,@"^/(?:@|%40)[a-zA-Z0-9_.~-]{1,100}/"+section+"/?$",RegexOptions.IgnoreCase))return u.GetLeftPart(UriPartial.Path);
        }
        return null;
    }
    public static void LeetifyDetails(SourceCard card,string id,string mapsHtml,string matchesHtml)
    {
        bool Same(string html)=>new PublicHtml(html).Tokens.Any(t=>t==id)||new PublicHtml(html).Links.Any(l=>l.Contains("steamcommunity.com/profiles/"+id,StringComparison.Ordinal));
        if(mapsHtml.Length>0&&Same(mapsHtml))
        {
            var tokens=Section(new PublicHtml(mapsHtml).Tokens,"Map Stats");
            var scope=tokens.Any(t=>t.Contains("Last 100 Matches",StringComparison.OrdinalIgnoreCase))?"Leetify · останні 100 матчів":"Leetify · період не вказано";
            var known=new HashSet<string>(new[]{"mirage","cache","nuke","dust2","inferno","ancient","anubis","train","overpass","vertigo","office","italy","shelter","fachwerk","alpine","stronghold","warden"});
            var result=new List<MapRate>();
            for(var i=0;i<tokens.Count;i++)
            {
                var name=tokens[i].ToLowerInvariant();if(!known.Contains(name))continue;
                var stop=tokens.FindIndex(i+1,t=>known.Contains(t.ToLowerInvariant()));var block=tokens.Skip(i+1).Take(stop<0?tokens.Count-i-1:stop-i-1).ToList();
                var rate=After(block,"Win Rate");var count=After(block,"Matches");
                if(rate is >=0 and <=100&&count is >=1 and <=1000000&&count==(int)count)result.Add(new(name,rate.Value,(int)count,scope));
            }
            card.MapRates=result.GroupBy(m=>m.Map).Where(g=>g.Count()==1).Select(g=>g.First()).ToList();
        }
        if(matchesHtml.Length>0&&Same(matchesHtml))
        {
            var history=new List<(DateTime Date,MatchPoint Point)>();
            foreach(Match table in Regex.Matches(matchesHtml,@"<table\b[^>]*>[\s\S]*?</table>",RegexOptions.IgnoreCase))
            {
                var rows=Regex.Matches(table.Value,@"<tr\b[^>]*>[\s\S]*?</tr>",RegexOptions.IgnoreCase).Cast<Match>()
                    .Select(r=>Regex.Matches(r.Value,@"<(th|td)\b[^>]*>([\s\S]*?)</\1>",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>string.Join(" ",new PublicHtml(m.Groups[2].Value).Tokens)).ToList()).Where(r=>r.Count>0).ToList();
                if(rows.Count<2)continue;var headers=rows[0].Select(Key).ToList();
                var dateIndex=headers.IndexOf("date");var mapIndex=headers.IndexOf("map");var ratingIndex=headers.FindIndex(h=>h is "rating" or "leetifyrating" or "leetifyleetifyrating");var sourceIndex=headers.IndexOf("source");
                if(dateIndex<0||mapIndex<0||ratingIndex<0)continue;
                foreach(var row in rows.Skip(1).Take(100))
                {
                    if(row.Count<=Math.Max(dateIndex,Math.Max(mapIndex,ratingIndex)))continue;
                    if(!DateTime.TryParseExact(row[dateIndex],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||Number(row[ratingIndex]) is not {} value||value is < -50 or >50)continue;
                    var scope=sourceIndex>=0&&row.Count>sourceIndex?row[sourceIndex]:"Джерело матчу не вказано";
                    history.Add((date,new(date.ToString("dd.MM")+" · "+row[mapIndex].ToUpperInvariant(),value,"",scope)));
                }
            }
            card.History=history.OrderBy(x=>x.Date).TakeLast(30).Select(x=>x.Point).ToList();
            card.RecentMatches=TrackingProfiles.MatchTables(matchesHtml,"LEETIFY");
        }
    }
    public static SourceCard CsStats(string id,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="CSSTATS");var page=new PublicHtml(html);
        c.AvatarUrl=page.Images.Select(i=>i.Src).FirstOrDefault(Avatars.TrustedUrl)??"";
        var i=page.Tokens.FindIndex(t=>t.StartsWith("Premier",StringComparison.OrdinalIgnoreCase));
        var rank=i<0?[]:page.Tokens.Skip(i).Take(35).ToList();
        var end=rank.Count>1?rank.FindIndex(1,t=>Key(t)=="faceit"||t.StartsWith("Competitive",StringComparison.OrdinalIgnoreCase)):-1;if(end>0)rank=rank.Take(end).ToList();
        Add(c,"premier","Premier",After(rank,"Current"),"",0,50000);
        Add(c,"premier_best","Найкращий Premier",After(rank,"Best"),"",0,50000);
        Add(c,"premier_wins","Перемоги в сезоні",After(rank,"Wins"),"",0,100000);
        foreach(var (key,label,source,unit,max) in new[]{("kd","K/D","K/D","",20d),("adr","ADR","ADR","",1000d),("kast","KAST","KAST","%",100d),("winrate","Перемоги","Win Rate","%",100d),("headshots","Хедшоти","HS%","%",100d),("matches","Матчі","Matches","",1000000d),("rating","Рейтинг","Rating","",10d)})
            Add(c,key,label,After(page.Tokens,source),unit,0,max);
        c.Maps=MapTables(html);
        // Never compare a CSStats lifetime aggregate with Leetify's recent form.
        c.Scope="Період і режим зі сторінки CSStats";
        c.Status=c.Metrics.Count>0?"Публічна частина профілю отримана":"Докладні дані недоступні без входу на сайт";
        c.Note="CSStats може вимагати вхід або перевірку браузера для K/D, ADR і матчів";
        return c;
    }
    public static SourceCard Scope(string id,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="SCOPE.GG");var page=new PublicHtml(html);
        foreach(var (key,label,source,unit,max) in new[]{("kd","K/D","K/D","",20d),("adr","ADR","ADR","",1000d),("kast","KAST","KAST","%",100d),("winrate","Перемоги","Winrate","%",100d),("aim","Прицілювання","Aim"," / 100",100d),("utility","Гранати","Utility"," / 100",100d),("ttd","Час до шкоди","Time to Damage"," мс",10000d)})
            Add(c,key,label,After(page.Tokens,source),unit,0,max);
        c.Maps=MapTables(html);c.Scope="Публічна сторінка SCOPE.GG";
        c.Status=c.Metrics.Count>0?"Публічні дані отримано":"Сайт не віддав статистику у відкритій сторінці";
        c.Note="Частина профілів потребує входу або завантаження у браузері";return c;
    }
    public static SourceCard SteamPage(string id,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="STEAM");var page=new PublicHtml(html);
        var account=ulong.TryParse(id,out var steam)?(steam-76561197960265728UL).ToString(CultureInfo.InvariantCulture):"";
        var identity=page.Elements.Any(e=>e.Attributes.GetValueOrDefault("data-miniprofile")==account&&e.Attributes.GetValueOrDefault("class")?.Contains("playerAvatar",StringComparison.OrdinalIgnoreCase)==true)
            ||page.Links.Any(l=>l=="https://steamcommunity.com/profiles/"+id);
        if(!identity){c.Status="Steam не підтвердив профіль гравця";return c;}
        c.AvatarUrl=page.Images.Select(i=>i.Src).FirstOrDefault(Avatars.TrustedUrl)??"";
        var name=System.Text.RegularExpressions.Regex.Match(html,@"<span\b[^>]*class=""[^""]*actual_persona_name[^""]*""[^>]*>([\s\S]*?)</span>",RegexOptions.IgnoreCase);
        if(name.Success)c.Metrics.Add(new("Ім’я Steam",string.Join(" ",new PublicHtml(name.Groups[1].Value).Tokens)));
        var level=page.Elements.FirstOrDefault(e=>e.Attributes.GetValueOrDefault("class")?.Split(' ').Contains("friendPlayerLevelNum")==true);
        if(level!=null&&level.TokenIndex<page.Tokens.Count)Add(c,"account_level","Рівень Steam",Number(page.Tokens[level.TokenIndex]),"",0,10000);
        // Only explicit ban notices are evidence. Absence never means zero bans.
        var ban=page.Tokens.FirstOrDefault(t=>Regex.IsMatch(t,@"^\d+ VAC bans? on record$",RegexOptions.IgnoreCase));
        if(ban!=null)c.Metrics.Add(new("VAC",ban));
        c.Scope="Публічний профіль Steam";c.Status=c.Metrics.Count>0?"Публічний профіль завантажено":"Steam не показав доступних даних";return c;
    }
    public static SourceCard Steam(string id,string text)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="STEAM");
        try
        {
            using var reader=XmlReader.Create(new StringReader(text),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=3*1024*1024});
            var root=XDocument.Load(reader).Root;
            if(root?.Name.LocalName!="profile"||root.Element("steamID64")?.Value!=id){c.Status="Steam не віддав відкритий профіль";return c;}
            c.AvatarUrl=root.Element("avatarFull")?.Value??root.Element("avatarMedium")?.Value??"";
            void Text(string tag,string label){if(root.Element(tag)?.Value is {Length:>0} value)c.Metrics.Add(new(label,value));}
            Text("steamID","Нік Steam");
            if(root.Element("memberSince")?.Value is {Length:>0} created)c.Metrics.Add(new("Дата створення",DateTime.TryParse(created,CultureInfo.GetCultureInfo("en-US"),DateTimeStyles.None,out var date)?date.ToString("dd.MM.yyyy"):created));
            if(root.Element("privacyState")?.Value is {Length:>0} privacy)c.Metrics.Add(new("Видимість профілю",privacy switch{"public"=>"Відкритий","private"=>"Приватний","friendsonly"=>"Лише для друзів",_=>privacy}));
            if(root.Element("vacBanned")?.Value is "0" or "1")c.Metrics.Add(new("VAC-бан",root.Element("vacBanned")!.Value=="1"?"Є позначка":"Немає позначки"));
            if(root.Element("tradeBanState")?.Value is {Length:>0} trade)c.Metrics.Add(new("Обмеження обміну",trade=="None"?"Немає позначки":trade));
            if(root.Element("onlineState")?.Value is {Length:>0} state)c.Metrics.Add(new("Статус",state switch{"online"=>"Онлайн","in-game"=>"У грі","offline"=>"Офлайн",_=>state}));
            c.Status="Публічний Steam-профіль отримано";c.Scope="Профіль Steam";c.Note="Відсутність позначки бану не є оцінкою трасту";
        }
        catch(XmlException){c.Status="Steam не віддав XML профілю";}
        return c;
    }
    public static List<MapRecord> MapTables(string html)
    {
        var tables=new List<CsStatsTable>();
        foreach(Match table in Regex.Matches(html,@"<table\b[^>]*>([\s\S]*?)</table>",RegexOptions.IgnoreCase))
        {
            var parsed=new CsStatsTable();
            foreach(Match row in Regex.Matches(table.Value,@"<tr\b[^>]*>([\s\S]*?)</tr>",RegexOptions.IgnoreCase))
            {
                var cells=Regex.Matches(row.Value,@"<(th|td)\b[^>]*>([\s\S]*?)</\1>",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>string.Join(" ",new PublicHtml(m.Groups[2].Value).Tokens)).ToList();
                if(cells.Count==0)continue;
                if(Regex.IsMatch(row.Value,@"<th\b",RegexOptions.IgnoreCase)&&parsed.Headers.Count==0)parsed.Headers=cells;else parsed.Rows.Add(cells);
            }
            tables.Add(parsed);
        }
        return CsStatsSnapshot.ParseMaps(tables);
    }
}
