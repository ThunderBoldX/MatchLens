using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MatchLens;

// Windows coordinates stay in physical pixels, including negative monitor origins.
public record DisplayTarget(string Name,bool Primary,System.Drawing.Rectangle Area);
public static class PresentationDisplay
{
    public static DisplayTarget? Choose(IEnumerable<DisplayTarget> screens,string? preferred=null)=>
        screens.Where(s=>!s.Primary&&s.Area.Width>0&&s.Area.Height>0).OrderByDescending(s=>s.Name==preferred).ThenBy(s=>s.Name,StringComparer.Ordinal).FirstOrDefault();
    public static DisplayTarget? Current(string? preferred=null)=>Choose(System.Windows.Forms.Screen.AllScreens.Select(s=>new DisplayTarget(s.DeviceName,s.Primary,s.WorkingArea)),preferred);
    public static double WheelOffset(double offset,double extent,double viewport,int delta,int lines)
    {
        if(lines==0||delta==0)return Math.Clamp(offset,0,Math.Max(0,extent-viewport));
        var distance=lines<0?viewport:Math.Min(lines,100)*24d;
        return Math.Clamp(offset-delta/120d*distance,0,Math.Max(0,extent-viewport));
    }
    public static bool OwnsWheel(int code,int message,bool visible,bool inside,bool topWindow)=>code==0&&message==0x20A&&visible&&inside&&topWindow;
}

public sealed class PresentationInput : IDisposable
{
    public const long NoActivate=0x08000000,ToolWindow=0x80,AppWindow=0x40000;
    readonly Window window;
    readonly Action<int> scroll;
    readonly Action<bool> hookStatus;
    readonly HwndSource source;
    readonly HookProc callback;
    readonly IntPtr hwnd;
    Thread? thread;
    volatile bool stopping,installed;
    uint threadId;
    IntPtr hook;
    DisplayTarget? target;
    readonly TaskCompletionSource<bool> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> Ready=>ready.Task;
    public bool Installed=>installed;
    public PresentationInput(Window window,Action<int> scroll,Action<bool> hookStatus)
    {
        this.window=window;this.scroll=scroll;this.hookStatus=hookStatus;
        hwnd=new WindowInteropHelper(window).Handle;source=HwndSource.FromHwnd(hwnd)!;
        SetWindowLongPtr(hwnd,-20,new IntPtr((GetWindowLongPtr(hwnd,-20).ToInt64()|NoActivate|ToolWindow)&~AppWindow));
        SetWindowPos(hwnd,IntPtr.Zero,0,0,0,0,0x37); // NOACTIVATE / FRAMECHANGED / NOMOVE / NOSIZE / NOZORDER
        source.AddHook(Messages);callback=Mouse;
    }
    public bool Place(DisplayTarget display)
    {
        target=display;
        return SetWindowPos(hwnd,new IntPtr(-1),display.Area.X,display.Area.Y,display.Area.Width,display.Area.Height,0x10);
    }
    public void Start()
    {
        if(thread!=null||stopping)return;
        thread=new Thread(Loop){IsBackground=true,Name="MatchLens presentation wheel"};thread.Start();
    }
    void Loop()
    {
        // Dedicated message loop: a slow chart render cannot time out the mouse hook.
        PeekMessage(out _,IntPtr.Zero,0,0,0);threadId=GetCurrentThreadId();
        try
        {
            if(stopping)return;
            hook=SetWindowsHookEx(14,callback,GetModuleHandle(null),0);installed=hook!=IntPtr.Zero;
            ready.TrySetResult(installed);
            Post(()=>hookStatus(installed));
            if(!installed)return;
            while(!stopping&&GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}
        }
        finally{if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);hook=IntPtr.Zero;installed=false;ready.TrySetResult(false);}
    }
    void Post(Action action)
    {
        if(stopping||window.Dispatcher.HasShutdownStarted)return;
        try{window.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>{if(!stopping)action();}));}catch(InvalidOperationException){}
    }
    IntPtr Mouse(int code,IntPtr message,IntPtr data)
    {
        if(code==0&&message.ToInt64()==0x20A&&!stopping&&IsWindowVisible(hwnd))
        {
            var input=Marshal.PtrToStructure<MouseData>(data);
            if(GetWindowRect(hwnd,out var r)&&PresentationDisplay.OwnsWheel(code,(int)message,true,
                input.Point.X>=r.Left&&input.Point.X<r.Right&&input.Point.Y>=r.Top&&input.Point.Y<r.Bottom,
                GetAncestor(WindowFromPoint(input.Point),2)==hwnd))
            {
                var delta=(short)(input.Data>>16);Post(()=>scroll(delta));return new IntPtr(1);
            }
        }
        return CallNextHookEx(IntPtr.Zero,code,message,data);
    }
    IntPtr Messages(IntPtr h,int message,IntPtr w,IntPtr l,ref bool handled)
    {
        if(message==0x24&&target is {} display)
        {
            // The initial HWND may still be associated with the smaller primary display.
            var info=Marshal.PtrToStructure<MinMaxInfo>(l);
            info.MaxTrackSize=new Point{X=display.Area.Width,Y=display.Area.Height};
            info.MaxSize=info.MaxTrackSize;info.MinTrackSize=new Point{X=1,Y=1};
            Marshal.StructureToPtr(info,l,false);handled=true;return IntPtr.Zero;
        }
        if(message==0x21){handled=true;return new IntPtr(4);} // MA_NOACTIVATEANDEAT (also hover activation)
        if(message==0x20A){handled=true;if(!installed)scroll((short)((ulong)w.ToInt64()>>16));}
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if(stopping)return;stopping=true;source.RemoveHook(Messages);
        var id=threadId;if(id!=0)PostThreadMessage(id,0x12,IntPtr.Zero,IntPtr.Zero);
        thread?.Join(1000);
        ready.TrySetResult(false);
    }
    public static long Styles(IntPtr hwnd)=>GetWindowLongPtr(hwnd,-20).ToInt64();
    public static System.Drawing.Rectangle Bounds(IntPtr hwnd){GetWindowRect(hwnd,out var r);return new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);}
    public static IntPtr Foreground()=>GetForegroundWindow();
    public static IntPtr MouseActivation(IntPtr hwnd)=>SendMessage(hwnd,0x21,IntPtr.Zero,IntPtr.Zero);
    delegate IntPtr HookProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo{public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
    [StructLayout(LayoutKind.Sequential)] struct Rectangle{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] struct MouseData{public Point Point;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)] struct Message{public IntPtr Hwnd;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public Point Point;public uint Private;}
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rectangle rectangle);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll",EntryPoint="SetWindowsHookExW",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int type,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW")] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",EntryPoint="PeekMessageW")] static extern bool PeekMessage(out Message message,IntPtr h,uint min,uint max,uint remove);
    [DllImport("user32.dll",EntryPoint="GetMessageW")] static extern int GetMessage(out Message message,IntPtr h,uint min,uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="DispatchMessageW")] static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll",EntryPoint="PostThreadMessageW")] static extern bool PostThreadMessage(uint thread,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr SendMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
}
