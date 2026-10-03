using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MatchLens;
public static class WindowAppearance
{
    [StructLayout(LayoutKind.Sequential)] struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MinMax {public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo {public int Size;public Rect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    public static Size InitialSize(double width,double height)=>new(Math.Min(1280,width*.92),Math.Min(850,height*.91));
    public static void FitInitial(Window window)
    {
        var area=SystemParameters.WorkArea;var fit=InitialSize(area.Width,area.Height);
        window.MinWidth=Math.Min(860,fit.Width);window.MinHeight=Math.Min(560,fit.Height);
        window.Width=fit.Width;window.Height=fit.Height;
    }
    static IntPtr Hook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message!=0x24)return IntPtr.Zero; // WM_GETMINMAXINFO
        var monitor=MonitorFromWindow(hwnd,2);var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(monitor==IntPtr.Zero||!GetMonitorInfo(monitor,ref info))return IntPtr.Zero;
        var m=Marshal.PtrToStructure<MinMax>(lParam);var width=info.Work.Right-info.Work.Left;var height=info.Work.Bottom-info.Work.Top;
        m.MaxPosition=new(){X=info.Work.Left-info.Monitor.Left,Y=info.Work.Top-info.Monitor.Top};m.MaxSize=new(){X=width,Y=height};
        m.MinTrackSize.X=Math.Min(m.MinTrackSize.X,width);m.MinTrackSize.Y=Math.Min(m.MinTrackSize.Y,height);
        Marshal.StructureToPtr(m,lParam,false);handled=true;return IntPtr.Zero;
    }
    public static void Apply(Window window)
    {
        // Native rounded corners keep hardware rendering, resizing and snapping.
        // Older Windows versions simply ignore unsupported DWM attributes.
        try
        {
            var handle=new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
            var rounded=2;DwmSetWindowAttribute(handle,33,ref rounded,sizeof(int));
            var dark=1;DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));
        }
        catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
    }
}
