using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MatchLens;
public sealed class Providers : IDisposable
{
    readonly HttpClient http;
    readonly SemaphoreSlim requests=new(8),parsing=new(2);
    readonly Dictionary<string,SemaphoreSlim> hostRequests=[];
    readonly Dictionary<string,(DateTimeOffset Time,List<SourceCard> Cards)> cache=[];
    readonly Dictionary<string,DateTimeOffset> cooldown=[];
    readonly BrowserPages[]? browsers;
    readonly Dictionary<string,BrowserPage> rendered=[];
    readonly PriorityGate browserSlots=new(2);
    readonly SemaphoreSlim backgroundBrowser=new(1);
    readonly Dictionary<string,DateTimeOffset> browserPreferred=[];
    readonly Dictionary<string,PublicCache> storedProfiles=[];
    readonly Queue<BrowserPages> available=[];
    readonly Func<string,Func<string,bool>,CancellationToken,Task<BrowserPage>>? renderPage;
    public Providers(HttpMessageHandler? handler=null,Func<string,Func<string,bool>,CancellationToken,Task<BrowserPage>>? renderPage=null)
    {
        http=handler==null?new(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.All}):new(handler);
        http.Timeout=TimeSpan.FromSeconds(12);http.DefaultRequestHeaders.UserAgent.ParseAdd("MatchLens/0.4");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        this.renderPage=renderPage;
        if(handler==null&&renderPage==null){browsers=[new(),new()];foreach(var browser in browsers)available.Enqueue(browser);}
    }
    public static List<SourceCard> Empty(string id)=>
    [
        new(){Name="LEETIFY",Accent="#D4B4F1",Url=$"https://leetify.com/public/profile/{id}",Status="Завантаження відкритої сторінки…",Note="Остання форма · відкриті показники"},
        new(){Name="CSSTATS",Accent="#D4B4F1",Url=$"https://csstats.gg/player/{id}",Status="Завантаження відкритої сторінки…",Note="Premier і доступна статистика профілю"},
        new(){Name="SCOPE.GG",Accent="#D4B4F1",Url=ScopeUrl(id),Status="Завантаження відкритої сторінки…",Note="Тільки показники, які сайт віддає публічно"},
        new(){Name="FACEIT",Accent="#D4B4F1",Url=$"https://www.faceit.com/en/search/player?query={id}",Status="Пошук відкритих показників…",Note="FACEIT-рейтинг з публічного профілю Leetify"},
        new(){Name="STEAM",Accent="#D4B4F1",Url=$"https://steamcommunity.com/profiles/{id}",Status="Завантаження Steam-профілю…",Note="Публічний профіль · без ключів"},
        new(){Name="CSTRACKER",Accent="#D4B4F1",Url=$"https://cstracker.gg/players/{id}",Status="Завантажуємо публічну статистику.",Note="Траст cstracker та статистика демо"}
    ];
    public void ClearCache(){cache.Clear();rendered.Clear();storedProfiles.Clear();browserPreferred.Clear();}
    static SourceCard Clone(SourceCard c)=>JsonSerializer.Deserialize<SourceCard>(JsonSerializer.Serialize(c,Store.Json),Store.Json)!;
    PublicCache Stored(string id)
    {
        if(!storedProfiles.TryGetValue(id,out var saved))storedProfiles[id]=saved=Store.Read<PublicCache>("public-profile-v043-"+id+".json");
        return saved;
    }
    public List<SourceCard> Cached(string id)
    {
        var saved=Stored(id);var now=DateTimeOffset.UtcNow;
        var cards=cache.TryGetValue(id,out var memory)?memory.Cards:saved.Id==id?saved.Cards:[];
        var result=Empty(id);
        for(var i=0;i<result.Count;i++)
        {
            var card=cards.FirstOrDefault(c=>c.Name==result[i].Name&&c.Metrics.Count>0&&c.FetchedAt is {} at&&now-at<TimeSpan.FromDays(1));
            if(card==null)continue;result[i]=Clone(card);result[i].Status=$"Збережена статистика • {card.FetchedAt!.Value.ToLocalTime():dd.MM HH:mm}";
        }
        return result;
    }
    async Task<T> Parse<T>(Func<T> action,CancellationToken ct)
    {await parsing.WaitAsync(ct);try{return await Task.Run(action,ct);}finally{parsing.Release();}}
    public static string ScopeUrl(string id)=>ulong.TryParse(id,out var n)&&n>=76561197960265728UL&&n-76561197960265728UL<=uint.MaxValue?
        "https://app.scope.gg/dashboard/"+(n-76561197960265728UL).ToString(System.Globalization.CultureInfo.InvariantCulture):"https://app.scope.gg/dashboard";
    internal static string Error(Exception ex)=>ex switch
    {
        HttpRequestException {StatusCode:HttpStatusCode.NotFound}=>"Профіль не знайдено на цьому сайті",
        HttpRequestException {StatusCode:HttpStatusCode.Unauthorized}=>"Сайт відповів HTTP 401; дані не отримано",
        HttpRequestException {StatusCode:HttpStatusCode.Forbidden}=>"Сайт відхилив запит (HTTP 403)",
        HttpRequestException {StatusCode:HttpStatusCode.TooManyRequests}=>"Ліміт запитів сайту. Повторна спроба трохи пізніше",
        Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException=>"Потрібен Microsoft Edge WebView2 Runtime — інсталятор у папці Dependencies",
        BrowserPageException=>"Не вдалося завантажити сторінку вбудованим браузером",
        OperationCanceledException=>"Час завантаження вичерпано. Можна повторити спробу",
        HttpRequestException=>"Помилка з’єднання із сайтом",
        _=>"Помилка завантаження статистики. Деталі — у підказці"
    };
    async Task<string> HttpPage(string url,CancellationToken ct)
    {
        var host=new Uri(url).Host;
        if(cooldown.TryGetValue(host,out var until)&&DateTimeOffset.UtcNow<until)throw new HttpRequestException("Cooldown",null,HttpStatusCode.TooManyRequests);
        if(!hostRequests.TryGetValue(host,out var hostGate))hostRequests[host]=hostGate=new SemaphoreSlim(2);
        await hostGate.WaitAsync(ct);
        try
        {
        await requests.WaitAsync(ct);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(12));var requestToken=deadline.Token;
            if(cooldown.TryGetValue(host,out until)&&DateTimeOffset.UtcNow<until)throw new HttpRequestException("Cooldown",null,HttpStatusCode.TooManyRequests);
            using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,requestToken);
            if(response.StatusCode==HttpStatusCode.TooManyRequests)
            {
                var delay=response.Headers.RetryAfter?.Delta??TimeSpan.FromMinutes(2);
                cooldown[host]=DateTimeOffset.UtcNow+TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds,60,3600));
            }
            response.EnsureSuccessStatusCode();
            var final=response.RequestMessage?.RequestUri;
            if(final!=null&&(final.Scheme!="https"||final.Host!=host))throw new HttpRequestException("Unexpected profile redirect");
            var requestedId=System.Text.RegularExpressions.Regex.Match(new Uri(url).AbsolutePath,@"/(?:player|players|profiles|public/profile)/(\d{17})(?:/|$)");
            var finalId=System.Text.RegularExpressions.Regex.Match(final?.AbsolutePath??"",@"/(?:player|players|profiles|public/profile)/(\d{17})(?:/|$)");
            if(requestedId.Success&&finalId.Success&&requestedId.Groups[1].Value!=finalId.Groups[1].Value)throw new HttpRequestException("Profile identity mismatch");
            const int limit=3*1024*1024;
            if(response.Content.Headers.ContentLength>limit)throw new IOException("Page limit");
            await using var input=await response.Content.ReadAsStreamAsync(requestToken);using var output=new MemoryStream();var buffer=new byte[16384];
            int count;while((count=await input.ReadAsync(buffer,requestToken))>0){if(output.Length+count>limit)throw new IOException("Page limit");output.Write(buffer,0,count);}
            var text=System.Text.Encoding.UTF8.GetString(output.ToArray());
            if(System.Text.RegularExpressions.Regex.IsMatch(text,@"<title[^>]*>\s*(just a moment|attention required)|cf-chl-platform|cf_chl_opt",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new HttpRequestException("Browser verification",null,HttpStatusCode.Forbidden);
            return text;
        }
        finally{requests.Release();}
        }finally{hostGate.Release();}
    }
    async Task<string> Page(string url,CancellationToken ct,Func<string,bool>? ready=null,int priority=0)
    {
        if(cooldown.TryGetValue(new Uri(url).Host,out var limited)&&limited>DateTimeOffset.UtcNow)throw new HttpRequestException("Cooldown",null,HttpStatusCode.TooManyRequests);
        string html="";
        try
        {
            var prefer=browserPreferred.TryGetValue(new Uri(url).Host,out var until)&&until>DateTimeOffset.UtcNow;
            if(!prefer||browsers==null&&renderPage==null)
            {
                html=await HttpPage(url,ct);
                if(browsers==null&&renderPage==null||ready==null||await Parse(()=>ready(html),ct))return html;
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(HttpRequestException ex)when(ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound){throw;}
        catch when(browsers!=null||renderPage!=null){ct.ThrowIfCancellationRequested();}
        if(browsers==null&&renderPage==null)return html;
        browserPreferred[new Uri(url).Host]=DateTimeOffset.UtcNow.AddMinutes(15);
        if(priority>1)await backgroundBrowser.WaitAsync(ct);
        try
        {
        await browserSlots.WaitAsync(priority,ct);var reader=browsers!=null?available.Dequeue():null;
        try
        {
            var browserUrl=url.Contains("steamcommunity.com")?url.Replace("/?xml=1",""):url;
            if(cooldown.TryGetValue(new Uri(url).Host,out var paused)&&paused>DateTimeOffset.UtcNow)throw new HttpRequestException("Cooldown",null,HttpStatusCode.TooManyRequests);
            var readiness=ready??(s=>new PublicHtml(s).Tokens.Count>100);
            var page=renderPage!=null?await renderPage(browserUrl,readiness,ct):await reader!.Read(browserUrl,readiness,ct);
            if(rendered.Count>=64)rendered.Clear();
            rendered[url]=page with{Html="<h1>"+WebUtility.HtmlEncode(new PublicHtml(page.Html).Name)+"</h1>",Text=page.Text.Length>50000?page.Text[..50000]:page.Text};
            if(page.Status is 429){cooldown[new Uri(url).Host]=DateTimeOffset.UtcNow.AddMinutes(2);throw new HttpRequestException("Too many requests",null,HttpStatusCode.TooManyRequests);}
            if(page.Status is 404)throw new HttpRequestException("Profile not found",null,HttpStatusCode.NotFound);
            return page.Html;
        }
        catch(HttpRequestException ex)when(ex.StatusCode==HttpStatusCode.TooManyRequests)
        {cooldown[new Uri(url).Host]=DateTimeOffset.UtcNow.AddMinutes(2);throw;}
        finally{if(reader!=null)available.Enqueue(reader);browserSlots.Release();}
        }finally{if(priority>1)backgroundBrowser.Release();}
    }
    internal static string EmptyPageStatus(BrowserPage page)
    {
        var text=page.Text;
        if(System.Text.RegularExpressions.Regex.IsMatch(page.Title+" "+text,@"just a moment|checking your browser|verify you are human|attention required",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return "Сайт ще перевіряє браузер. Натисни «Оновити», щоб повторити.";
        if(System.Text.RegularExpressions.Regex.IsMatch(page.Title+" "+new PublicHtml(page.Html).Name,@"private profile|profile (?:is )?private|profile.{0,20}hidden",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return "Власник приховав статистику профілю";
        if(System.Text.RegularExpressions.Regex.IsMatch(text,@"player not found|profile not found|no matches (?:found|played)|no data (?:available|found)",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return "На сайті ще немає статистики цього гравця";
        if(System.Text.RegularExpressions.Regex.IsMatch(text,@"please (?:log ?in|sign in) to (?:view|see) (?:player )?stats",System.Text.RegularExpressions.RegexOptions.IgnoreCase))return "Ці показники сайт показує лише після входу";
        return "Сторінку відкрито, але статистика ще не завантажилась. Спробуй оновити.";
    }
    public async Task<List<SourceCard>> Fetch(string id,Settings settings,CancellationToken ct=default,Action<List<SourceCard>>? partial=null)
    {
        if(!SteamIds.Valid(id))throw new ArgumentException("Некоректний Steam ID");
        if(cache.TryGetValue(id,out var old)&&DateTimeOffset.UtcNow-old.Time<CacheLifetime(old.Cards))return old.Cards;
        ct.ThrowIfCancellationRequested();
        var result=Cached(id);partial?.Invoke(result.ToList());
        var leetifyHtml="";var trackerHtml="";var stored=Stored(id);
        var usable=stored.Id==id&&DateTimeOffset.UtcNow-stored.SavedAt<TimeSpan.FromDays(1)?stored.Cards.Where(c=>c.FetchedAt is {} at&&DateTimeOffset.UtcNow-at<TimeSpan.FromDays(1)).ToList():[];
        async Task Read(int index,string url,Func<string,SourceCard> parse)
        {
            try
            {
                string? parsedHtml=null;SourceCard? parsed=null;
                bool Ready(string text){parsed=parse(text);parsedHtml=text;return parsed.Metrics.Count>=(index is 0 or 5?6:1);}
                var html=await Page(url,ct,Ready,index==2?2:index==4?1:0);ct.ThrowIfCancellationRequested();if(index==0)leetifyHtml=html;if(index==5)trackerHtml=html;
                result[index]=parsedHtml==html&&parsed!=null?parsed:await Parse(()=>parse(html),ct);result[index].FetchedAt=DateTimeOffset.Now;
                if(rendered.Remove(url,out var browserPage)&&result[index].Metrics.Count==0)result[index].Status=EmptyPageStatus(browserPage);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex)
            {
                var previous=usable.FirstOrDefault(c=>c.Name==result[index].Name&&c.Metrics.Count>0);
                if(previous!=null){result[index]=Clone(previous);result[index].Status="Збережені дані · "+Error(ex);}
                else result[index].Status=Error(ex);
                result[index].Diagnostic=Diagnostic(ex);
            }
            partial?.Invoke(result.ToList());
        }
        var tracker=Read(5,result[5].Url,html=>TrackingProfiles.CsTracker(id,html));
        var leetify=Read(0,result[0].Url,html=>PublicProfileParser.Leetify(id,html));
        var csstats=Read(1,result[1].Url,html=>PublicProfileParser.CsStats(id,html));
        var steam=Read(4,result[4].Url+"/?xml=1",html=>html.TrimStart().StartsWith("<profile")||html.TrimStart().StartsWith("<?xml")?PublicProfileParser.Steam(id,html):PublicProfileParser.SteamPage(id,html));
        var scope=Read(2,result[2].Url,html=>PublicProfileParser.Scope(id,html));
        await Task.WhenAll(leetify,csstats,steam,scope,tracker);
        ct.ThrowIfCancellationRequested();
        if(result[0].Metrics.Count>0&&leetifyHtml.Length>0&&(PublicProfileParser.LeetifySubpage(leetifyHtml,"maps")!=null||PublicProfileParser.LeetifySubpage(leetifyHtml,"matches")!=null))
        {
            async Task<string> Detail(string section)
            {
                var url=PublicProfileParser.LeetifySubpage(leetifyHtml,section);if(url==null)return "";
                try{return await Page(url,ct,s=>s.Contains("<table",StringComparison.OrdinalIgnoreCase)||section=="maps"&&new PublicHtml(s).Tokens.Any(t=>t=="Map Stats"),2);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{return "";}
            }
            var mapsTask=Detail("maps");var matchesTask=Detail("matches");await Task.WhenAll(mapsTask,matchesTask);
            var maps=await mapsTask;var matches=await matchesTask;
            result[0]=await Parse(()=>{var card=Clone(result[0]);PublicProfileParser.LeetifyDetails(card,id,maps,matches);return card;},ct);partial?.Invoke(result.ToList());
        }
        var faceit=result[3]=Clone(result[3]);
        var rankSource=result[0].Numbers.Any(n=>n.Key=="faceit_elo")?result[0]:result[5];
        faceit.Numbers=rankSource.Numbers.Where(m=>m.Key is "faceit_elo" or "faceit_level").Select(m=>m with{Scope=m.Scope.Length>0?m.Scope:rankSource.Name+" · рейтинг FACEIT"}).ToList();
        faceit.Metrics=faceit.Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();
        faceit.FetchedAt=rankSource.FetchedAt;faceit.Scope="FACEIT · рейтинг із "+rankSource.Name;
        faceit.Note=faceit.Scope;faceit.Status=faceit.Metrics.Count>0?"Публічний рейтинг із "+rankSource.Name:"Публічний FACEIT-профіль не знайдено";
        var faceitUrl=TrackingProfiles.FaceitLink(leetifyHtml)??TrackingProfiles.FaceitLink(trackerHtml);
        if(faceitUrl!=null)
        {
            try
            {
                var profileHtml=await Page(faceitUrl,ct,s=>TrackingProfiles.Faceit(id,faceitUrl,s) is {} c&&(c.Metrics.Count>0||c.RecentMatches.Count>0),2);var direct=await Parse(()=>TrackingProfiles.Faceit(id,faceitUrl,profileHtml),ct);
                if(direct.Metrics.Count>0||direct.RecentMatches.Count>0)
                {
                    foreach(var metric in faceit.Numbers.Where(n=>!direct.Numbers.Any(d=>d.Key==n.Key))){direct.Numbers.Add(metric);direct.Metrics.Add(new(metric.Label,metric.Display));}
                    direct.FetchedAt=DateTimeOffset.Now;result[3]=faceit=direct;
                }
                // Only the same public CS2 page, reached from a confirmed profile link.
                if(faceit.RecentMatches.Count==0)
                {
                    var historyHtml=await Page(faceitUrl+"/cs2/matchmaking",ct,s=>TrackingProfiles.Faceit(id,faceitUrl,s).RecentMatches.Count>0,2);var extra=await Parse(()=>TrackingProfiles.Faceit(id,faceitUrl,historyHtml),ct);
                    foreach(var metric in extra.Numbers.Where(n=>!faceit.Numbers.Any(d=>d.Key==n.Key))){faceit.Numbers.Add(metric);faceit.Metrics.Add(new(metric.Label,metric.Display));}
                    faceit.RecentMatches=extra.RecentMatches;TrackingProfiles.AggregateRecent(faceit);
                }
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){faceit.Status=(faceit.Metrics.Count>0?"Рейтинг доступний; останні матчі: ":"FACEIT: ")+Error(ex);faceit.Diagnostic=Diagnostic(ex);}
        }
        if(faceit.RecentMatches.Count==0&&result[0].RecentMatches.Count>0)
        {faceit.RecentMatches=result[0].RecentMatches;TrackingProfiles.AggregateRecent(faceit);faceit.Note+=" · останні FACEIT-матчі з LEETIFY";}
        if(faceit.RecentMatches.Count==0&&TrackingProfiles.TrackerHistory(id,trackerHtml) is {} historyUrl)
        {
            try
            {
                var historyHtml=await Page(historyUrl,ct,priority:2);faceit.RecentMatches=await Parse(()=>TrackingProfiles.MatchTables(historyHtml,"CSTRACKER"),ct);TrackingProfiles.AggregateRecent(faceit);
                if(faceit.RecentMatches.Count>0){faceit.Note+=" · останні FACEIT-матчі з CSTRACKER";faceit.Status="Публічна статистика та історія матчів";}
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch(Exception ex){faceit.Diagnostic=Diagnostic(ex);}
        }
        var previousFaceit=usable.FirstOrDefault(c=>c.Name=="FACEIT"&&c.RecentMatches.Count>0);
        if(faceit.RecentMatches.Count==0&&previousFaceit!=null)
        {
            faceit.RecentMatches=previousFaceit.RecentMatches;TrackingProfiles.AggregateRecent(faceit);
            faceit.Note+=$" · кеш історії {previousFaceit.FetchedAt?.ToLocalTime():dd.MM.yyyy HH:mm}";faceit.Status="Історія FACEIT з останнього доступного кешу";
        }
        partial?.Invoke(result.ToList());
        ct.ThrowIfCancellationRequested();
        cache[id]=(DateTimeOffset.UtcNow,result);
        if(result.Any(c=>c.Metrics.Count>0))try{var saved=new PublicCache{Id=id,SavedAt=DateTimeOffset.UtcNow,Cards=result.Where(c=>c.Metrics.Count>0).Select(Clone).ToList()};storedProfiles[id]=saved;await Task.Run(()=>Store.Write("public-profile-v043-"+id+".json",saved),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{}
        return result;
    }
    static string Diagnostic(Exception ex)
    {
        var parts=new List<string>();
        for(var error=ex;error!=null&&parts.Count<4;error=error.InnerException)
            parts.Add(error.GetType().Name+(error is HttpRequestException h&&h.StatusCode is {} code?" HTTP "+(int)code:"")+(error is BrowserPageException?": "+error.Message:""));
        return string.Join(" / ",parts);
    }
    internal static TimeSpan CacheLifetime(IEnumerable<SourceCard> cards)=>cards.Any(c=>c.Diagnostic.Length>0||c.Metrics.Count==0&&c.Name!="FACEIT")?TimeSpan.FromSeconds(30):TimeSpan.FromMinutes(15);
    // Pure legacy-data parser retained for migration; no authenticated request.
    public static void ParseLeetify(SourceCard c,JsonElement d)
    {
        if(d.At("privacy_mode").Str() is "private" or "hidden"){c.Status="Профіль прихований";return;}
        foreach(var (label,node) in new[]{("Рейтинг Leetify",d.At("ranks","leetify")),("Premier",d.At("ranks","premier")),("Прицілювання",d.At("rating","aim")),("Позиціонування",d.At("rating","positioning")),("Гранати",d.At("rating","utility")),("Матчі",d.At("total_matches"))})
            if(node.Num() is {} n)c.Metrics.Add(new(label,n.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)));
        if(d.At("winrate").Num() is {} wr&&wr>=0&&wr<=1)c.Metrics.Add(new("Перемоги",(wr*100).ToString("0.#",System.Globalization.CultureInfo.InvariantCulture)+"%"));
        c.Status=c.Metrics.Count>0?"Дані отримано":"Немає доступної статистики";
    }
    public void Dispose(){http.Dispose();if(browsers!=null)foreach(var reader in browsers)reader.Dispose();}
}
public class PublicCache
{
    public string Id {get;set;}="";
    public DateTimeOffset SavedAt {get;set;}
    public List<SourceCard> Cards {get;set;}=[];
}
