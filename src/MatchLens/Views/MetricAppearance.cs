using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MatchLens;

public enum MetricTone { Neutral, Good, Low }
public static class MetricPalette
{
    public static readonly Brush Neutral=Make("#EEEAF4"),Good=Make("#7BD4AB"),Low=Make("#E994A0");
    static Brush Make(string value){var b=(Brush)new BrushConverter().ConvertFromString(value)!;b.Freeze();return b;}
    public static Brush Brush(MetricTone tone)=>tone switch{MetricTone.Good=>Good,MetricTone.Low=>Low,_=>Neutral};
}
public sealed class MetricColumnsConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is double width&&width>=1040?6:4;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public static class MetricAppearance
{
    static readonly HashSet<string> inverse=["ttd","crosshair"];
    public static string KeyFor(string label,string source="")=>label switch
    {
        "K/D"=>"kd","Рейтинг Leetify"=>"leetify_rating","Перемоги" or "Перемоги в сезоні"=>"winrate",
        "Premier" or "Найкращий Premier"=>"premier","FACEIT Elo" or "Elo"=>"faceit_elo","FACEIT рівень"=>"faceit_level",
        "KAST"=>"kast","Прицілювання"=>"aim","Гранати"=>"utility","Позиціонування"=>"positioning",
        "Час до шкоди"=>"ttd","Розміщення прицілу" or "Приціл, відхилення"=>"crosshair",
        "ADR"=>"adr","Хедшоти"=>"headshots","Убивства / матч" or "Убивства" or "Kills"=>"kills",
        "Смерті" or "Deaths"=>"deaths","Асисти" or "Assists"=>"assists","Успішність розмінів"=>"trade_success",
        "Влучність по видимій цілі"=>"accuracy_spotted","Початкові дуелі"=>"opening","Клатчі"=>"clutch",
        "VAC-бан"=>"vac","Траст Valve"=>"trust","Дата створення" or "Вік акаунта"=>"calendar",
        "Рівень Steam"=>"steam","Ім'я Steam"=>"person","Приватність профілю" or "Торгівля акаунта"=>"shield",
        "Статус"=>"status","Соло-матчі"=>"person","Група 2-4 гравці" or "Повна група з 5 гравців"=>"assists",
        "Рівень" when source=="FACEIT"=>"faceit_level",_=>"chart"
    };
    public static string SourceKey(string name)=>name switch{"LEETIFY"=>"leetify_rating","FACEIT"=>"faceit_elo","STEAM"=>"steam","SCOPE.GG"=>"scope","CSTRACKER"=>"shield",_=>"chart"};
    public static StatTile Tile(string key,string label,string value,string hint,NumericMetric? number=null,Player? player=null,Player? self=null,string? source=null)
    {
        var tone=MetricTone.Neutral;var explanation=L.T("Недостатньо даних для оцінки нетиповості.");
        var card=player?.Sources.FirstOrDefault(c=>c.Name==source);
        if(number is {} n&&double.IsFinite(n.Value))
        {
            bool score=key is "trust";
            bool performance=key is "recent_kd" or "recent_winrate" or "recent_kills" or "recent_matches" or "kd" or "adr" or "headshots" or "winrate" or "leetify_rating" or "kast" or "aim" or "utility" or "positioning" or "ttd" or "crosshair" or "accuracy_spotted" or "accuracy" or "preaim" or "aim_offset" or "hltv_rating" ||source=="CSTRACKER"&&key is not ("faceit_elo" or "faceit_level");
            if(score)
            {
                // Display the site's actual score; this UI cutoff is not its formula.
                tone=n.Value>=80?MetricTone.Good:MetricTone.Low;
                explanation=L.T("Оцінка cstracker.gg. Нижче 80% — червоний індикатор MatchLens; причини наведені в джерелі. Це не відсоток ймовірності читів.");
                if(card?.TrustUpdated is {} updated)explanation+=L.F($" Оновлено сайтом {updated.ToLocalTime():dd.MM.yyyy HH:mm}.");
            }
            else if(performance)
            {
                tone=MetricTone.Good;explanation=L.T("Звичайне значення за доступними правилами; зелений колір не означає перевірку античитом.");
                var rule=card?.ReviewRules.FirstOrDefault(r=>r.Key==key);
                var penalty=card?.TrustAdjustments.FirstOrDefault(r=>r.Delta<0&&(r.Key==key||key=="detail_headshots"&&r.Key=="headshots"));
                if(rule!=null&&rule.Unusual(n.Value))
                {tone=MetricTone.Low;explanation=L.F($"Поріг статистичного огляду cstracker: {L.T(rule.Lower?"нижче":"вище")} {rule.Cutoff:0.##}{L.T(n.Unit)}. {L.T(rule.Scope)}.");}
                else if(card?.ReviewFlags.Contains(key)==true||penalty!=null)
                {tone=MetricTone.Low;explanation=penalty!=null?L.F($"cstracker знизив оцінку: {L.T(penalty.Label)} {penalty.Display}."):L.T("cstracker позначив це значення як нетипове на своїй сторінці.");}
                else if(source!="CSTRACKER")
                {
                    // Conservative local review markers, separately attributed; never
                    // apply raw shot accuracy thresholds to normalized aim/spotted aim.
                    var samples=card?.SampleCount??0;
                    double? threshold=key switch{"kd"=>2.5,"adr"=>130,"headshots"=>85,"kast"=>92,_=>null};
                    if(samples>=20&&threshold is {} limit&&n.Value>limit)
                    {tone=MetricTone.Low;explanation=L.F($"Локальний індикатор MatchLens: {label} вище {limit:0.##}{L.T(n.Unit)}, вибірка {samples} матчів. Не є корекцією трасту cstracker.");}
                }
            }
        }
        if(key=="vac"&&value=="Є VAC-бан"){tone=MetricTone.Low;explanation=L.T("Публічний профіль Steam повідомляє VAC-бан.");}
        return new(){Key=key,Label=L.T(label),Value=L.T(value),Hint=number?.Scope is {Length:>0} metricScope?L.T(metricScope):L.T(hint),Tone=tone,Tooltip=L.T(L.T(label)+": "+value+"\n"+explanation+(number?.Scope is {Length:>0} scope?"\n"+scope:"")+L.T("\nНетипова статистика не є доказом читів."))};
    }
    public static List<StatTile> Source(SourceCard card,Player player,Player? self)=>card.Metrics.Select(m=>
    {
        var n=card.Numbers.FirstOrDefault(n=>n.Label==m.Label);
        return Tile(n?.Key??KeyFor(m.Label,card.Name),m.Label,m.Value,card.Name,n,player,self,card.Name);
    }).ToList();
}

