namespace MatchLens;
public record ComparisonItem(string Key,string Label,double Player,double Yours,double Scale,string Unit)
{
    public string Summary=>L.F($"{Player:0.##}{L.T(Unit)} / ви {Yours:0.##}{L.T(Unit)}");
}
public record ComparisonResult(bool Available,double Index,string Label,string Detail,List<ComparisonItem> Metrics);
public static class PlayerComparison
{
    static readonly (string Key,double Scale,double Weight,bool Inverse)[] dimensions=
    [("leetify_rating",8,2,false),("kd",1,1.5,false),("kast",35,1,false),("aim",60,1,false),("utility",60,.6,false),("ttd",600,.8,true),("trade_success",60,.6,false),("crosshair",20,.6,true)];
    public static ComparisonResult Read(Player player,Player? self)
    {
        ComparisonResult Empty(string detail)=>new(false,0,L.T("Недостатньо даних"),detail,[]);
        if(self==null)return Empty(L.T("Очікуємо ваш Steam ID від GSI"));
        if(player.Id==self.Id)return new(false,0,L.T("Це ваш профіль"),L.T("Ваша статистика — основа порівняння"),[]);
        var p=player.Sources.FirstOrDefault(s=>s.Name=="LEETIFY");var me=self.Sources.FirstOrDefault(s=>s.Name=="LEETIFY");
        if(p==null||me==null||string.IsNullOrEmpty(p.Scope)||p.Scope!=me.Scope)return Empty(L.T("Потрібна статистика одного джерела за однаковий період"));
        if(p.FetchedAt==null||me.FetchedAt==null||DateTimeOffset.Now-p.FetchedAt>TimeSpan.FromDays(1)||DateTimeOffset.Now-me.FetchedAt>TimeSpan.FromDays(1))return Empty(L.T("Потрібні свіжі відкриті показники"));
        var rows=new List<ComparisonItem>();double sum=0,weights=0;
        foreach(var dimension in dimensions)
        {
            var a=p.Numbers.FirstOrDefault(n=>n.Key==dimension.Key);var b=me.Numbers.FirstOrDefault(n=>n.Key==dimension.Key);if(a==null||b==null||a.Unit!=b.Unit||!double.IsFinite(a.Value)||!double.IsFinite(b.Value))continue;
            rows.Add(new(dimension.Key,a.Label,a.Value,b.Value,dimension.Scale,a.Unit.Replace(" / 100","")));
            var difference=(a.Value-b.Value)/dimension.Scale*(dimension.Inverse?-1:1);
            sum+=Math.Clamp(difference,-1,1)*dimension.Weight;weights+=dimension.Weight;
        }
        if(rows.Count<3)return Empty(L.F($"Спільних показників: {rows.Count}. Для порівняння потрібно хоча б 3"));
        var index=Math.Round(sum/weights*100,1);
        return new(true,index,index>8?L.T("Може бути складнішим"):index< -8?L.T("Нижчі показники"):L.T("Близькі показники"),
            L.F($"{rows.Count} спільних метрик · {L.T(p.Scope)}. Індекс −100…+100, умовні ваги; це не шанс перемоги. Розмір фактичної вибірки може бути невідомий."),rows);
    }
}
