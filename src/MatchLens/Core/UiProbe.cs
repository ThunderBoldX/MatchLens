using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MatchLens;
// Headless UI diagnostics: no shown windows, no input injection and no live stats requests.
public static class UiProbe
{
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    static object Capture(ImageSource image,string path)
    {
        var window=new Window{Icon=image,ShowActivated=false,ShowInTaskbar=false,WindowStyle=WindowStyle.None,Left=-32000,Top=-32000,Width=1,Height=1};
        try
        {
            var handle=new WindowInteropHelper(window).EnsureHandle();
            var iconHandle=SendMessage(handle,0x7F,new IntPtr(1),IntPtr.Zero); // WM_GETICON / ICON_BIG
            if(iconHandle==IntPtr.Zero)throw new Exception("Native taskbar icon missing");
            using var icon=System.Drawing.Icon.FromHandle(iconHandle);using var bitmap=icon.ToBitmap();bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);
            int left=bitmap.Width,top=bitmap.Height,right=-1,bottom=-1;
            for(int y=0;y<bitmap.Height;y++)for(int x=0;x<bitmap.Width;x++)
            {
                var c=bitmap.GetPixel(x,y);if(c.A>=96&&Math.Max(c.R,Math.Max(c.G,c.B))>=150){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
            }
            return new{canvasWidth=bitmap.Width,canvasHeight=bitmap.Height,visibleWidth=Math.Max(0,right-left+1),visibleHeight=Math.Max(0,bottom-top+1)};
        }
        finally{window.Close();}
    }
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var oldInput=new BitmapImage(new Uri("pack://application:,,,/Assets/MatchLens.ico"));
        var before=Capture(oldInput,Path.Combine(directory,"taskbar-before.png"));
        var after=Capture(BrandMarks.MatchLens,Path.Combine(directory,"taskbar-after.png"));
        using(var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MatchLens.ico"))!.Stream)
        using(var tray=new System.Drawing.Icon(stream,System.Windows.Forms.SystemInformation.SmallIconSize))
        using(var bitmap=tray.ToBitmap())bitmap.Save(Path.Combine(directory,"tray.png"),System.Drawing.Imaging.ImageFormat.Png);
        var main=new MainWindow(true);var content=(FrameworkElement)main.Content;
        void Layout(){content.Measure(new Size(1280,850));content.Arrange(new Rect(0,0,1280,850));content.UpdateLayout();}
        Layout();var initialDisabled=!main.PlayerSelector.IsEnabled&&!main.PlayerSelector.IsDropDownOpen;
        main.SetDemo();Layout();var populatedEnabled=main.PlayerSelector.IsEnabled&&main.PlayerSelector.Items.Count==10;
        var toggle=(ToggleButton)main.PlayerSelector.Template.FindName("DropToggle",main.PlayerSelector);
        var arrow=(FrameworkElement)toggle.Template.FindName("SelectorArrow",toggle);var populatedArrow=arrow.Visibility==Visibility.Visible;
        var list=(System.Collections.ObjectModel.ObservableCollection<Player>)main.PlayerSelector.ItemsSource;
        // This control has never been loaded: WPF defers popup creation, so no popup is shown.
        main.PlayerSelector.IsDropDownOpen=true;list.Clear();Layout();
        var clearedDisabled=!main.PlayerSelector.IsEnabled&&!main.PlayerSelector.IsDropDownOpen;
        var placeholder=(TextBlock)main.PlayerSelector.Template.FindName("Placeholder",main.PlayerSelector);
        var emptyPlaceholder=placeholder.Text==L.T("Очікуємо гравців")&&arrow.Visibility==Visibility.Hidden;
        main.Close();
        File.WriteAllText(Path.Combine(directory,"ui-probe.json"),JsonSerializer.Serialize(new{before,after,icoInputPixels=oldInput.PixelWidth,mainInputPixels=BrandMarks.MatchLens.PixelWidth,initialDisabled,populatedEnabled,populatedArrow,clearedDisabled,emptyPlaceholder},Store.Json));
        if(!(initialDisabled&&populatedEnabled&&populatedArrow&&clearedDisabled&&emptyPlaceholder))throw new Exception("Selector lifecycle failed");
    }
}
