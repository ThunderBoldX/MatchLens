using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace MatchLens;
// Offline layout preview; no Telegram request or visible window.
public static class TelegramPreviewRenderer
{
    public static void Save(string html,string path)
    {
        var text=new TextBlock{FontFamily=new("Segoe UI"),FontSize=15,LineHeight=23,Foreground=new SolidColorBrush(Color.FromRgb(234,232,240)),TextWrapping=TextWrapping.Wrap};
        void Add(XNode node,bool bold=false,bool mono=false,bool muted=false)
        {
            if(node is XText value)text.Inlines.Add(new Run(value.Value){FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,
                FontFamily=new(mono?"Consolas":"Segoe UI"),FontSize=mono?14:muted?12:15,
                Foreground=new SolidColorBrush(muted?Color.FromRgb(152,150,168):Color.FromRgb(234,232,240))});
            else if(node is XElement element)foreach(var child in element.Nodes())Add(child,bold||element.Name=="b",mono||element.Name=="pre",muted||element.Name=="i");
        }
        foreach(var node in XElement.Parse("<root>"+html+"</root>",LoadOptions.PreserveWhitespace).Nodes())Add(node);
        var bubble=new Border{Width=420,Padding=new(24),CornerRadius=new(24),Background=new SolidColorBrush(Color.FromRgb(31,31,39)),Child=text};
        bubble.Measure(new Size(420,double.PositiveInfinity));var height=Math.Ceiling(bubble.DesiredSize.Height);
        bubble.Arrange(new Rect(0,0,420,height));bubble.UpdateLayout();
        var bitmap=new RenderTargetBitmap(420,(int)height,96,96,PixelFormats.Pbgra32);bitmap.Render(bubble);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);
    }
}
