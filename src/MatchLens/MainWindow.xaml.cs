using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MatchLens;
public partial class MainWindow : Window
{
    readonly ObservableCollection<Player> players=[];
    readonly MatchTracker tracker=new();
    readonly Providers providers=new();
    readonly Avatars avatars=new();
    readonly Telegram telegram=new();
    readonly GsiServer gsi=new();
    readonly SteamDiscovery steamDiscovery=new();
    readonly RecentDiscovery recentDiscovery=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    readonly DiscoverySchedule discoverySchedule=new();
    DateTimeOffset nextSteamPoll;int pollingGeneration=-1;bool pendingDispatch;
    DateTimeOffset? firstRosterAt;DateTimeOffset healthWritten;
    readonly HashSet<string> fetching=[],avatarRequests=[];
    readonly Dictionary<string,CancellationTokenSource> playerLoads=[];
    readonly DispatcherTimer viewTimer=new(){Interval=TimeSpan.FromMilliseconds(150)};
    Task? telegramTask;
    Player? drawnPlayer;List<SourceCard>? drawnSources,drawnSelfSources;LiveMatchStats? drawnLive;System.Windows.Media.ImageSource? drawnAvatar;string drawnName="";
    readonly List<Player> pending=[];
    readonly bool preview;
    Settings settings;
    TrayHost? tray;
    PresentationWindow? presentation;
    Player? selected;
    CancellationTokenSource fetchStop=new();
    bool demo,closing,closed,busy,selectionChanging;
    int seenGeneration=-1,fetchEpoch,activeLoads;
    string gsiStatus=L.T("GSI очікує підключення");
    DateTimeOffset? lastSteamPoll;
    MatchState? demoState;
    WindowState restoreState=WindowState.Normal;
    public MainWindow(bool preview=false)
    {
        this.preview=preview;InitializeComponent();Style=(Style)Application.Current.FindResource(typeof(Window));
        settings=Store.Read<Settings>("settings.json");L.Set(L.OverrideLanguage??settings.Language);PlayerSelector.ItemsSource=players;
        players.CollectionChanged+=(_,_)=>{if(players.Count==0)PlayerSelector.IsDropDownOpen=false;};
        PlayerSelector.DropDownOpened+=(_,_)=>{if(!PlayerSelector.HasItems)PlayerSelector.IsDropDownOpen=false;};
        Icon=BrandMarks.MatchLens;
        if(!preview)WindowAppearance.FitInitial(this);
        SourceInitialized+=(_,_)=>WindowAppearance.Apply(this);
        if(settings.Cs2Path=="")settings.Cs2Path=Installation.FindCs2();
        Loaded+=async(_,_)=>{if(!preview)await Start();};Closing+=OnClosing;
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized&&!preview)HideToTray();else restoreState=WindowState;UpdateWindowShape();};
        viewTimer.Tick+=(_,_)=>{viewTimer.Stop();if(!closing){UpdateView();ScheduleTelegram();}};
        timer.Tick+=async(_,_)=>await Tick();UpdateView();
    }
    async Task Start()
    {
        tray=new TrayHost(()=>Dispatcher.Invoke(RestoreWindow),()=>Dispatcher.Invoke(Close),()=>Dispatcher.Invoke(TogglePresentation));
        try
        {
            await gsi.Start(settings.GsiToken,d=>Dispatcher.BeginInvoke(()=>{if(!closing){var generation=tracker.State.Generation;tracker.Apply(d,DateTimeOffset.UtcNow);if(!demo){Sync();if(generation!=tracker.State.Generation){nextSteamPoll=default;_=Tick();}}}}));
            gsiStatus=L.T("GSI готове · 127.0.0.1:37931");
        }
        catch{gsiStatus=L.T("Не вдалося відкрити порт 37931. Перевірте, чи немає іншої копії MatchLens.");}
        timer.Start();UpdateView();await Tick();
    }
    async Task Tick()
    {
        if(busy||closing)return;
        var now=DateTimeOffset.UtcNow;tracker.Tick(now);
        var generation=tracker.State.Generation;
        discoverySchedule.Session(generation,now);
        if(pollingGeneration!=generation){pollingGeneration=generation;nextSteamPoll=default;firstRosterAt=null;}
        if(now<nextSteamPoll)return;
        busy=true;
        try
        {
            if(!demo)
            {
                var snapshot=await steamDiscovery.Poll(settings.Cs2Path,generation);
                if(snapshot?.Available==true)lastSteamPoll=DateTimeOffset.UtcNow;if(closing)return;
                if(snapshot?.Available==true&&generation==tracker.State.Generation&&!demo)
                    tracker.ReplaceAutomatic(recentDiscovery.Observe(tracker.State,snapshot.Players,DateTimeOffset.UtcNow,snapshot.ServerPlayers));
                else if(generation==tracker.State.Generation&&snapshot?.Available==false&&(!lastSteamPoll.HasValue||DateTimeOffset.UtcNow-lastSteamPoll.Value>TimeSpan.FromSeconds(15))){recentDiscovery.Reset();tracker.ReplaceAutomatic([]);}
                Sync();
                if(tracker.State.Active&&players.Any(p=>!p.IsLocal))firstRosterAt??=DateTimeOffset.UtcNow;
                if(DateTimeOffset.UtcNow-healthWritten>TimeSpan.FromSeconds(5))
                {
                    healthWritten=DateTimeOffset.UtcNow;
                    var health=new {Version="0.4.9",CapturedAt=healthWritten,tracker.State.Active,Generation=tracker.State.Generation,
                        Adapter=steamDiscovery.Status,Selection=recentDiscovery.Status,History=recentDiscovery.RawCount,Server=recentDiscovery.ServerCount,
                        Profiles=players.Count,Withheld=recentDiscovery.WithheldCount,FirstProfilesSeconds=firstRosterAt is {} first?(double?)(first-tracker.State.StartedAt).TotalSeconds:null};
                    _=Task.Run(()=>{try{Store.Write("discovery-health-v049.json",health);}catch{}});
                }
            }
            ScheduleTelegram();RequestView();
        }
        catch{FooterStatus.Text=L.T("Не вдалося оновити список гравців.");}
        finally
        {
            busy=false;
            nextSteamPoll=generation==tracker.State.Generation?DateTimeOffset.UtcNow+discoverySchedule.Interval(tracker.State.Active,DateTimeOffset.UtcNow):default;
            if(!closing&&generation!=tracker.State.Generation)_=Tick();
        }
    }
    void RequestView(){if(!closing&&!viewTimer.IsEnabled)viewTimer.Start();}
    void ScheduleTelegram()
    {
        if(preview||closing||telegramTask is {IsCompleted:false})return;telegramTask=SendTelegram();
    }
    async Task SendTelegram()
    {
        int generation;do{var state=CurrentState();generation=state.Generation;await telegram.Update(settings,state,players.ToList());}
        while(!closing&&generation!=CurrentState().Generation);
    }
    public string TelegramPreview()=>Telegram.Format(CurrentState(),players.ToList()).Text;
    MatchState CurrentState()=>demo?demoState!:tracker.State.Copy();
    void CancelFetches(){fetchEpoch++;pending.Clear();fetchStop.Cancel();fetchStop.Dispose();fetchStop=new();fetching.Clear();avatarRequests.Clear();}
    void Sync()
    {
        if(demo)return;var state=tracker.State;
        recentDiscovery.Session(state);
        var generationChanged=seenGeneration!=state.Generation;
        if(generationChanged){CancelFetches();players.Clear();Select(null);seenGeneration=state.Generation;}
        var membershipChanged=generationChanged||players.Count!=state.Roster.Count||players.Any(p=>!state.Roster.Any(e=>e.Id==p.Id));
        var ids=state.Roster.Select(p=>p.Id).ToHashSet();foreach(var p in players.Where(p=>!ids.Contains(p.Id)).ToArray()){if(playerLoads.TryGetValue(p.Id,out var load))load.Cancel();pending.Remove(p);fetching.Remove(p.Id);players.Remove(p);}
        foreach(var entry in state.Roster.OrderByDescending(p=>p.Id==state.LocalId))
        {
            var p=players.FirstOrDefault(p=>p.Id==entry.Id);
            if(p==null){p=new(){Id=entry.Id,Name=entry.Name,Team=entry.Team,Confirmed=entry.Confirmed,Evidence=entry.Evidence,IsLocal=entry.Id==state.LocalId,Sources=Providers.Empty(entry.Id)};players.Add(p);}
            p.Name=entry.Name;p.Team=entry.Team;p.Confirmed=entry.Confirmed;p.Evidence=entry.Evidence;p.IsLocal=p.Id==state.LocalId;
            p.Live=state.LivePlayers.GetValueOrDefault(p.Id);p.Refresh();QueueStats(p);
        }
        if(selected!=null&&!players.Contains(selected))Select(null);if(membershipChanged)UpdateView();else RequestView();
    }
    void QueueStats(Player p,bool priority=false)
    {
        if(demo||closing||preview)return;if(fetching.Add(p.Id))pending.Add(p);
        if(priority&&pending.Remove(p))pending.Insert(0,p);
        if(pendingDispatch)return;pendingDispatch=true;Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{pendingDispatch=false;StartPending();}));
    }
    void StartPending()
    {
        while(activeLoads<64&&!demo&&!closing&&pending.Count>0)
        {
            var p=pending[0];pending.RemoveAt(0);if(!players.Contains(p))continue;activeLoads++;_=LoadStats(p);
        }
    }
    async Task LoadStats(Player p)
    {
        var epoch=fetchEpoch;using var job=CancellationTokenSource.CreateLinkedTokenSource(fetchStop.Token);playerLoads[p.Id]=job;var token=job.Token;
        void Apply(List<SourceCard> cards)
        {
            if(epoch!=fetchEpoch||demo||closing||!players.Contains(p))return;
            p.Sources=cards;p.Refresh();if(p.Avatar==null&&cards.Any(c=>Avatars.TrustedUrl(c.AvatarUrl)))_=LoadAvatar(p);RequestView();
        }
        try{var result=await providers.Fetch(p.Id,settings,token,Apply);Apply(result);if(!token.IsCancellationRequested&&epoch==fetchEpoch&&players.Contains(p)&&p.Avatar==null)_=LoadAvatar(p);}
        catch(OperationCanceledException){}
        catch{if(epoch==fetchEpoch)FooterStatus.Text=L.T("Не вдалося завантажити статистику профілю.");}
        finally{if(playerLoads.GetValueOrDefault(p.Id)==job)playerLoads.Remove(p.Id);activeLoads--;StartPending();}
    }
    async Task LoadAvatar(Player p,string? known=null)
    {
        if(p.Avatar!=null||!avatarRequests.Add(p.Id))return;
        var epoch=fetchEpoch;var token=fetchStop.Token;known??=p.Sources.Select(c=>c.AvatarUrl).FirstOrDefault(Avatars.TrustedUrl);
        try{var image=await avatars.Get(p.Id,known,token);if(epoch==fetchEpoch&&!demo&&!closing&&players.Contains(p)){p.Avatar=image;p.Refresh();RequestView();}}
        catch(OperationCanceledException){}
        finally
        {
            if(epoch==fetchEpoch)
            {
                avatarRequests.Remove(p.Id);
                if(p.Avatar==null&&string.IsNullOrEmpty(known)&&!demo&&!closing&&players.Contains(p)&&p.Sources.Select(c=>c.AvatarUrl).FirstOrDefault(Avatars.TrustedUrl) is {} url)_=LoadAvatar(p,url);
            }
        }
    }
    static NumericMetric? Find(Player? p,string key)=>p?.Sources.OrderBy(c=>c.Name=="LEETIFY"?0:c.Name=="CSSTATS"?1:2).SelectMany(c=>c.Numbers).FirstOrDefault(n=>n.Key==key);
    static SourceCard? Origin(Player p,string key)=>p.Sources.OrderBy(c=>c.Name=="LEETIFY"?0:c.Name=="CSSTATS"?1:2).FirstOrDefault(c=>c.Numbers.Any(n=>n.Key==key));
    List<StatTile> Tiles(Player? p,params (string Key,string Label)[] keys)=>keys.Select(k=>{var n=Find(p,k.Key);var c=p==null?null:Origin(p,k.Key);return MetricAppearance.Tile(k.Key,k.Label,n?.Display??"-",p==null?L.T("Очікуємо ваш профіль"):c?.Name??L.T("Немає відкритих даних"),n,p,players.FirstOrDefault(x=>x.IsLocal),c?.Name);}).ToList();
    void UpdateView()
    {
        if(MatchTitle==null||settings==null)return;var s=CurrentState();var self=players.FirstOrDefault(p=>p.IsLocal);
        bool Match(Player p)=>string.IsNullOrWhiteSpace(SearchBox.Text)||p.Name.Contains(SearchBox.Text,StringComparison.OrdinalIgnoreCase)||p.Id.Contains(SearchBox.Text);
        var visible=players.Where(Match).OrderByDescending(p=>p.IsLocal).ToList();if(RosterList.ItemsSource is not List<Player> previous||!previous.SequenceEqual(visible))RosterList.ItemsSource=visible;RosterEmpty.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
        MatchTitle.Text=s.Active?L.T("Твій матч, у фокусі."):L.T("Більше, ніж просто нік.");
        MatchSubtitle.Text=s.Active?L.F($"{s.Map.ToUpperInvariant()}  /  {s.ModeLabel}  /  {players.Count} знайдених профілів"):L.T("Запусти CS2 та зайди на сервер. MatchLens автоматично збере відкриті дані.");
        LiveBadge.Text=demo?L.T("ДЕМОНСТРАЦІЯ"):s.Active?L.T("У ГРІ"):L.T("ОЧІКУВАННЯ");
        SelfName.Text=self?.Name??L.T("Ваш профіль з’явиться тут");
        SelfScope.Text=L.T(self?.Sources.FirstOrDefault(c=>c.Name=="LEETIFY")?.Scope is {Length:>0} scope?scope:L.T("Статистика завантажується з публічних профілів"));
        SelfTiles.ItemsSource=Tiles(self,("trust",L.T("Траст cstracker")),("kd","K/D"),("leetify_rating",L.T("Рейтинг Leetify")),("aim",L.T("Прицілювання")),("winrate",L.T("Перемоги")),("ttd",L.T("Час до шкоди")));
        var compared=players.Where(p=>!p.IsLocal).Select(p=>new{Player=p,Result=PlayerComparison.Read(p,self)}).Where(x=>x.Result.Available).OrderByDescending(x=>x.Result.Index).ToList();
        var harder=compared.Where(x=>x.Result.Index>8).Take(3).ToList();
        ThreatList.ItemsSource=harder.Select(x=>new{Player=x.Player,Name=x.Player.Name,Label=x.Result.Label,Detail=x.Result.Detail,Index=x.Result.Index.ToString("+0.0;-0.0;0.0",System.Globalization.CultureInfo.InvariantCulture)}).ToList();
        ComparisonEmpty.Visibility=harder.Count==0?Visibility.Visible:Visibility.Collapsed;
        ComparisonEmpty.Text=compared.Count==0?L.T("Чекаємо достатньо спільної статистики гравців і вашого профілю."):L.T("У порівнюваних профілях суттєво вищих показників не знайдено.");
        ComparisonSummary.Text=L.F($"{compared.Count} з {Math.Max(0,players.Count-1)} профілів доступні для порівняння");
        RosterStatus.Text=demo?L.T("ДЕМО · вигадані показники для перегляду дизайну"):L.F($"{players.Count} профілів · {players.Count(p=>p.Confirmed)} з GSI · {L.T(recentDiscovery.Status)}");
        NoticeTitle.Text=demo?L.T("Демонстрація інтерфейсу"):s.Active?L.T("Автоматичне завантаження"):L.T("Одне підключення до CS2");
        NoticeText.Text=demo?L.T("Ці гравці й показники вигадані. Telegram їх не отримує."):s.Active?(players.Any(p=>!p.IsLocal)?L.T("Список гравців з’являється одразу; статистика заповнюється окремо. Профілі Steam позначені джерелом — підтвердження GSI показано окремо."):L.T("Матч знайдено. Очікуємо Steam ID інших гравців від Steam; завантаження статистики не затримує список.")):L.T("Налаштування → Встановити GSI → перезапустити CS2. Розширення й API-ключі не потрібні.");
        if(!demo&&s.Active&&recentDiscovery.WithheldCount>0)NoticeText.Text+=L.F($"\n{recentDiscovery.WithheldCount} профілів з попередніх каток приховано. Повторний учасник з’явиться після підтвердження поточною грою.");
        if(!demo&&Store.Warning!="")NoticeText.Text+="\n"+L.T(Store.Warning);
        FooterStatus.Text=demo?L.T("MatchLens 0.4.9 · Демо"):L.F($"MatchLens 0.4.9 · {L.T(gsiStatus)} · {activeLoads+pending.Count} профілів у завантаженні · {L.T(telegram.Status)}");
        ShowSelected();presentation?.Refresh(s,players.ToList());
    }
    void PresentationClick(object sender,RoutedEventArgs e)=>TogglePresentation();
    void TogglePresentation()
    {
        if(closing)return;
        if(presentation!=null){presentation.Close();return;}
        if(PresentationDisplay.Current()==null){FooterStatus.Text=L.T("Підключи другий монітор і вибери розширення робочого столу у Windows.");return;}
        var window=new PresentationWindow();presentation=window;window.Refresh(CurrentState(),players.ToList());
        window.Closed+=(_,_)=>{if(presentation==window)presentation=null;PresentationButton.BorderBrush=(System.Windows.Media.Brush)Application.Current.FindResource("Line");tray?.PresentationState(false);};
        if(!window.ShowOnSecondMonitor()){window.Close();FooterStatus.Text=L.T("Не вдалося відкрити показ. Перевір налаштування другого монітора.");return;}
        PresentationButton.BorderBrush=(System.Windows.Media.Brush)Application.Current.FindResource("Accent");tray?.PresentationState(true);
    }
    public PresentationWindow PresentationPreview(){var window=new PresentationWindow(true);window.Refresh(CurrentState(),players.ToList());return window;}
    public void OpenPreviewProfile(bool low=false){Select(low?players.Skip(3).FirstOrDefault():players.FirstOrDefault(p=>!p.IsLocal));}
    public FrameworkElement PopupPreview()
    {
        if(!preview)throw new InvalidOperationException("Preview only");
        Select(players.Skip(3).FirstOrDefault());PlayerSelector.ApplyTemplate();
        var popup=(System.Windows.Controls.Primitives.Popup)PlayerSelector.Template.FindName("PART_Popup",PlayerSelector);
        var surface=(FrameworkElement)popup.Child;surface.Width=360;
        surface.Measure(new Size(360,440));surface.Arrange(new Rect(0,0,360,surface.DesiredSize.Height));surface.UpdateLayout();
        return surface;
    }
    void Select(Player? p)
    {
        selected=p;selectionChanging=true;PlayerSelector.SelectedItem=p;selectionChanging=false;
        ProfileTab.IsEnabled=p!=null;MainTabs.SelectedIndex=p==null?0:1;if(p!=null){QueueStats(p,true);ShowSelected();}
    }
    void ShowSelected()
    {
        if(selected is not {} p){drawnPlayer=null;return;}
        var self=players.FirstOrDefault(x=>x.IsLocal);var leet=p.Sources.FirstOrDefault(c=>c.Name=="LEETIFY");
        if(drawnPlayer==p&&drawnSources==p.Sources&&drawnSelfSources==self?.Sources&&drawnLive==p.Live&&drawnAvatar==p.Avatar&&drawnName==p.Name)return;
        drawnPlayer=p;drawnSources=p.Sources;drawnSelfSources=self?.Sources;drawnLive=p.Live;drawnAvatar=p.Avatar;drawnName=p.Name;
        SelectedName.Text=p.Name;SelectedMeta.Text=$"{L.T(p.IsLocal?"ЦЕ ВИ":"STEAM-ПРОФІЛЬ")}  ·  {p.Id}";ProfileAvatar.Source=p.Avatar;
        SelectedScope.Text=L.T(leet?.Scope is {Length:>0} scope?scope:"Показники з відкритих сторінок");
        var tiles=Tiles(p,("kd","K/D"),("leetify_rating",L.T("Рейтинг Leetify")),("premier","Premier"),("faceit_elo","FACEIT Elo"),("kast","KAST"),("winrate",L.T("Перемоги")),("aim",L.T("Прицілювання")),("utility",L.T("Гранати")),("ttd",L.T("Час до шкоди")),("crosshair",L.T("Приціл, відхилення")),("adr","ADR"));
        var trust=Find(p,"trust");tiles.Add(MetricAppearance.Tile("trust",L.T("Траст cstracker"),trust?.Display??"—","CSTRACKER",trust,p,self,"CSTRACKER"));ProfileTiles.ItemsSource=tiles;
        var win=leet?.Numbers.FirstOrDefault(n=>n.Key=="winrate");WinChart.RingValue=win?.Value;WinChart.RingTone=tiles.FirstOrDefault(t=>t.Key=="winrate")?.Tone??MetricTone.Neutral;WinChart.ToolTip=tiles.FirstOrDefault(t=>t.Key=="winrate")?.Tooltip;WinChart.EmptyText=L.T("Немає відкритого win rate");WinChart.InvalidateVisual();WinScope.Text=L.T(leet?.Scope??"Період не вказано");
        SkillChart.Data=leet?.Numbers.Where(n=>n.Key is "aim" or "utility" or "positioning").Select(n=>new BarDatum(n.Label,n.Value,100,n.Display,Tone:MetricAppearance.Tile(n.Key,n.Label,n.Display,"LEETIFY",n,p,self,"LEETIFY").Tone)).ToList()??[];
        SkillScope.Text=L.T(leet?.Scope??"");SkillChart.EmptyText=L.T("Оцінки ще недоступні");
        var compare=PlayerComparison.Read(p,self);CompareTitle.Text=p.IsLocal?L.T("Ваш орієнтир"):L.T("Порівняння з вами");
        CompareMeta.Text=compare.Available?L.F($"{compare.Label} · індекс {compare.Index:+0.0;-0.0;0.0}"):compare.Label;CompareMeta.ToolTip=compare.Detail;
        CompareChart.EmptyText=p.IsLocal?L.T("Оберіть іншого гравця"):compare.Detail;
        CompareChart.Data=compare.Metrics.Where(m=>m.Player>=0&&m.Yours>=0).Take(3).Select(m=>new BarDatum(m.Label,m.Player,Math.Max(Math.Max(m.Player,m.Yours)*1.2,1),m.Player.ToString("0.##")+m.Unit,m.Yours,m.Yours.ToString("0.##")+m.Unit,MetricAppearance.Tile(m.Key,m.Label,m.Player.ToString(),"LEETIFY",leet?.Numbers.FirstOrDefault(n=>n.Key==m.Key),p,self,"LEETIFY").Tone)).ToList();
        var mapSource=p.Sources.FirstOrDefault(c=>c.Maps.Count>0||c.MapRates.Count>0);
        MapsChart.Data=mapSource?.MapRates.Count>0?mapSource.MapRates.OrderByDescending(m=>m.Matches).Take(3).Select(m=>new BarDatum(m.Map.ToUpperInvariant(),m.WinRate,100,L.F($"{m.WinRate:0.#}% · {m.Matches} матчів"))).ToList():mapSource?.Maps.OrderByDescending(m=>m.Matches).Take(3).Select(m=>new BarDatum(m.Map.ToUpperInvariant(),(double)m.Wins/m.Matches*100,100,$"{(double)m.Wins/m.Matches:P0} · {m.Wins}/{m.Matches}")).ToList()??[];
        MapsMeta.Text=mapSource==null?L.T("Сайт ще не віддав відкриту статистику карт"):L.T(mapSource.MapRates.FirstOrDefault()?.Scope)??L.F($"{mapSource.Name} · перемоги / матчі");MapsChart.EmptyText=L.T("Карти недоступні у відкритій сторінці");
        var history=p.Sources.FirstOrDefault(c=>c.History.Count>0);HistoryChart.Points=history?.History??[];
        HistoryMeta.Text=history==null?L.T("Відкрита історія матчів ще недоступна"):L.F($"{history.Name} · рейтинг у {history.History.Count} видимих матчах · ")+string.Join(" / ",history.History.Select(m=>L.T(m.Scope)).Distinct().Take(4));
        var live=p.Live;LiveTiles.ItemsSource=live==null?new List<StatTile>():new List<StatTile>{new(){Label=L.T("Убивства"),Value=live.Kills?.ToString()??"—",Hint="GSI"},new(){Label=L.T("Смерті"),Value=live.Deaths?.ToString()??"—",Hint="GSI"},new(){Label=L.T("Асисти"),Value=live.Assists?.ToString()??"—",Hint="GSI"}};
        LiveTiles.ItemsSource=((IEnumerable<StatTile>)LiveTiles.ItemsSource).Select(t=>MetricAppearance.Tile(MetricAppearance.KeyFor(t.Label),t.Label,t.Value,t.Hint)).ToList();
        LiveEmpty.Text=live==null?L.T("CS2 не передала поточну статистику цього гравця."):"";
        object SourceView(SourceCard c)=>new{c.Name,c.Url,c.Status,c.Footer,ShowAttribution=!demo&&c.Name=="LEETIFY"&&c.Metrics.Count>0,IconKey=MetricAppearance.SourceKey(c.Name),Metrics=MetricAppearance.Source(c,p,self),Matches=c.RecentMatches,
            TrustSummary=c.Name!="CSTRACKER"?"":c.Numbers.Any(n=>n.Key=="trust")?(c.TrustUpdated is {} time?L.F($"Оновлено cstracker {time.ToLocalTime():dd.MM.yyyy HH:mm}. "):"")+ (c.TrustAdjustments.Count>0?L.T("Впливи на траст за даними сайту:"):L.T("Сайт не віддав деталізацію корекцій; оцінка показана без перерахунку.")):L.T("Оцінка трасту поки недоступна."),
            Adjustments=c.TrustAdjustments.Select(r=>new{r.Label,r.Display,Brush=MetricPalette.Brush(r.Delta<0?MetricTone.Low:MetricTone.Good)}).ToList()};
        SourcesLeft.ItemsSource=p.Sources.Where(c=>c.Name is "CSTRACKER" or "LEETIFY" or "CSSTATS").OrderBy(c=>c.Name=="CSTRACKER"?0:c.Name=="LEETIFY"?1:2).Select(SourceView).ToList();
        SourcesRight.ItemsSource=p.Sources.Where(c=>c.Name is not ("CSTRACKER" or "LEETIFY" or "CSSTATS")).OrderBy(c=>c.Name=="FACEIT"?0:c.Name=="STEAM"?1:2).Select(SourceView).ToList();CoverageLabel.Text=L.F($"{p.Sources.Sum(c=>c.Metrics.Count)} доступних показників · {p.Sources.Count(c=>c.Metrics.Count>0)}/{p.Sources.Count} джерел");
    }
    void PlayerClicked(object sender,RoutedEventArgs e){if(sender is Button{Tag:Player p})Select(p);}
    void PlayerChosen(object sender,SelectionChangedEventArgs e){if(!selectionChanging&&PlayerSelector.SelectedItem is Player p)Select(p);}
    void SelfClick(object sender,RoutedEventArgs e){if(players.FirstOrDefault(p=>p.IsLocal) is {} p)Select(p);}
    void SearchChanged(object sender,TextChangedEventArgs e){if(settings!=null)UpdateView();}
    public void SetDemo()
    {
        demo=true;CancelFetches();Select(null);players.Clear();
        demoState=new(){Active=true,Demo=true,Map="de_mirage",Mode="competitive",Phase="live",Generation=-1,LocalId=(SteamIds.Base+100000).ToString()};
        var names=new[]{"THUNDERBOLD","nordic.exe","afterglow","pixel","NOVA","frostbite","sundown","KØBEN","quietshot","NightShift"};
        var skills=new[]{58,84,63,42,72,69,54,75,49,67};
        for(int i=0;i<names.Length;i++)
        {
            var id=(SteamIds.Base+100000+(ulong)i).ToString();var cards=Providers.Empty(id);var l=cards[0];
            l.SampleCount=30;l.Scope="Останні 30 матчів · усі джерела 5v5";l.Status="ДЕМО · вигадані показники";l.FetchedAt=DateTimeOffset.Now;l.Note="Демонстрація, не реальна статистика";
            l.Numbers=[new("kd","K/D",.85+skills[i]/100d),new("leetify_rating","Рейтинг Leetify",(skills[i]-55)/5d),new("winrate","Перемоги",43+skills[i]/5d,"%"),new("kast","KAST",48+skills[i]/3d,"%"),new("aim","Прицілювання",skills[i]," / 100"),new("utility","Гранати",skills[i]-10," / 100"),new("positioning","Позиціонування",skills[i]-4," / 100"),new("ttd","Час до шкоди",820-skills[i]*3," мс"),new("crosshair","Розміщення прицілу",17-skills[i]/10d,"°"),new("trade_success","Успішність розмінів",skills[i]-15,"%"),new("accuracy_spotted","Влучність по видимій цілі",skills[i]/2d,"%"),new("kills_match","Убивства / матч",skills[i]/4d),new("adr","ADR",52+skills[i]/2d),new("headshots","Хедшоти",skills[i]/2d,"%"),new("premier","Premier",10000+skills[i]*110),new("faceit_elo","FACEIT Elo",800+skills[i]*15)];
            l.Metrics=l.Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();l.Maps=[new("mirage",18+i,30),new("nuke",13+i,28),new("ancient",12+i,25)];
            l.History=Enumerable.Range(0,18).Select(n=>new MatchPoint($"{n+1:00}.09",Math.Round(Math.Sin(n*1.8+i)*2.8+(skills[i]-55)/8d,2),"","ДЕМО")).ToList();
            cards[1].Numbers=l.Numbers.Where(n=>n.Key is "premier" or "adr" or "headshots").ToList();cards[1].Metrics=cards[1].Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();
            cards[2].Numbers=l.Numbers.Where(n=>n.Key is "aim" or "utility" or "kd" or "ttd").ToList();cards[2].Metrics=cards[2].Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();
            cards[3].Metrics=[new("Elo",(800+skills[i]*15).ToString()),new("Рівень","7")];cards[4].Metrics=[new("Рівень Steam","28"),new("Дата створення","18.03.2019"),new("VAC-бан","Немає позначки")];
            cards[3].Numbers=[new("faceit_elo","FACEIT Elo",800+skills[i]*15),new("faceit_level","FACEIT рівень",7)];cards[3].Metrics=cards[3].Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();
            cards[3].RecentMatches=Enumerable.Range(0,5).Select(n=>new RecentMatch(DateTimeOffset.UtcNow.AddDays(-n-1),new[]{"mirage","nuke","ancient"}[n%3],n%3==0?"Поразка":"Перемога",n%3==0?"9:13":"13:8",16+n,11+n,3+n,Source:"ДЕМО · FACEIT")).ToList();TrackingProfiles.AggregateRecent(cards[3]);
            var ct=cards[5];ct.SampleCount=34;ct.Scope="ДЕМО · останні 34 матчі";ct.TrustUpdated=DateTimeOffset.Now.AddDays(-7);
            ct.TrustAdjustments=[new("Шкода союзникам",-1.9,"team_damage"),new("Засліплення союзників",-1.8,"team_flashes"),new("Час AFK",-.8,"afk"),new("Вбивства крізь стіни",-2.5,"wallbangs")];
            ct.Numbers=[new("trust","Траст cstracker",93,"%",ct.Scope),new("kd","K/D",1.32,"",ct.Scope),new("accuracy","Точність у полі зору",24.8,"%",ct.Scope),new("ttd","Час до шкоди",651," мс",ct.Scope),new("headshots","Хедшоти",61,"%",ct.Scope),new("team_damage","Шкода своїм / матч",31.2,"","ДЕМО · усі матчі"),new("teamkills","Тімкіли / матч",.1,"","ДЕМО · усі матчі"),new("team_flashes","Засліплено своїх / флешка",.8,"","ДЕМО · усі матчі")];
            ct.ReviewRules=[new("kd",1.8,false,ct.Scope),new("accuracy",40,false,ct.Scope),new("ttd",600,true,ct.Scope)];
            if(i==3){ct.Numbers[0]=ct.Numbers[0] with{Value=43};ct.Numbers[1]=ct.Numbers[1] with{Value=2.8};ct.Numbers[2]=ct.Numbers[2] with{Value=65};ct.Numbers[3]=ct.Numbers[3] with{Value=420};ct.Numbers[4]=ct.Numbers[4] with{Value=92};ct.TrustAdjustments.Add(new("Хедшоти",-15,"headshots"));}
            ct.Metrics=ct.Numbers.Select(n=>new Metric(n.Label,n.Display)).ToList();
            foreach(var c in cards){c.Status="ДЕМО · вигадані показники";c.FetchedAt=DateTimeOffset.Now;c.Note="Демонстрація дизайну";}
            players.Add(new Player{Id=id,Name=names[i],Confirmed=true,Evidence="ДЕМО",IsLocal=i==0,Sources=cards,Live=new(14+i,9+i,3,1,32+i)});
        }
        UpdateView();
    }
    async void DemoClick(object sender,RoutedEventArgs e){if(!demo){await telegram.Update(settings,new MatchState(),[]);SetDemo();}else{demo=false;seenGeneration=-1;Sync();await Tick();}}
    async void RefreshClick(object sender,RoutedEventArgs e)
    {
        if(demo){SetDemo();return;}CancelFetches();providers.ClearCache();avatars.RetryFailed();foreach(var p in players.OrderByDescending(p=>p.IsLocal||p==selected)){QueueStats(p);if(p.Avatar==null)_=LoadAvatar(p);}nextSteamPoll=default;await Tick();
    }
    void SettingsClick(object sender,RoutedEventArgs e)
    {
        timer.Stop();var w=new SettingsWindow(settings,telegram){Owner=this};w.ShowDialog();
        if(w.Saved){settings=w.Value;L.Set(L.OverrideLanguage??settings.Language);drawnPlayer=null;foreach(var p in players)p.Refresh();tray?.RefreshLanguage();CancelFetches();providers.ClearCache();if(!demo)foreach(var p in players)QueueStats(p);UpdateView();}
        if(!preview&&!closing)timer.Start();
    }
    void CopyDiagnosticsClick(object sender,RoutedEventArgs e)
    {
        var s=tracker.State;var sources=players.SelectMany(p=>p.Sources).GroupBy(c=>c.Name).Select(g=>L.F($"{g.Key}: {g.Count(c=>c.Metrics.Count>0)}/{g.Count()} з даними · ")+string.Join(" / ",g.Select(c=>c.Status+(c.Diagnostic.Length>0?" ["+c.Diagnostic+"]":"")).Distinct().Take(4)));
        var text=L.F($"MatchLens 0.4.9\n{L.T(gsiStatus)}\nМатч активний: {s.Active}; режим: {s.Mode}\nSteam: {steamDiscovery.Status}\nВідбір: {L.T(recentDiscovery.Status)}\nОстання відповідь: {lastSteamPoll?.ToString("u")??"ще немає"}\nПрофілі: {players.Count}; GSI: {players.Count(p=>p.Confirmed)}; аватари: {players.Count(p=>p.Avatar!=null)}\n")+string.Join("\n",sources);
        try{Clipboard.SetText(text);FooterStatus.Text=L.T("Діагностику скопійовано · без токенів і Steam ID");}catch{FooterStatus.Text=L.T("Не вдалося скопіювати діагностику");}
    }
    public void PreviewDetails(){ProfileScroll.UpdateLayout();var position=SourceArea.TransformToAncestor(ProfileScroll).Transform(new Point(0,0));ProfileScroll.ScrollToVerticalOffset(ProfileScroll.VerticalOffset+position.Y-70);ProfileScroll.UpdateLayout();}
    public static void OpenUrl(string url){if(Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https")try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch{}}
    void SourceClick(object sender,RoutedEventArgs e){if(!demo&&sender is Button{Tag:string url})OpenUrl(url);}
    void ProfileClick(object sender,RoutedEventArgs e){if(!demo&&selected!=null)OpenUrl($"https://steamcommunity.com/profiles/{selected.Id}");}
    void CsStatsClick(object sender,RoutedEventArgs e){if(!demo&&selected!=null)OpenUrl($"https://csstats.gg/player/{selected.Id}");}
    void TitleDrag(object sender,MouseButtonEventArgs e){if(e.ChangedButton!=MouseButton.Left)return;if(e.ClickCount==2)ToggleMaximize();else if(e.LeftButton==MouseButtonState.Pressed)try{DragMove();}catch(InvalidOperationException){}}
    void ToggleMaximize()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    void WindowKeys(object sender,KeyEventArgs e){if(e.Key==Key.F11||(e.Key==Key.System&&e.SystemKey==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Alt)!=0)){ToggleMaximize();e.Handled=true;}}
    void WindowResized(object sender,SizeChangedEventArgs e)=>UpdateWindowShape();
    void UpdateWindowShape(){if(WindowFrame!=null)WindowFrame.CornerRadius=new(WindowState==WindowState.Maximized?0:26);}
    void MaximizeClick(object sender,RoutedEventArgs e)=>ToggleMaximize();
    void MinimizeClick(object sender,RoutedEventArgs e)=>HideToTray();
    void ExitClick(object sender,RoutedEventArgs e)=>Close();
    void HideToTray(){if(closing||preview)return;Hide();ShowInTaskbar=false;}
    public void RestoreWindow(){if(closing)return;ShowInTaskbar=true;WindowState=restoreState;Show();Activate();Topmost=true;Topmost=false;Focus();}
    async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(preview){timer.Stop();viewTimer.Stop();return;}if(closed)return;e.Cancel=true;if(closing)return;closing=true;presentation?.Close();timer.Stop();viewTimer.Stop();CancelFetches();IsEnabled=false;
        try{for(var i=0;busy&&i<50;i++)await Task.Delay(200);if(telegramTask!=null)await telegramTask;await telegram.Update(settings,new MatchState(),[],true);await gsi.DisposeAsync();}
        catch{}
        finally{steamDiscovery.Dispose();tray?.Dispose();providers.Dispose();avatars.Dispose();telegram.Dispose();closed=true;Close();}
    }
}
