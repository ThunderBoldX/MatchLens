using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;

namespace MatchLens;
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>new CornerRadius(value is double height?Math.Max(0,height/2):0);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
public sealed class RoundedPanel : Border
{
    public RoundedPanel(){SizeChanged+=(_,_)=>Round();}
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e){base.OnPropertyChanged(e);if(e.Property==CornerRadiusProperty)Round();}
    void Round(){var r=CornerRadius.TopLeft;Clip=new RectangleGeometry(new Rect(0,0,Math.Max(0,ActualWidth),Math.Max(0,ActualHeight)),r,r);}
}
public class StatTile
{
    public string Key {get;set;}="chart";
    public string Label {get;set;}="";
    public string Value {get;set;}="-";
    public string Hint {get;set;}="";
    public MetricTone Tone {get;set;}
    public Brush ValueBrush=>MetricPalette.Brush(Tone);
    public string Tooltip {get;set;}="";
}
public record BarDatum(string Label,double Value,double Maximum,string Display,double? Yours=null,string? YourDisplay=null,MetricTone Tone=MetricTone.Neutral);
public sealed class StatChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty=DependencyProperty.Register(nameof(Data),typeof(List<BarDatum>),typeof(StatChart),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public List<BarDatum>? Data {get=>(List<BarDatum>?)GetValue(DataProperty);set=>SetValue(DataProperty,value);}
    public static readonly DependencyProperty RingValueProperty=DependencyProperty.Register(nameof(RingValue),typeof(double?),typeof(StatChart),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public double? RingValue {get=>(double?)GetValue(RingValueProperty);set=>SetValue(RingValueProperty,value);}
    public string EmptyText {get;set;}=L.T("Показники ще недоступні");
    public bool Ring {get;set;}
    public MetricTone RingTone {get;set;}
    static readonly Brush muted=new SolidColorBrush(Color.FromRgb(137,137,148));
    static readonly Brush ink=new SolidColorBrush(Color.FromRgb(236,235,241));
    static readonly Brush accent=new SolidColorBrush(Color.FromRgb(166,61,255));
    static readonly Brush track=new SolidColorBrush(Color.FromArgb(22,255,255,255));
    void Text(DrawingContext dc,string value,double x,double y,double size,Brush? brush=null,double max=1000)
    {
        var t=new FormattedText(L.T(value),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush??ink,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(1,max),Trimming=TextTrimming.CharacterEllipsis};dc.DrawText(t,new(x,y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var w=ActualWidth;var h=ActualHeight;if(w<20||h<20)return;
        if(Ring)
        {
            var radius=Math.Min(w*.3,h*.37);var center=new Point(w/2,h*.43);var pen=new Pen(track,7);
            dc.DrawEllipse(null,pen,center,radius,radius);
            if(RingValue is {} value&&value>=0&&value<=100)
            {
                if(value>=99.999)dc.DrawEllipse(null,new Pen(accent,7),center,radius,radius);
                else if(value>0)
                {
                    var angle=value/100*Math.PI*2;var end=new Point(center.X+radius*Math.Sin(angle),center.Y-radius*Math.Cos(angle));
                    var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(new(center.X,center.Y-radius),false,false);c.ArcTo(end,new(radius,radius),0,value>50,SweepDirection.Clockwise,true,false);}g.Freeze();
                    dc.DrawGeometry(null,new Pen(accent,7){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},g);
                }
                var label=value.ToString("0.#",CultureInfo.InvariantCulture)+"%";var f=new FormattedText(label,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),32,MetricPalette.Brush(RingTone),VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(f,new(center.X-f.Width/2,center.Y-f.Height/2));Text(dc,L.T("частка перемог"),w/2-52,center.Y+radius+22,11,muted,130);
            }
            else{Text(dc,"—",center.X-14,center.Y-21,32);Text(dc,EmptyText,12,h-27,11,muted,w-24);}
            return;
        }
        var rows=Data?.Where(d=>double.IsFinite(d.Value)&&d.Maximum>0).Take(6).ToList()??[];
        if(rows.Count==0){Text(dc,EmptyText,0,Math.Max(10,h/2-10),12,muted,w);return;}
        var gap=Math.Min(53,h/rows.Count);
        for(int i=0;i<rows.Count;i++)
        {
            var d=rows[i];var y=i*gap;var right=d.Yours.HasValue?d.Display+" / "+d.YourDisplay:d.Display;
            Text(dc,d.Label,0,y,11,muted,w*.51);Text(dc,right,w*.52,y,11,MetricPalette.Brush(d.Tone),w*.48);
            var barY=y+26;dc.DrawRoundedRectangle(track,null,new Rect(0,barY,w,3),1.5,1.5);
            var length=Math.Clamp(d.Value/d.Maximum,0,1)*w;if(length>0)dc.DrawRoundedRectangle(accent,null,new Rect(0,barY,length,3),1.5,1.5);
            if(d.Yours is {} yours)
            {
                var position=Math.Clamp(yours/d.Maximum,0,1)*w;dc.DrawEllipse(ink,null,new(Math.Clamp(position,3,w-3),barY+1.5),3,3);
            }
        }
    }
}
public sealed class LensMark : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var s=Math.Min(ActualWidth,ActualHeight);if(s<=0)return;
        var rect=new Rect((ActualWidth-s)/2,(ActualHeight-s)/2,s,s);
        dc.DrawImage(BrandMarks.MatchLens,rect);
    }
}
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty=DependencyProperty.Register(nameof(Points),typeof(List<MatchPoint>),typeof(TrendChart),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public List<MatchPoint>? Points {get=>(List<MatchPoint>?)GetValue(PointsProperty);set=>SetValue(PointsProperty,value);}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var w=ActualWidth;var h=ActualHeight;if(w<50||h<50)return;
        var rows=Points?.Where(p=>double.IsFinite(p.Value)).TakeLast(30).ToList()??[];
        var ink=new SolidColorBrush(Color.FromRgb(150,143,164));var bright=new SolidColorBrush(Color.FromRgb(166,61,255));
        void T(string value,double x,double y,double size=10)=>dc.DrawText(new FormattedText(L.T(value),CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,ink,VisualTreeHelper.GetDpi(this).PixelsPerDip){MaxTextWidth=Math.Max(1,w-x),Trimming=TextTrimming.CharacterEllipsis},new(x,y));
        if(rows.Count<2){T(L.T("Для динаміки потрібна відкрита історія хоча б двох матчів."),5,h/2-8,12);return;}
        var low=Math.Floor(rows.Min(p=>p.Value))-1;var high=Math.Ceiling(rows.Max(p=>p.Value))+1;
        var left=42d;var top=10d;var bottom=h-28;
        for(int i=0;i<3;i++){var y=top+(bottom-top)*i/2;dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(18,255,255,255)),1),new(left,y),new(w,y));T((high-(high-low)*i/2).ToString("0.#"),0,y-6);}
        var positions=rows.Select((p,i)=>new Point(left+(w-left-5)*i/(rows.Count-1),bottom-(p.Value-low)/(high-low)*(bottom-top))).ToArray();
        var area=new StreamGeometry();using(var a=area.Open()){a.BeginFigure(new(positions[0].X,bottom),true,true);a.PolyLineTo(positions,true,false);a.LineTo(new(positions[^1].X,bottom),true,false);}area.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(45,166,61,255),Color.FromArgb(0,166,61,255),90),null,area);
        var line=new StreamGeometry();using(var l=line.Open()){l.BeginFigure(positions[0],false,false);l.PolyLineTo(positions.Skip(1).ToArray(),true,false);}line.Freeze();
        dc.DrawGeometry(null,new Pen(bright,2){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round},line);foreach(var p in positions)dc.DrawEllipse(bright,null,p,2.2,2.2);
        T(rows[0].Label,left,h-18);T(rows[^1].Label,Math.Max(left,w-125),h-18);
    }
}
