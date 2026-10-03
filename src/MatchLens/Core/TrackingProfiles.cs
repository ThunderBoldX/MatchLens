using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatchLens;

public record TrustAdjustment(string Label,double Delta,string Key)
{
    public string Display=>Delta.ToString("+0.0;-0.0;0.0",CultureInfo.InvariantCulture)+"%";
}
public record ReviewRule(string Key,double Cutoff,bool Lower,string Scope)
{
    public bool Unusual(double value)=>Lower?value<Cutoff:value>Cutoff;
}
public record RecentMatch(DateTimeOffset Date,string Map,string Result,string Score,int? Kills=null,int? Deaths=null,int? Assists=null,double? Rating=null,string Source="",string Url="")
{
    public string Heading=>$"{Date:dd.MM.yyyy} · {Map.ToUpperInvariant()}";
    public string Detail=>string.Join(" · ",new[]{Result,Score,Kills.HasValue?$"K/D/A {Kills}/{Deaths?.ToString()??"—"}/{Assists?.ToString()??"—"}":"",Rating.HasValue?$"Рейтинг {Rating:0.##}":"",Source}.Where(s=>s.Length>0));
}

// Only visible public markup and attributes that describe its displayed charts.
// No script hydration, login refresh, private endpoints or inferred trust formula.
public static class TrackingProfiles
{
    static string Key(string text)=>Regex.Replace(text.ToLowerInvariant(),@"[\s_/:&.-]+","");
    static double? Num(string text)=>PublicProfileParser.Number(Regex.Replace(text.Trim(),@"^\$|(?<=\d)\s*s$",""));
    static void Add(SourceCard c,string key,string label,double? value,string unit,string scope,double max=1000000)
    {
        if(value is not {} n||!double.IsFinite(n)||n<0||n>max||c.Numbers.Any(m=>m.Key==key))return;
        var metric=new NumericMetric(key,label,n,unit,scope);c.Numbers.Add(metric);c.Metrics.Add(new(label,metric.Display));
    }
    static (double? Value,int Index) After(List<string> tokens,string label)
    {
        var i=tokens.FindIndex(t=>Key(t)==Key(label));if(i<0)return(null,-1);
        for(var j=i+1;j<Math.Min(tokens.Count,i+4);j++)
        {
            if(Num(tokens[j]) is {} n)return(n,j);
            if(tokens[j].StartsWith("//")||tokens[j].Length>50)break;
        }
        return(null,-1);
    }
    static readonly Dictionary<string,(string Key,string Label,string Unit)> detailed=new(StringComparer.OrdinalIgnoreCase)
    {
        ["Win rate"]=("detail_winrate","Перемоги · всі матчі","%"),["Enemy damage"]=("enemy_damage","Шкода суперникам",""),
        ["First kills"]=("first_kills","Перші вбивства",""),["Trade kills"]=("trade_kills","Розмінні вбивства",""),["Bhop success"]=("bhop","Успішний bunny hop","%"),
        ["Accuracy"]=("detail_accuracy","Точність · всі матчі","%"),["Spray accuracy"]=("spray_accuracy","Точність спрею","%"),
        ["Preaim"]=("detail_preaim","Preaim · всі матчі","°"),["Aim offset"]=("detail_aim_offset","Рух прицілу · всі матчі","°"),
        ["Spot to damage"]=("detail_ttd","Час до шкоди · всі матчі"," мс"),["Spot to kill"]=("ttk","Час до вбивства"," мс"),["Counter-strafing"]=("counter_strafe","Контрстрейф","%"),
        ["Grenade throws"]=("grenades","Кинуто гранат",""),["Flash assists"]=("flash_assists","Асисти флешками",""),["Enemies flashed / flash"]=("enemies_flashed","Засліплено / флешка",""),
        ["Avg flash duration"]=("flash_duration","Засліплення суперника"," с"),["Util dmg / match"]=("utility_damage","Шкода гранатами / матч",""),
        ["HE dmg / throw"]=("he_damage","Шкода HE / кидок",""),["Fire dmg / throw"]=("fire_damage","Шкода вогнем / кидок",""),["Unused util on death"]=("unused_utility","Втрачено гранат / смерть"," $"),
        ["AFK time / match"]=("afk","AFK / матч"," с"),["Teamkills / match"]=("teamkills","Тімкіли / матч",""),["Team damage / match"]=("team_damage","Шкода своїм / матч",""),
        ["Avg teammates flashed"]=("team_flashes","Засліплено своїх / флешка",""),["Teammate flash duration"]=("team_flash_duration","Засліплення союзника"," с"),
        ["Input automation / 100M"]=("input_automation","Автоматизація / 100 матчів",""),["Vote kicked / 100M"]=("vote_kicks","Кіки голосуванням / 100",""),["Team DMG kicks / 100M"]=("damage_kicks","Кіки за шкоду своїм / 100","")
    };
    static readonly Dictionary<string,(string Key,string Label,string Unit)> telemetry=new(StringComparer.OrdinalIgnoreCase)
    {
        ["TTD"]=("ttd","Час до шкоди"," мс"),["Preaim"]=("preaim","Preaim","°"),["Aim Offset"]=("aim_offset","Рух прицілу","°"),
        ["K/D Ratio"]=("kd","K/D",""),["HLTV Rating"]=("hltv_rating","Рейтинг HLTV",""),["KAST"]=("kast","KAST","%"),["ADR"]=("adr","ADR",""),["Accuracy"]=("accuracy","Точність у полі зору","%")
    };
    static (string Key,string Label) Reason(string label)=>Key(label) switch
    {
        "teamdamage"=>("team_damage","Шкода союзникам"),"teamkills"=>("teamkills","Вбивства союзників"),"headshotkills"=>("headshots","Хедшоти"),
        "wallbangkills"=>("wallbangs","Вбивства крізь стіни"),"teammatesflashed"=>("team_flashes","Засліплення союзників"),"afktime"=>("afk","Час AFK"),
        "teammates"=>("teammates","Історія союзників"),"ttd"=>("ttd","Час до шкоди"),"preaim"=>("preaim","Preaim"),"accuracy"=>("accuracy","Точність"),
        "teamdmgkicks"=>("damage_kicks","Кіки за шкоду союзникам"),"votekicked"=>("vote_kicks","Кіки голосуванням"),"faceitelo"=>("faceit_elo","Активність та Elo FACEIT"),
        _=>("",label)
    };
    public static SourceCard CsTracker(string id,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="CSTRACKER");var page=new PublicHtml(html);
        // The actual site identifies its player in public OG metadata and section links.
        var identity=page.Elements.Any(e=>(e.Attributes.GetValueOrDefault("hx-get")??"").StartsWith("/players/"+id+"/",StringComparison.Ordinal))
            ||page.Elements.Any(e=>e.Tag=="meta"&&e.Attributes.GetValueOrDefault("property")=="og:image"&&e.Attributes.GetValueOrDefault("content")=="https://cstracker.gg/og/players/"+id)
            ||page.Links.Any(l=>l=="https://steamcommunity.com/profiles/"+id);
        if(!identity){c.Status="Профіль не підтверджено або статистика прихована";return c;}
        c.AvatarUrl=page.Images.Select(i=>i.Src).FirstOrDefault(Avatars.TrustedUrl)??"";
        var latest=page.Tokens.Select(t=>Regex.Match(t,@"^latest (\d{1,2}) matches$",RegexOptions.IgnoreCase)).FirstOrDefault(m=>m.Success);
        c.SampleCount=latest!=null?int.Parse(latest.Groups[1].Value):null;c.Scope=c.SampleCount is {} count?$"CSTRACKER · останні {count} матчів":"CSTRACKER · вибірка не вказана";
        var begin=page.Tokens.FindIndex(t=>Key(t)=="trustrating");var end=begin<0?-1:page.Tokens.FindIndex(begin+1,t=>Key(t)=="ttd");
        var trust=begin<0?[]:page.Tokens.Skip(begin).Take(end<0?1:end-begin).ToList();
        Add(c,"trust","Траст cstracker",After(trust,"trust rating").Value,"%",c.Scope,100);
        for(var i=0;i<trust.Count-1;i++)
        {
            if(Regex.IsMatch(trust[i],@"^[+-]\d+(?:\.\d+)?%$")&&Num(trust[i]) is {} delta&&Math.Abs(delta)<=100)
            {var r=Reason(trust[i+1]);c.TrustAdjustments.Add(new(r.Label,delta,r.Key));}
        }
        c.TrustUpdated=page.Elements.Where(e=>e.Tag=="time"&&e.TokenIndex>begin&&e.TokenIndex<end).Select(e=>e.Attributes.GetValueOrDefault("datetime")).Where(t=>DateTimeOffset.TryParse(t,out _)).Select(t=>(DateTimeOffset?)DateTimeOffset.Parse(t!,CultureInfo.InvariantCulture)).FirstOrDefault();
        var detailStart=page.Tokens.FindIndex(t=>Key(t)=="detailedstats");var detailEnd=detailStart<0?-1:page.Tokens.FindIndex(detailStart+1,t=>Key(t)=="clutchperformance");
        var details=detailStart<0?[]:page.Tokens.Skip(detailStart+1).Take(detailEnd<0?page.Tokens.Count-detailStart-1:detailEnd-detailStart-1).ToList();
        var total=detailStart<0?null:page.Tokens.Take(detailStart).Reverse().Take(5).Select(t=>Regex.Match(t,@"^(\d+) matches$",RegexOptions.IgnoreCase)).FirstOrDefault(m=>m.Success);
        var detailScope=total!=null?$"CSTRACKER · усі {total.Groups[1].Value} відстежених матчів":"CSTRACKER · усі відстежені матчі";
        foreach(var e in page.Elements.Where(e=>e.Attributes.ContainsKey("data-histogram-config")))
        {
            try
            {
                using var json=JsonDocument.Parse(e.Attributes["data-histogram-config"]);var d=json.RootElement;
                if(!telemetry.TryGetValue(d.At("label").Str(),out var spec))continue;
                var block=page.Tokens.Skip(e.TokenIndex).Take(8).ToList();var value=After(block,d.At("label").Str()).Value;
                Add(c,spec.Key,spec.Label,value,spec.Unit,c.Scope,spec.Unit.Contains('%')?100:10000);
                if(d.At("suspiciousCutoff").Num() is {} cutoff&&cutoff>=0&&cutoff<=10000&&d.At("lowerIsSuspicious").ValueKind is JsonValueKind.True or JsonValueKind.False)
                    c.ReviewRules.Add(new(spec.Key,cutoff,d.At("lowerIsSuspicious").ValueKind==JsonValueKind.True,c.Scope));
            }
            catch(JsonException){}
        }
        foreach(var (label,spec) in detailed)
        {
            var found=After(details,label);Add(c,spec.Key,spec.Label,found.Value,spec.Unit,detailScope,spec.Unit.Contains('%')?100:10000000);
            var absolute=detailStart+1+found.Index;
            if(found.Index>=0&&page.Elements.Any(e=>e.TokenIndex==absolute&&Regex.IsMatch(e.Attributes.GetValueOrDefault("class")??"",@"\btext-(?:rose|red|amber)-(?:300|400)\b")))c.ReviewFlags.Add(spec.Key);
        }
        var kda=details.FindIndex(t=>Key(t)=="kda");
        if(kda>=0&&kda+1<details.Count)
        {
            var m=Regex.Match(details[kda+1],@"^([\d,]+)\s*/\s*([\d,]+)\s*/\s*([\d,]+)$");
            if(m.Success){Add(c,"kills","Вбивства · всі матчі",Num(m.Groups[1].Value),"",detailScope);Add(c,"deaths","Смерті · всі матчі",Num(m.Groups[2].Value),"",detailScope);Add(c,"assists","Асисти · всі матчі",Num(m.Groups[3].Value),"",detailScope);}
        }
        var hs=details.FindIndex(t=>Key(t)=="hskills");if(hs>=0&&hs+1<details.Count)
        {var m=Regex.Match(details[hs+1],@"\(([\d.]+)%\)");if(m.Success)Add(c,"detail_headshots","Хедшоти · всі матчі",Num(m.Groups[1].Value),"%",detailScope,100);}
        foreach(var (label,key,title) in new[]{("wallbangs","wallbangs","Крізь стіни"),("through smokes","smoke_kills","Крізь дим"),("in-air","air_kills","У повітрі"),("noscope","noscope","Без прицілу"),("headshots","headshots","Хедшоти")})
        {
            var i=page.Tokens.FindIndex(t=>t.StartsWith("//")&&Key(t)==Key(label));
            if(i>0)
            {
                var previous=page.Tokens.Take(i).TakeLast(4).ToList();double? value=null;
                for(var j=previous.Count-1;j>=0;j--)
                {
                    if(Regex.IsMatch(previous[j],@"^\d+(?:\.\d+)?%$")){value=Num(previous[j]);break;}
                    if(previous[j]=="%"&&j>0){value=Num(previous[j-1]);break;}
                }
                Add(c,key,title,value,"%",c.Scope,100);
            }
        }
        var level=page.Elements.FirstOrDefault(e=>e.Tag=="img"&&Regex.IsMatch(e.Attributes.GetValueOrDefault("alt")??"",@"^FACEIT level \d{1,2}$",RegexOptions.IgnoreCase));
        if(level!=null)
        {
            var number=Regex.Match(level.Attributes["alt"],@"\d+$").Value;Add(c,"faceit_level","FACEIT рівень",Num(number),"","CSTRACKER · кеш FACEIT",10);
            var elo=page.Tokens.Skip(level.TokenIndex+1).Take(2).Select(Num).FirstOrDefault(n=>n is >=1 and <=10000);Add(c,"faceit_elo","FACEIT Elo",elo,"","CSTRACKER · кеш FACEIT",10000);
        }
        c.Status=c.Metrics.Count>0?"Публічна статистика та оцінка сайту":"Статистика ще не відкрита або не зібрана";
        c.Note=c.Scope+" · траст — оцінка cstracker.gg";return c;
    }
    public static string? FaceitLink(string html)
    {
        return new PublicHtml(html).Links.Select(l=>Uri.TryCreate(l,UriKind.Absolute,out var u)?u:null)
            .Where(u=>u!=null&&u.Scheme=="https"&&u.Host is "faceit.com" or "www.faceit.com"&&Regex.IsMatch(u.AbsolutePath,@"^/(?:en|uk|ru)/players/[A-Za-z0-9_-]{1,50}/?$"))
            .Select(u=>"https://www.faceit.com/en/players/"+u!.Segments.Last().Trim('/')).Distinct().SingleOrDefaultSafe();
    }
    static string? SingleOrDefaultSafe(this IEnumerable<string> values){var items=values.Take(2).ToList();return items.Count==1?items[0]:null;}
    public static string? TrackerHistory(string id,string html)=>new PublicHtml(html).Elements.Any(e=>e.Attributes.GetValueOrDefault("hx-get")==$"/players/{id}/sections/history")?$"https://cstracker.gg/players/{id}/sections/history":null;
    public static SourceCard Faceit(string id,string url,string html)
    {
        var c=Providers.Empty(id).Single(c=>c.Name=="FACEIT");c.Url=url;var p=new PublicHtml(html);
        var nick=new Uri(url).Segments.Last().Trim('/');
        if(!p.Name.Split(' ').Any(n=>n.Equals(nick,StringComparison.OrdinalIgnoreCase))){c.Status="Публічний FACEIT-профіль не віддав статистику";return c;}
        var recent=p.Tokens.FindIndex(t=>t.Contains("Recent Performance",StringComparison.OrdinalIgnoreCase));
        var scope=recent>=0&&p.Tokens.Skip(recent).Take(5).Any(t=>Regex.IsMatch(t,@"^Last 30 (matches|games)$",RegexOptions.IgnoreCase))?"FACEIT · останні 30 матчів":"FACEIT · період на сторінці";
        var body=recent<0?[]:p.Tokens.Skip(recent).TakeWhile(t=>!t.Contains("Recent matches",StringComparison.OrdinalIgnoreCase)).ToList();
        foreach(var (label,key,title,unit,max) in new[]{("Win Rate","winrate","Перемоги","%",100d),("Average K/D Ratio","kd","Середній K/D","",20d),("Average Kills","kills_match","Вбивства / матч","",200d),("Average Headshots %","headshots","Хедшоти","%",100d),("Matches","matches","Матчі","",1000000d),("Wins","wins","Перемоги · матчі","",1000000d)})
            Add(c,key,title,After(body,label).Value,unit,scope,max);
        Add(c,"faceit_elo","FACEIT Elo",After(p.Tokens,"Elo").Value,"","FACEIT · публічний рейтинг",10000);
        Add(c,"faceit_level","FACEIT рівень",After(p.Tokens,"Skill Level").Value,"","FACEIT · публічний рейтинг",10);
        c.Scope=scope;var sample=c.Numbers.FirstOrDefault(n=>n.Key=="matches")?.Value;
        c.SampleCount=scope.Contains("30 матчів")&&sample is >=0 and <=30&&sample==(int)sample?(int)sample:null;c.RecentMatches=MatchTables(html,"FACEIT");
        AggregateRecent(c);c.Status=c.Metrics.Count>0||c.RecentMatches.Count>0?"Відкрита статистика FACEIT":"FACEIT віддав сторінку без статистики";c.Note=scope;return c;
    }
    public static List<RecentMatch> MatchTables(string html,string source)
    {
        var matches=new List<RecentMatch>();
        foreach(Match table in Regex.Matches(html,@"<table\b[^>]*>[\s\S]*?</table>",RegexOptions.IgnoreCase))
        {
            var rows=Regex.Matches(table.Value,@"<tr\b[^>]*>[\s\S]*?</tr>",RegexOptions.IgnoreCase).Cast<Match>().Select(r=>Regex.Matches(r.Value,@"<(th|td)\b[^>]*>([\s\S]*?)</\1>",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>string.Join(" ",new PublicHtml(m.Groups[2].Value).Tokens)).ToList()).Where(r=>r.Count>0).ToList();
            if(rows.Count<2)continue;var h=rows[0].Select(Key).ToList();int Index(params string[] labels)=>h.FindIndex(t=>labels.Select(Key).Contains(t));
            int date=Index("Date","Played"),map=Index("Map"),result=Index("Result","W/L"),score=Index("Score"),kda=Index("K/D/A"),src=Index("Source","Platform"),kills=Index("Kills"),deaths=Index("Deaths"),assists=Index("Assists");
            if(date<0||map<0)continue;
            foreach(var row in rows.Skip(1).Take(100))
            {
                string Cell(int i)=>i>=0&&i<row.Count?row[i]:"";
                if(!DateTimeOffset.TryParse(Cell(date),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var when)||when>DateTimeOffset.UtcNow.AddDays(1))continue;
                var platform=Cell(src);if(source!="FACEIT"&&!platform.Equals("FACEIT",StringComparison.OrdinalIgnoreCase))continue;
                var name=Cell(map).Trim();if(name.Length is <2 or >40||!Regex.IsMatch(name,@"^[A-Za-z0-9_ -]+$"))continue;
                int? Count(string value)=>Num(value) is {} n&&n>=0&&n<=500&&n==(int)n?(int)n:null;
                var k=Count(Cell(kills));var d=Count(Cell(deaths));var a=Count(Cell(assists));
                var triple=Regex.Match(Cell(kda),@"^(\d+)\s*/\s*(\d+)\s*/\s*(\d+)$");if(triple.Success){k=Count(triple.Groups[1].Value);d=Count(triple.Groups[2].Value);a=Count(triple.Groups[3].Value);}
                var r=Cell(result).ToUpperInvariant();var s=Cell(score);
                if(Regex.IsMatch(s,@"^[WL]\s+\d+\s*[:\-]\s*\d+$")){r=s[..1];s=s[1..].Trim();}
                if(r is not ("W" or "L" or "D" or "WIN" or "LOSS" or "DRAW"))r="";
                if(!Regex.IsMatch(s,@"^\d{1,2}\s*[:\-]\s*\d{1,2}$"))s="";
                matches.Add(new(when,name,r switch{"W" or "WIN"=>"Перемога","L" or "LOSS"=>"Поразка","D" or "DRAW"=>"Нічия",_=>""},s,k,d,a,Source:source=="FACEIT"?"FACEIT":source+" · FACEIT"));
            }
        }
        if(source=="CSTRACKER")matches.AddRange(TrackerMatches(html));
        return matches.DistinctBy(m=>(m.Date,m.Map,m.Score)).OrderByDescending(m=>m.Date).Take(10).ToList();
    }
    static List<RecentMatch> TrackerMatches(string html)
    {
        var result=new List<RecentMatch>();
        foreach(Match row in Regex.Matches(html,@"<tr\b[^>]*>[\s\S]*?</tr>",RegexOptions.IgnoreCase))
        {
            var cells=Regex.Matches(row.Value,@"<td\b[^>]*>([\s\S]*?)</td>",RegexOptions.IgnoreCase).Cast<Match>().Select(m=>new PublicHtml(m.Groups[1].Value)).ToList();
            if(cells.Count<11||!cells[0].Tokens.Any(t=>t.Equals("FACEIT",StringComparison.OrdinalIgnoreCase)))continue;
            var map=cells[0].Tokens.FirstOrDefault()??"";var score=cells[0].Tokens.Skip(1).FirstOrDefault(t=>Regex.IsMatch(t,@"^\d{1,2}[–:-]\d{1,2}$"))??"";
            if(map.Length is <2 or >40||score.Length==0)continue;
            var when=cells[^1].Elements.Select(e=>e.Attributes.GetValueOrDefault("data-time-ago")).FirstOrDefault(v=>long.TryParse(v,out var ts)&&ts>1000000000&&ts<4102444800);
            if(!long.TryParse(when,out var stamp))continue;var date=DateTimeOffset.FromUnixTimeSeconds(stamp);if(date>DateTimeOffset.UtcNow.AddDays(1))continue;
            var triple=Regex.Match(cells[2].Tokens.FirstOrDefault()??"",@"^(\d{1,3})\s*/\s*(\d{1,3})\s*/\s*(\d{1,3})$");
            int? k=triple.Success?int.Parse(triple.Groups[1].Value):null,d=triple.Success?int.Parse(triple.Groups[2].Value):null,a=triple.Success?int.Parse(triple.Groups[3].Value):null;
            var parts=Regex.Split(score,@"[–:-]");var r=int.Parse(parts[0])>int.Parse(parts[1])?"Перемога":int.Parse(parts[0])<int.Parse(parts[1])?"Поразка":"Нічия";
            var rating=Num(string.Join(" ",cells[5].Tokens));if(rating is <0 or >10)rating=null;
            result.Add(new(date,map,r,score,k,d,a,rating,"CSTRACKER · FACEIT"));
        }
        return result;
    }
    public static void AggregateRecent(SourceCard c)
    {
        var rows=c.RecentMatches;var scope=$"{rows.FirstOrDefault()?.Source??"FACEIT"} · {rows.Count} останніх відкритих матчів";
        if(rows.Count==0)return;
        Add(c,"recent_matches","Останні матчі",rows.Count,"",scope);
        var decided=rows.Where(m=>m.Result is "Перемога" or "Поразка").ToList();if(decided.Count>0)Add(c,"recent_winrate","Перемоги · останні",decided.Count(m=>m.Result=="Перемога")*100d/decided.Count,"%",scope+$" · {decided.Count} із результатом W/L",100);
        var combat=rows.Where(m=>m.Kills.HasValue&&m.Deaths.HasValue).ToList();if(combat.Count>0)
        {var combatScope=scope+$" · {combat.Count} із K/D";Add(c,"recent_kills","Вбивства / матч · останні",combat.Average(m=>m.Kills!.Value),"",combatScope,500);var ds=combat.Sum(m=>m.Deaths!.Value);if(ds>0)Add(c,"recent_kd","K/D · останні",combat.Sum(m=>m.Kills!.Value)/(double)ds,"",combatScope,500);}
    }
}
