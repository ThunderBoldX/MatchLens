using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MatchLens;
public partial class App : Application
{
    Mutex? instance;
    EventWaitHandle? activation;
    RegisteredWaitHandle? activationWait;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains("--passive-steam-worker"))
        {ShutdownMode=ShutdownMode.OnExplicitShutdown;await SteamDiscovery.RunWorker();Shutdown();return;}
        var language=Array.IndexOf(e.Args,"--language");if(language>=0&&language+1<e.Args.Length)L.OverrideLanguage=L.Normalize(e.Args[language+1]);
        var root=Array.IndexOf(e.Args,"--data-dir");if(root>=0&&root+1<e.Args.Length)Store.Root=Path.GetFullPath(e.Args[root+1]);
        if(e.Args.Contains("--ui-probe"))
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try{UiProbe.Run(Store.Root);Shutdown(0);}catch(Exception ex){Directory.CreateDirectory(Store.Root);File.WriteAllText(Path.Combine(Store.Root,"ui-probe-error.txt"),ex.ToString());Shutdown(1);}return;
        }
        var browserProbe=Array.IndexOf(e.Args,"--browser-probe");
        if(browserProbe>=0&&browserProbe+1<e.Args.Length)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Directory.CreateDirectory(Store.Root);
            try
            {
                var url=e.Args[browserProbe+1];var id=System.Text.RegularExpressions.Regex.Match(url,@"\d{17}").Value;
                using var reader=new BrowserPages();
                var page=await reader.Read(url,html=>url.Contains("leetify.com")?PublicProfileParser.Leetify(id,html).Metrics.Count>=6:url.Contains("cstracker.gg")?TrackingProfiles.CsTracker(id,html).Metrics.Count>=6:new PublicHtml(html).Tokens.Count>100);
                File.WriteAllText(Path.Combine(Store.Root,"browser-page.html"),page.Html);
                File.WriteAllText(Path.Combine(Store.Root,"browser-page.json"),System.Text.Json.JsonSerializer.Serialize(page,Store.Json));
                Shutdown(0);
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(Store.Root,"browser-error.txt"),ex.ToString());Shutdown(1);}
            return;
        }
        var trackerFixture=Array.IndexOf(e.Args,"--tracker-fixture");
        if(trackerFixture>=0&&trackerFixture+1<e.Args.Length)
        {
            Directory.CreateDirectory(Store.Root);var identity=Array.IndexOf(e.Args,"--player");
            var id=identity>=0&&identity+1<e.Args.Length?e.Args[identity+1]:"76561198063546077";
            var card=TrackingProfiles.CsTracker(id,File.ReadAllText(e.Args[trackerFixture+1]));
            File.WriteAllText(Path.Combine(Store.Root,"tracker-fixture.json"),System.Text.Json.JsonSerializer.Serialize(card,Store.Json));Shutdown(card.Metrics.Count>0?0:2);return;
        }
        var publicProbe=Array.IndexOf(e.Args,"--public-probe");
        if(publicProbe>=0&&publicProbe+1<e.Args.Length)
        {
            Directory.CreateDirectory(Store.Root);
            try
            {
                using var providers=new Providers();
                var cards=await providers.Fetch(e.Args[publicProbe+1],new Settings());
                File.WriteAllText(Path.Combine(Store.Root,"public-probe.json"),System.Text.Json.JsonSerializer.Serialize(cards,Store.Json));
                Shutdown(cards.Any(c=>c.Metrics.Count>0)?0:2);
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(Store.Root,"public-probe-error.txt"),ex.ToString());Shutdown(1);}
            return;
        }
        if(e.Args.Contains("--discovery-probe"))
        {
            Directory.CreateDirectory(Store.Root);
            using var discovery=new SteamDiscovery();var selector=new RecentDiscovery();
            var elapsed=System.Diagnostics.Stopwatch.StartNew();var snapshot=await discovery.Poll(Installation.FindCs2());elapsed.Stop();
            var state=new MatchState{Active=true,Generation=1,StartedAt=DateTimeOffset.UtcNow};
            var selected=snapshot?.Available==true?selector.Observe(state,snapshot.Players,DateTimeOffset.UtcNow,snapshot.ServerPlayers):[];
            File.WriteAllText(Path.Combine(Store.Root,"discovery-probe.txt"),
                $"Available: {snapshot?.Available==true}\nAdapter: {discovery.Status}\nSteam records: {snapshot?.Players.Count??0}\nServer candidates: {snapshot?.ServerPlayers?.Count??0}\nSelected profiles: {selected.Count}\nPoll milliseconds: {elapsed.ElapsedMilliseconds}\n{selector.Status}\nLive match membership: NOT verified against a running game\n");
            Shutdown(snapshot?.Available==true?0:2);return;
        }
        if(e.Args.Contains("--self-test"))
        {ShutdownMode=ShutdownMode.OnExplicitShutdown;try{await SelfTests.Run();Shutdown(0);}catch(Exception ex){Directory.CreateDirectory(Store.Root);File.WriteAllText(Path.Combine(Store.Root,"test-failure.txt"),ex.ToString());Shutdown(1);}return;}
        if(e.Args.Contains("--steam-probe"))
        {
            using var reader=new SteamReader();var path=Installation.FindCs2();var success=reader.Connect(path);
            var coplay=reader.Read();var server=reader.ReadServer();Directory.CreateDirectory(Store.Root);
            File.WriteAllText(Path.Combine(Store.Root,"steam-probe.txt"),$"CS2 found: {path!=""}\nSteam initialized: {success}\nStatus: {reader.Status}\nCoplay entries: {coplay.Count}\nServer entries: {server.Count}\n");Shutdown(success?0:2);return;
        }
        var sourceProbe=Array.IndexOf(e.Args,"--source-probe");
        if(sourceProbe>=0&&sourceProbe+1<e.Args.Length)
        {
            Directory.CreateDirectory(Store.Root);
            var path=Installation.FindCs2();var lines=new System.Collections.Generic.List<string>();
            if(!GamePresence.Running()){lines.Add("CS2 is not running. Steam connection was not attempted.");}
            else if(ulong.TryParse(e.Args[sourceProbe+1],out var source))
            {
                using var reader=new SteamReader();var ok=reader.Connect(path);lines.Add("CS2 found: "+(path!=""));lines.Add("Steam initialized: "+ok);
                if(ok){for(var i=0;i<4;i++){await Task.Delay(500);var list=reader.ReadSource(source);lines.Add($"Source poll {i+1}: {list.Count} players");}}
                lines.Add(reader.Status);
            }
            File.WriteAllLines(Path.Combine(Store.Root,"source-probe.txt"),lines);Shutdown();return;
        }
        if(e.Args.Contains("--gsi-probe"))
        {
            Directory.CreateDirectory(Store.Root);
            try
            {
                var count=0;await using var server=new GsiServer();await server.Start("probe",_=>count++);
                using var client=new System.Net.Http.HttpClient{Timeout=TimeSpan.FromSeconds(5)};
                using var bad=await client.PostAsync("http://127.0.0.1:37931/gsi",new System.Net.Http.StringContent("{\"auth\":{\"token\":\"wrong\"}}"));
                using var good=await client.PostAsync("http://127.0.0.1:37931/gsi",new System.Net.Http.StringContent("{\"auth\":{\"token\":\"probe\"},\"provider\":{\"appid\":730}}"));
                if(bad.StatusCode!=System.Net.HttpStatusCode.Forbidden||!good.IsSuccessStatusCode||count!=1)throw new Exception("GSI authentication/dispatch failed");
                File.WriteAllText(Path.Combine(Store.Root,"gsi-probe.txt"),"PASS: loopback HTTP listener, rejection of invalid token, authenticated payload dispatch.\n");Shutdown(0);
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(Store.Root,"gsi-probe.txt"),"BLOCKED/FAIL: "+ex.GetType().Name+": "+ex.Message);Shutdown(2);}
            return;
        }
        var telegramPreview=Array.IndexOf(e.Args,"--telegram-preview");
        if(telegramPreview>=0&&telegramPreview+1<e.Args.Length)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var window=new MainWindow(true);window.SetDemo();
            var text=window.TelegramPreview();var path=Path.GetFullPath(e.Args[telegramPreview+1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);
            File.WriteAllText(Path.ChangeExtension(path,".html"),"<!doctype html><meta charset='utf-8'><title>MatchLens Telegram · демонстрація</title><style>body{background:#0e0e11;color:#eee;font:15px/1.7 system-ui;padding:32px}article{max-width:410px;background:#202027;border:1px solid #363640;border-radius:24px;padding:24px;margin:auto;white-space:pre-wrap}pre{font:13px/1.7 Consolas,monospace;margin:5px 0;color:#e8e5f0}a{color:#c9a6ea;text-decoration:none}i{color:#9898a7;font-size:12px}</style><article>"+text+"</article>");
            TelegramPreviewRenderer.Save(text,Path.ChangeExtension(path,".png"));
            Shutdown(0);return;
        }
        var render=Array.IndexOf(e.Args,"--render-preview");
        if(render>=0&&render+1<e.Args.Length)
        {
            var width=1440;var height=1030;var sizeArg=Array.IndexOf(e.Args,"--preview-size");
            if(sizeArg>=0&&sizeArg+1<e.Args.Length){var parts=e.Args[sizeArg+1].Split('x');if(parts.Length==2&&int.TryParse(parts[0],out var rw)&&int.TryParse(parts[1],out var rh)&&rw>=860&&rw<=2000&&rh>=560&&rh<=2200){width=rw;height=rh;}}
            var w=new MainWindow(true);if(!e.Args.Contains("--idle"))w.SetDemo();w.Width=width;w.Height=height;
            if(e.Args.Contains("--profile"))w.OpenPreviewProfile(e.Args.Contains("--profile-low"));
            var content=(FrameworkElement)w.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
            if(e.Args.Contains("--second-monitor")){var display=w.PresentationPreview();display.Width=width;display.Height=height;content=(FrameworkElement)display.Content;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();if(e.Args.Contains("--scrolled")){display.StatisticsScroll.ScrollToVerticalOffset(600);content.UpdateLayout();}}
            if(e.Args.Contains("--details")){w.PreviewDetails();content.UpdateLayout();}
            if(e.Args.Contains("--dropdown")){content=w.PopupPreview();width=(int)Math.Ceiling(content.ActualWidth);height=(int)Math.Ceiling(content.ActualHeight);}
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(e.Args[render+1]))png.Save(file);Shutdown();return;
        }
        var instanceKey=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Store.Root.ToUpperInvariant())))[..20];
        instance=new Mutex(true,"Local\\MatchLens-"+instanceKey,out var first);
        activation=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\MatchLens-activate-"+instanceKey);
        if(!first){activation.Set();Shutdown();return;}
        MainWindow=new MainWindow();MainWindow.Show();
        activationWait=ThreadPool.RegisterWaitForSingleObject(activation,(_,_)=>Dispatcher.BeginInvoke(()=>{if(MainWindow is MatchLens.MainWindow w)w.RestoreWindow();}),null,Timeout.Infinite,false);
    }
    protected override void OnExit(ExitEventArgs e){activationWait?.Unregister(null);activation?.Dispose();instance?.Dispose();base.OnExit(e);}
}
