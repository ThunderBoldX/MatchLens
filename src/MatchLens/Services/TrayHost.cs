using System.Windows.Forms;

namespace MatchLens;
public sealed class TrayHost : IDisposable
{
    readonly NotifyIcon icon;
    readonly System.Drawing.Icon appIcon;
    readonly ToolStripMenuItem presentation;
    public TrayHost(Action restore,Action exit,Action showPresentation)
    {
        var menu=new ContextMenuStrip();
        presentation=new ToolStripMenuItem(L.T("Другий монітор"),null,(_,_)=>showPresentation());
        menu.Items.Add(presentation);
        menu.Items.Add(L.T("Відкрити MatchLens"),null,(_,_)=>restore());
        menu.Items.Add(L.T("Вийти"),null,(_,_)=>exit());
        using(var resource=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MatchLens.ico"))!.Stream)
        using(var loaded=new System.Drawing.Icon(resource,SystemInformation.SmallIconSize))appIcon=(System.Drawing.Icon)loaded.Clone();
        icon=new NotifyIcon{Text="MatchLens · CS2",Icon=appIcon,ContextMenuStrip=menu,Visible=true};
        icon.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)restore();};
    }
    public void Dispose(){icon.Visible=false;icon.ContextMenuStrip?.Dispose();icon.Dispose();appIcon.Dispose();}
    public void PresentationState(bool showing)=>presentation.Checked=showing;
    public void RefreshLanguage(){var menu=icon.ContextMenuStrip!;menu.Items[0].Text=L.T("Другий монітор");menu.Items[1].Text=L.T("Відкрити MatchLens");menu.Items[2].Text=L.T("Вийти");}
}
