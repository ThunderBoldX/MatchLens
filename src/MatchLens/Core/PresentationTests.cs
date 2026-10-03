using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MatchLens;
public static class PresentationTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var primary=new DisplayTarget("primary",true,new(0,0,1920,1040));
        var left=new DisplayTarget("left",false,new(-2560,-220,2560,1440));
        var right=new DisplayTarget("right",false,new(1920,0,1920,1080));
        check(PresentationDisplay.Choose([primary])==null,"Presentation never chooses sole primary monitor");
        check(PresentationDisplay.Choose([primary,left])==left,"Presentation uses physical negative-origin secondary monitor bounds");
        check(PresentationDisplay.Choose([primary,left,right],"right")==right,"Presentation retains selected secondary across display changes");
        check(PresentationDisplay.Choose([primary,new("gone",false,new(0,0,0,0))])==null,"Disconnected displays cannot cover the game");
        check(PresentationDisplay.WheelOffset(0,2000,900,-120,3)==72,"Inactive wheel scroll moves downward");
        check(PresentationDisplay.WheelOffset(0,2000,900,-30,3)==18,"High-resolution wheel fractional delta is preserved");
        check(PresentationDisplay.WheelOffset(1000,2000,900,-120,3)==1072&&PresentationDisplay.WheelOffset(1090,2000,900,-120,3)==1100,"Wheel offset clamps to scroll extent");
        check(PresentationDisplay.WheelOffset(100,2000,900,120,-1)==0&&PresentationDisplay.WheelOffset(100,2000,900,-120,-1)==1000,"Windows page-scrolling setting is respected");
        check(PresentationDisplay.WheelOffset(100,2000,900,-120,0)==100,"Windows zero scroll-lines setting is respected");
        check(!PresentationDisplay.OwnsWheel(0,0x201,true,true,true)&&!PresentationDisplay.OwnsWheel(-1,0x20A,true,true,true),"Presentation does not consume game clicks or chained hook events");
        check(!PresentationDisplay.OwnsWheel(0,0x20A,true,false,true)&&!PresentationDisplay.OwnsWheel(0,0x20A,true,true,false)&&!PresentationDisplay.OwnsWheel(0,0x20A,false,true,true),"Wheel outside, covered or hidden presentation remains untouched");
        var window=new PresentationWindow(true);var state=new MatchState{Active=true,Map="de_mirage"};
        var p=new Player{Id="76561198000000001",Name="Player",IsLocal=true,Sources=[new(){Name="CSSTATS",Numbers=[new("kd","K/D",1.2)]}]};
        window.Refresh(state,[p]);var card=window.PlayerGrid.Children[0];window.Refresh(state,[p]);
        check(ReferenceEquals(card,window.PlayerGrid.Children[0]),"Unchanged stats retain card and scroll layout");
        p.Name="Updated";window.Refresh(state,[p]);check(!ReferenceEquals(card,window.PlayerGrid.Children[0]),"Changed player data refreshes presentation without selection");
        var content=(FrameworkElement)window.Content;content.Measure(new Size(1280,700));content.Arrange(new Rect(0,0,1280,700));content.UpdateLayout();
        var many=Enumerable.Range(1,10).Select(i=>new Player{Id=(76561198000000000L+i).ToString(),Name="Player "+i}).ToList();
        window.Refresh(state,many);content.UpdateLayout();window.StatisticsScroll.ScrollToVerticalOffset(250);content.UpdateLayout();
        var offset=window.StatisticsScroll.VerticalOffset;many[2].Name="New data";window.Refresh(state,many);content.UpdateLayout();
        check(offset>0&&Math.Abs(window.StatisticsScroll.VerticalOffset-offset)<1,"Partial statistics updates preserve scrolled position");
        window.Refresh(new MatchState(),many);check(window.PlayerGrid.Children.Count==0,"Match end clears previous presentation roster");
        var before=PresentationInput.Foreground();var handle=new WindowInteropHelper(window).EnsureHandle();
        var styles=PresentationInput.Styles(handle);
        check((styles&PresentationInput.NoActivate)!=0&&(styles&PresentationInput.ToolWindow)!=0&&(styles&PresentationInput.AppWindow)==0,"Native presentation styles disable activation and taskbar entry");
        check(PresentationInput.MouseActivation(handle)==new IntPtr(4),"Native WM_MOUSEACTIVATE rejects activation and eats click");
        check(!window.ShowActivated&&!window.ShowInTaskbar&&!window.Focusable&&window.Owner==null,"Presentation is independent of minimized main window and cannot request focus");
        // Install on an invisible test window: never inject mouse events or activate user apps.
        using(var input=new PresentationInput(window,_=>{},_=>{}))
        {
            var placed=input.Place(left);var bounds=PresentationInput.Bounds(handle);
            check(placed&&bounds==left.Area,$"Native placement uses exact physical monitor bounds without DPI conversion drift ({bounds}, expected {left.Area}, result {placed})");
            input.Start();check(await input.Ready.WaitAsync(TimeSpan.FromSeconds(3)),"Dedicated inactive-wheel hook installs successfully");
            input.Dispose();check(!input.Installed,"Closing presentation releases its native mouse hook");
        }
        window.Close();check(PresentationInput.Foreground()==before,"Hidden native activation probe leaves user's foreground app unchanged");
    }
}
