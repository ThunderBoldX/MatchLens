using System.Net;
using System.Net.Http;
using System.Text;

namespace MatchLens;
public static class BrowserFetchTests
{
    public static async Task Run(Action<bool,string> check)
    {
        const string id="76561198000000188";
        check(Providers.ScopeUrl("76561198005894538")=="https://app.scope.gg/dashboard/45628810"&&!BrowserPages.SameProfile("https://app.scope.gg/dashboard/45628810","https://app.scope.gg/progress/45628811"),"Scope public URLs use the Steam account ID and reject a different account on redirect");
        check(BrowserPages.Allowed("https://leetify.com/public/profile/"+id)&&!BrowserPages.Allowed("https://leetify.com.evil.test/")&&!BrowserPages.Allowed("file:///C:/secret")&&!BrowserPages.Allowed("https://name:password@leetify.com/"),"Browser navigation stays on public HTTPS tracker hosts without credentials");
        check(BrowserPages.SameProfile("https://leetify.com/public/profile/"+id,"https://leetify.com/app/profile/"+id+"/")&&!BrowserPages.SameProfile("https://leetify.com/public/profile/"+id,"https://leetify.com/app/profile/76561198000000189"),"Browser redirects preserve Steam identity across route and trailing-slash changes");
        check(!Providers.Error(new HttpRequestException("Denied",null,HttpStatusCode.Forbidden)).Contains("вхід")&&Providers.Error(new HttpRequestException("Denied",null,HttpStatusCode.Forbidden)).Contains("403"),"HTTP 403 does not fabricate a requirement to log in");
        var shell="<html><body><h1>Loading</h1><script>loadProfile()</script></body></html>";
        var html=PublicStatsTests.LeetifyFixture.Replace("<body>","<body><a href='https://steamcommunity.com/profiles/"+id+"'>Steam</a>");
        var pages=new List<string>();var pendingPredicate=false;var readyPredicate=false;
        Task<BrowserPage> Render(string url,Func<string,bool> ready,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();pages.Add(url);
            var leetify=url.Contains("leetify.com");
            if(leetify){pendingPredicate=!ready(shell);readyPredicate=ready(html);}
            return Task.FromResult(new BrowserPage(url,"Public profile","Public statistics",leetify?html:shell,200));
        }
        using(var handler=new ShellHandler())using(var providers=new Providers(handler,Render))
        {
            var cards=await providers.Fetch(id,new Settings{SteamKey="MUST_NOT_BE_SENT"});
            check(cards.Single(c=>c.Name=="LEETIFY").Numbers.Any(n=>n.Key=="kd"&&n.Value==1.31)&&pendingPredicate&&readyPredicate,"HTTP 200 JavaScript shell is replaced with rendered public statistics, after metric readiness");
            check(pages.Any(u=>u.Contains("leetify.com"))&&!handler.HasAuthorization,"Browser fallback does not send service API keys or HTTP authorization");
        }
        pages.Clear();using(var handler=new ShellHandler{Forbidden=true})using(var providers=new Providers(handler,Render))
        {
            var cards=await providers.Fetch("76561198000000187",new Settings());
            check(cards.Single(c=>c.Name=="LEETIFY").Numbers.Count>6&&pages.Any(u=>u.Contains("leetify.com")),"Ordinary browser loading is attempted after a plain HTTP 403");
        }
        pages.Clear();using(var handler=new ShellHandler{Limited=true})using(var providers=new Providers(handler,Render))
        {
            await providers.Fetch("76561198000000186",new Settings());
            check(!pages.Any(u=>u.Contains("leetify.com")),"Browser fallback respects HTTP 429 rather than bypassing the site's request limit");
        }
        var unrelated=new BrowserPage("https://leetify.com/@test","Player profile","Log In\nThis Steam profile is private\nLast 30 Matches",html,200);
        check(!Providers.EmptyPageStatus(unrelated).Contains("після входу")&&!Providers.EmptyPageStatus(unrelated).Contains("приховав"),"Login buttons and a private Steam account do not make a public Leetify profile private");
        check(Providers.EmptyPageStatus(unrelated with{Text="Please login to view player stats"}).Contains("після входу"),"Explicit statistics gate is distinguished from an ordinary login link");
        check(Providers.EmptyPageStatus(unrelated with{Title="Just a moment...",Text="Checking your browser"}).Contains("перевіряє браузер"),"Browser verification has a separate actionable status");
        check(Providers.CacheLifetime([new SourceCard{Name="LEETIFY"}])==TimeSpan.FromSeconds(30),"Failed or empty page fetches cannot suppress retries for fifteen minutes");
        var steam=PublicProfileParser.SteamPage(id,$"<div class='playerAvatar' data-miniprofile='{ulong.Parse(id)-76561197960265728UL}'><img src='https://avatars.steamstatic.com/test.jpg'></div><span class=\"actual_persona_name\">Test player</span><span class='friendPlayerLevelNum'>28</span>");
        check(steam.Metrics.Count==2&&steam.AvatarUrl.EndsWith("test.jpg")&&steam.Metrics.All(m=>m.Label!="VAC"),"Rendered Steam profile reads avatar and account level without inventing absent ban information");
        check(PublicProfileParser.SteamPage("76561198000000189","<span class=\"actual_persona_name\">Test player</span>").Metrics.Count==0,"Rendered Steam profile requires a matching public identity");
    }
    sealed class ShellHandler : HttpMessageHandler
    {
        public bool Forbidden,Limited,HasAuthorization;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            HasAuthorization|=request.Headers.Authorization!=null||request.RequestUri!.ToString().Contains("MUST_NOT_BE_SENT");
            var code=request.RequestUri!.Host=="leetify.com"?(Limited?HttpStatusCode.TooManyRequests:Forbidden?HttpStatusCode.Forbidden:HttpStatusCode.OK):HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code){RequestMessage=request,Content=new StringContent("<html><body><h1>Loading</h1><script>loadProfile()</script></body></html>",Encoding.UTF8,"text/html")});
        }
    }
}
