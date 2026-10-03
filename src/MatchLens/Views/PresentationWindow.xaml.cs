using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MatchLens;
public partial class PresentationWindow : Window
{
    readonly Dictionary<string,(string Stamp,FrameworkElement Card)> cards=[];
    readonly bool preview;
    PresentationInput? input;
    string? monitor;
    bool closed;
    public PresentationWindow(bool preview=false)
    {
        this.preview=preview;InitializeComponent();Style=(Style)Application.Current.FindResource(typeof(Window));
        SourceInitialized+=(_,_)=>input=new PresentationInput(this,ScrollWheel,ready=>WheelHint.Text=ready?
            L.T("Другий монітор • наведи курсор і прокручуй колесиком"):
            L.T("Для колесика увімкни у Windows «Прокручування неактивних вікон»"));
        Loaded+=(_,_)=>{if(!preview){input?.Start();SystemEvents.DisplaySettingsChanged+=DisplayChanged;}};
        Closed+=(_,_)=>{closed=true;SystemEvents.DisplaySettingsChanged-=DisplayChanged;input?.Dispose();input=null;};
        PreviewMouseDown+=(_,e)=>e.Handled=true;
        ((FrameworkElement)Content).SizeChanged+=(_,e)=>PlayerGrid.Columns=e.NewSize.Width>=1650?3:e.NewSize.Width>=920?2:1;
        StatisticsScroll.ScrollChanged+=(_,_)=>ScrollHint.Text=StatisticsScroll.ScrollableHeight>0?
            L.F($"{(int)Math.Round(StatisticsScroll.VerticalOffset/StatisticsScroll.ScrollableHeight*100)}% • ↓ колесико"):L.T("Усі дані на екрані");
    }
    public bool ShowOnSecondMonitor()
    {
        var target=PresentationDisplay.Current();if(target==null)return false;
        monitor=target.Name;new WindowInteropHelper(this).EnsureHandle();
        if(input?.Place(target)!=true){Close();return false;}
        Show();return input.Place(target);
    }
    void DisplayChanged(object? sender,EventArgs e)=>Dispatcher.BeginInvoke(new Action(()=>
    {
        if(closed)return;var target=PresentationDisplay.Current(monitor);
        if(target==null||input?.Place(target)!=true)Close();else monitor=target.Name;
    }));
    void ScrollWheel(int delta)=>StatisticsScroll.ScrollToVerticalOffset(PresentationDisplay.WheelOffset(
        StatisticsScroll.VerticalOffset,StatisticsScroll.ExtentHeight,StatisticsScroll.ViewportHeight,delta,SystemParameters.WheelScrollLines));
    static NumericMetric? Find(Player? p,string key)=>p?.Sources.OrderBy(c=>c.Name=="LEETIFY"?0:c.Name=="CSSTATS"?1:2).SelectMany(c=>c.Numbers).FirstOrDefault(n=>n.Key==key);
    static string Value(Player? p,string key)=>Find(p,key)?.Display??"—";
    public void Refresh(MatchState state,IReadOnlyList<Player> players)
    {
        if(closed)return;
        var active=state.Active;var list=active?players.OrderByDescending(p=>p.IsLocal).ToList():[];
        var self=list.FirstOrDefault(p=>p.IsLocal);
        MatchMeta.Text=active?$"{state.Map.ToUpperInvariant()}  /  {state.ModeLabel}  /  {state.CtScore} : {state.TScore}":L.T("Очікуємо наступний матч");
        LiveLabel.Text=state.Demo?L.T("ДЕМО • ПРИКЛАД ДАНИХ"):active?L.T("НАЖИВО • АВТООНОВЛЕННЯ"):L.T("ВИ НЕ В ГРІ");
        SelfSummary.Text=self?.Name??L.T("Чекаємо твій профіль");
        SelfDetail.Text=self==null?L.T("Вхід у матч визначається автоматично"):$"K/D {Value(self,"kd")}  ·  ADR {Value(self,"adr")}  ·  Leetify {Value(self,"leetify_rating")}";
        var comparisons=list.Where(p=>!p.IsLocal).Select(p=>(Player:p,Result:PlayerComparison.Read(p,self))).Where(x=>x.Result.Available).OrderByDescending(x=>x.Result.Index).ToList();
        var harder=comparisons.Where(x=>x.Result.Index>8).Take(3).ToList();
        ThreatSummary.Text=harder.Count>0?string.Join("  ·  ",harder.Select(x=>x.Player.Name)):comparisons.Count>0?L.T("Близька до тебе форма"):L.T("Очікуємо статистику");
        ThreatDetail.Text=L.F($"{comparisons.Count} з {Math.Max(0,list.Count-1)} профілів можна порівняти з твоїм");
        RosterCount.Text=L.F($"{list.Count} гравців  ·  {list.Count(p=>p.Sources.Any(s=>s.Numbers.Count>0))} зі статистикою");
        EmptyLabel.Visibility=list.Count==0?Visibility.Visible:Visibility.Collapsed;
        var ids=list.Select(p=>p.Id).ToHashSet();foreach(var id in cards.Keys.Where(id=>!ids.Contains(id)).ToArray()){PlayerGrid.Children.Remove(cards[id].Card);cards.Remove(id);}
        foreach(var p in list)
        {
            var stamp=Stamp(p,self);if(!cards.TryGetValue(p.Id,out var old)||old.Stamp!=stamp)
            {
                var card=Card(p,self);var index=old.Card==null?-1:PlayerGrid.Children.IndexOf(old.Card);
                if(index>=0){PlayerGrid.Children.RemoveAt(index);PlayerGrid.Children.Insert(index,card);}else PlayerGrid.Children.Add(card);
                cards[p.Id]=(stamp,card);
            }
        }
        // Reconcile membership without resetting the ScrollViewer when partial stats arrive.
        for(var i=0;i<list.Count;i++){var card=cards[list[i].Id].Card;var index=PlayerGrid.Children.IndexOf(card);if(index!=i){PlayerGrid.Children.RemoveAt(index);PlayerGrid.Children.Insert(i,card);}}
    }
    static string Stamp(Player p,Player? self)=>L.Language+"|"+p.Name+"|"+p.IsLocal+"|"+(p.Avatar==null?0:RuntimeHelpers.GetHashCode(p.Avatar))+"|"+p.Live+"|"+
        string.Join("|",p.Sources.Select(s=>s.Name+":"+s.Status+":"+s.Scope+":"+s.SampleCount+":"+string.Join(",",s.Numbers)))+"|"+PlayerComparison.Read(p,self)+"|"+
        string.Join("|",self?.Sources.SelectMany(s=>s.Numbers)??[]);
    FrameworkElement Card(Player p,Player? self)
    {
        var stack=new StackPanel();var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new(62)});header.ColumnDefinitions.Add(new());
        var avatar=new Border{Width=48,Height=48,CornerRadius=new(24),Background=(Brush)Application.Current.FindResource("Glass"),HorizontalAlignment=HorizontalAlignment.Left,
            Clip=new EllipseGeometry(new Point(24,24),24,24)};
        avatar.Child=p.Avatar!=null?new Image{Source=p.Avatar,Stretch=Stretch.UniformToFill}:new MetricGlyph{Kind="person",Width=26,Height=26};header.Children.Add(avatar);
        var identity=new StackPanel{VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(identity,1);header.Children.Add(identity);
        identity.Children.Add(Text(p.Name,20,false,true));identity.Children.Add(Text(p.IsLocal?L.T("ТВІЙ ПРОФІЛЬ"):"STEAM  ·  "+p.Id,11,true));stack.Children.Add(header);
        var comp=PlayerComparison.Read(p,self);var note=p.IsLocal?L.T("Твоя статистика для порівняння"):comp.Available?comp.Label:L.T("Недостатньо даних для порівняння");
        var noteText=Text(note,13,true);noteText.Margin=new(0,14,0,14);stack.Children.Add(noteText);
        var tiles=new UniformGrid{Columns=3};
        foreach(var (key,label) in new (string,string)[]{("trust",L.T("Траст cstracker")),("kd","K/D"),("adr","ADR"),("headshots",L.T("Хедшоти")),("leetify_rating","Leetify"),("faceit_elo","FACEIT Elo"),("winrate",L.T("Перемоги")),("aim",L.T("Точність / aim")),("ttd",L.T("Час до шкоди"))})
        {
            var metric=Find(p,key);var origin=p.Sources.OrderBy(s=>s.Name=="LEETIFY"?0:s.Name=="CSSTATS"?1:2).FirstOrDefault(s=>s.Numbers.Any(n=>n.Key==key));
            var appearance=MetricAppearance.Tile(key,label,metric?.Display??"—",origin?.Name??L.T("Немає даних"),metric,p,self,origin?.Name);
            var contents=new StackPanel();var caption=new StackPanel{Orientation=Orientation.Horizontal};caption.Children.Add(new MetricGlyph{Kind=key,Width=17,Height=17,Margin=new(0,0,6,0)});caption.Children.Add(Text(label,11,true));contents.Children.Add(caption);
            var value=Text(appearance.Value,25,false,true);value.Foreground=appearance.ValueBrush;
            contents.Children.Add(new Viewbox{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Left,Height=32,Margin=new(0,7,0,4),Child=value});
            contents.Children.Add(Text(origin?.Name??L.T("Немає даних"),10,true));
            tiles.Children.Add(new Border{CornerRadius=new(18),Background=new SolidColorBrush(Color.FromArgb(9,255,255,255)),BorderBrush=new SolidColorBrush(Color.FromArgb(14,255,255,255)),BorderThickness=new(1),Padding=new(12),Margin=new(0,0,8,8),Child=contents});
        }
        stack.Children.Add(tiles);
        if(p.Live is {} live){var t=Text(L.F($"У цьому матчі  ·  K {live.Kills?.ToString()??"—"}  /  D {live.Deaths?.ToString()??"—"}  /  A {live.Assists?.ToString()??"—"}"),13);t.Margin=new(0,8,0,0);stack.Children.Add(t);}
        var sourceLine=Text(string.Join("  ·  ",p.Sources.Where(s=>s.Numbers.Count>0).Select(s=>s.Name+(s.SampleCount is {} n?L.F($" ({n} матчів)"):""))),11,true);sourceLine.Margin=new(0,10,0,0);stack.Children.Add(sourceLine);
        return new Border{Style=(Style)Application.Current.FindResource("GlassCard"),CornerRadius=new(26),Padding=new(22),Margin=new(0,0,16,16),Child=stack};
    }
    static TextBlock Text(string value,double size,bool muted=false,bool bold=false)=>new(){Text=value,FontSize=size,Foreground=muted?(Brush)Application.Current.FindResource("Muted"):MetricPalette.Neutral,
        FontWeight=bold?FontWeights.Medium:FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
}
