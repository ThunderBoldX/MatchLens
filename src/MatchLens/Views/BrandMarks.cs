using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MatchLens;
public static class BrandMarks
{
    static readonly Lazy<BitmapSource> matchLens=new(()=>
    {
        using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MatchLens-logo.png"))!.Stream;
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    });
    // Use a full-resolution PNG for Window.Icon. BitmapImage(ICO) exposes only one frame.
    public static BitmapSource MatchLens=>matchLens.Value;
    static readonly Lazy<BitmapSource> leetify=new(()=>{var b=new BitmapImage(new Uri("pack://application:,,,/Assets/Leetify-badge.png"));b.Freeze();return b;});
    public static void DrawLeetify(DrawingContext dc)
    {
        // Display the original brand mark from the unmodified official badge.
        // The complete attribution badge also appears in the source card.
        dc.PushClip(new RectangleGeometry(new Rect(0,0,24,24)));
        dc.DrawImage(leetify.Value,new Rect(-12,-17.6,108,46));dc.Pop();
    }
}