public sealed class MetricGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(MetricGlyph),new FrameworkPropertyMetadata("chart",FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind {get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    static readonly Dictionary<string,string> paths=new()
    {
        ["kd"]="M4,17 L4,7 M8,7 L8,11 4,11 M8,11 L10,17 M14,7 L14,17 17,17 C22,17 22,7 17,7 Z",
        ["winrate"]="M5,21 L5,3 M5,4 C10,0 14,8 20,4 L20,14 C14,18 10,10 5,14",
        ["premier"]="M4,7 L8,11 12,4 16,11 20,7 18,18 6,18 Z M7,21 L17,21",
        ["kast"]="M12,2 L20,6 20,12 C20,18 12,22 12,22 C12,22 4,18 4,12 L4,6 Z M8,12 L11,15 16,9",
        ["aim"]="M8,12 A4,4 0 1 1 16,12 A4,4 0 1 1 8,12 M12,2 L12,6 M12,18 L12,22 M2,12 L6,12 M18,12 L22,12",
        ["utility"]="M9,8 C3,10 4,21 12,21 C20,21 21,10 15,8 Z M10,8 L10,4 15,4 15,8 M15,4 L19,7 M10,4 L10,2 14,2",
        ["ttd"]="M8,3 L16,3 M12,3 L12,6 M6,9 L4,7 M18,9 L20,7 M5,14 A7,7 0 1 1 19,14 A7,7 0 1 1 5,14 M12,10 L12,14 15,12",
        ["crosshair"]="M5,5 L9,5 M5,5 L5,9 M19,5 L15,5 M19,5 L19,9 M5,19 L9,19 M5,19 L5,15 M19,19 L15,19 M19,19 L19,15 M10,12 L14,12 M12,10 L12,14",
        ["adr"]="M13,2 L5,14 11,14 10,22 19,10 13,10 Z",
        ["headshots"]="M11,7 C7,7 5,10 5,14 C5,17 7,19 11,19 L11,22 M11,7 C15,7 16,11 16,13 L19,15 16,16 16,20 11,20 M2,5 L8,5 M2,3 L2,7 M8,3 L11,5 8,7 Z M13,4 L15,2 M14,6 L17,6",
        ["kills"]="M7,21 L7,9 C7,6 10,3 10,3 C10,3 13,6 13,9 L13,21 Z M7,16 L13,16 M17,9 L17,19 M15,14 L19,14",
        ["kills_match"]="M7,21 L7,9 C7,6 10,3 10,3 C10,3 13,6 13,9 L13,21 Z M7,16 L13,16 M17,9 L17,19 M15,14 L19,14",
        ["deaths"]="M5,11 C5,0 19,0 19,11 L19,15 16,15 16,21 8,21 8,15 5,15 Z M8,10 A1,1 0 1 1 8,12 A1,1 0 1 1 8,10 M15,10 A1,1 0 1 1 15,12 A1,1 0 1 1 15,10 M11,21 L11,18 M14,21 L14,18",
        ["assists"]="M8,4 A3,3 0 1 1 8,10 A3,3 0 1 1 8,4 M2,21 L2,17 C2,12 14,12 14,17 L14,21 M17,5 C23,5 23,11 17,11 M18,14 C22,14 23,17 23,21",
        ["positioning"]="M12,22 C12,22 4,13 4,9 A8,8 0 1 1 20,9 C20,13 12,22 12,22 Z M9,9 A3,3 0 1 1 15,9 A3,3 0 1 1 9,9",
        ["trade_success"]="M3,7 L19,7 15,3 M19,7 L15,11 M21,17 L5,17 9,13 M5,17 L9,21",
        ["accuracy_spotted"]="M2,12 C6,4 18,4 22,12 C18,20 6,20 2,12 Z M9,12 A3,3 0 1 1 15,12 A3,3 0 1 1 9,12",
        ["opening"]="M3,3 L8,3 16,20 19,20 M21,3 L16,3 8,20 5,20 M5,17 L11,21 M13,21 L19,17",
        ["clutch"]="M7,3 L17,3 17,9 C17,17 7,17 7,9 Z M7,5 L3,5 3,8 C3,12 7,12 7,12 M17,5 L21,5 21,8 C21,12 17,12 17,12 M12,15 L12,21 M8,21 L16,21",
        ["calendar"]="M3,5 L21,5 21,21 3,21 Z M7,2 L7,8 M17,2 L17,8 M3,11 L21,11 M7,15 L9,15 M15,15 L17,15",
        ["person"]="M12,3 A4,4 0 1 1 12,11 A4,4 0 1 1 12,3 M4,21 C4,11 20,11 20,21",
        ["shield"]="M12,2 L20,6 20,12 C20,18 12,22 12,22 C12,22 4,18 4,12 L4,6 Z",
        ["trust"]="M12,2 L20,6 20,12 C20,18 12,22 12,22 C12,22 4,18 4,12 L4,6 Z M10,9 C10,6 15,7 15,10 C15,12 12,12 12,14 M12,17 L12,17.2",
        ["vac"]="M12,3 L22,21 2,21 Z M12,9 L12,14 M12,17 L12,17.2",
        ["status"]="M4,12 A8,8 0 1 1 20,12 A8,8 0 1 1 4,12 M12,7 L12,12 16,12",
        ["scope"]="M6,12 A6,6 0 1 1 18,12 A6,6 0 1 1 6,12 M12,2 L12,7 M12,17 L12,22 M2,12 L7,12 M17,12 L22,12",
        ["chart"]="M3,3 L3,21 22,21 M7,16 L7,12 M12,16 L12,7 M17,16 L17,10",
        ["refresh"]="M20,8 C18,2 9,2 5,7 C0,13 5,21 12,21 C17,21 20,18 21,14 M15,8 L21,8 21,2"
    };
    static readonly Dictionary<string,Geometry> geometries=paths.ToDictionary(x=>x.Key,x=>{var g=Geometry.Parse(x.Value);g.Freeze();return g;});
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var size=Math.Min(ActualWidth,ActualHeight);if(size<=4)return;
        var key=Kind??"chart";key=key switch{"detail_headshots"=>"headshots","detail_winrate" or "recent_winrate"=>"winrate","recent_kd"=>"kd","recent_kills"=>"kills_match","detail_ttd" or "ttk"=>"ttd","preaim" or "detail_preaim" or "aim_offset" or "detail_aim_offset"=>"crosshair","accuracy" or "detail_accuracy" or "spray_accuracy"=>"accuracy_spotted","teamkills" or "team_damage" or "team_flashes" or "damage_kicks" or "vote_kicks" or "afk"=>"shield","grenades" or "utility_damage" or "he_damage" or "fire_damage" or "unused_utility"=>"utility","flash_assists" or "enemies_flashed"=>"assists","recent_matches"=>"calendar",_=>key};var color=key switch{"faceit_elo" or "faceit_level"=>"#F2A579","leetify_rating"=>"#F84982","winrate" or "kast"=>"#A0C7B3","headshots" or "kills" or "kills_match"=>"#E2BC98","utility"=>"#BFB5E4","ttd"=>"#A0C5CE","steam"=>"#BACAD6",_=>"#B7AFCA"};
        var brush=(Brush)new BrushConverter().ConvertFromString(color)!;
        dc.PushTransform(new TranslateTransform((ActualWidth-size)/2+2,(ActualHeight-size)/2+2));dc.PushTransform(new ScaleTransform((size-4)/24,(size-4)/24));
        if(key is "faceit_elo" or "faceit_level")dc.DrawGeometry(brush,null,Geometry.Parse("M23.999 2.705 a.167.167 0 0 0-.312-.1 a1141.27 1141.27 0 0 0-6.053 9.375 H.218 c-.221 0-.301.282-.11.352 c7.227 2.73 17.667 6.836 23.5 9.134 c.15.06.39-.08.39-.18 z"));
        else if(key=="leetify_rating")BrandMarks.DrawLeetify(dc);
        else if(key=="steam")
        {
            dc.DrawEllipse(null,new Pen(brush,1.5),new Point(16,7),5,5);dc.DrawEllipse(null,new Pen(brush,1.5),new Point(6,17),3,3);
            dc.DrawLine(new Pen(brush,2),new(8,15),new(13,10));dc.DrawLine(new Pen(brush,2),new(3,17),new(0,15));
        }
        else dc.DrawGeometry(null,new Pen(brush,1.6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round},geometries.GetValueOrDefault(key)??geometries["chart"]);
        dc.Pop();dc.Pop();
    }
}
