using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MatchLens;

public static class LocalizationTests
{
    static bool Ukrainian(string text)=>text.Any(c=>c>='\u0400'&&c<='\u04ff');
    static IEnumerable<TextBlock> Texts(DependencyObject parent)
    {
        if(parent is TextBlock text)yield return text;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in Texts(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
    static void Layout(FrameworkElement content,double width=1440,double height=1030)
    {content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();}
    public static void Run(Action<bool,string> check)
    {
        var language=L.Language;var root=Store.Root;var previousOverride=L.OverrideLanguage;
        Store.Root=Path.Combine(root,"localization-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Store.Root);L.OverrideLanguage=null;
        try
        {
            check(new Settings().Language=="en","New settings default to English");
            L.Set("en");
            check(L.T("Огляд матчу")=="Match overview"&&L.T("Налаштування")=="Settings","English catalog translates navigation and settings");
            check(L.T("CSTRACKER · останні 34 матчів")=="CSTRACKER · last 34 matches","Rendered source scopes translate without changing stored source data");
            check(L.F($"{9} з {10} профілів доступні для порівняння")=="9 of 10 profiles can be compared","English interpolation preserves numeric values");
            var name="Перемоги ❤️";var player=new Player{Id=(SteamIds.Base+8765).ToString(),Name=name,IsLocal=true};
            var message=TelegramMessage.Format(new MatchState{Active=true,Map="de_mirage",Mode="competitive"},[player]);
            check(message.Contains(name)&&message.Contains("Players")&&!message.Contains("Гравців"),"English Telegram preserves player identities while translating labels");
            check(TelegramMessage.Format(new MatchState(),[])=="You are not in a match ❤️⚡","English idle Telegram message is localized");
            var source=new SourceCard{Name="CSTRACKER",Scope="CSTRACKER · усі відстежені матчі",ReviewRules=[new("kd",1.8,false,"CSTRACKER · усі відстежені матчі")]};
            player.Sources=[source];var number=new NumericMetric("kd","K/D",3,"",source.Scope);
            check(!Ukrainian(MetricAppearance.Tile("kd","K/D","3",source.Scope,number,player,null,"CSTRACKER").Tooltip),"English review tooltip translates the threshold and source scope");
            var window=new MainWindow(true);window.SetDemo();window.OpenPreviewProfile(false);Layout((FrameworkElement)window.Content);
            var untranslated=Texts((DependencyObject)window.Content).Where(t=>Ukrainian(t.Text)).Select(t=>t.Text).Distinct().ToList();
            File.WriteAllText(Path.Combine(Store.Root,"untranslated-profile.txt"),string.Join("\n",untranslated));
            check(untranslated.Count==0,"Rendered English profile has no leftover Ukrainian labels");
            var popup=window.PopupPreview();Layout(popup,360,440);
            check(Texts(popup).All(t=>!Ukrainian(t.Text)),"English roster dropdown uses localized identity labels");
            var display=window.PresentationPreview();Layout((FrameworkElement)display.Content,1920,1080);
            var missing=Texts((DependencyObject)display.Content).Where(t=>Ukrainian(t.Text)).Select(t=>t.Text).Distinct().ToList();
            File.WriteAllText(Path.Combine(Store.Root,"untranslated-display.txt"),string.Join("\n",missing));
            check(missing.Count==0,"English second-monitor view translates cards and summaries");
            L.Set("uk");
            check((string)Application.Current.Resources["Loc.0"]! != ""&&L.T("Огляд матчу")=="Огляд матчу","Ukrainian selection restores the original text");
            check(TelegramMessage.Format(new MatchState(),[])=="Ви не в грі❤️⚡","Ukrainian idle Telegram message preserves the requested text");
            display.Refresh(new MatchState{Active=true,Map="de_mirage",Mode="competitive"},[player]);
            check(display.MatchMeta.Text.Contains("Змагальний"),"Existing presentation window refreshes in the selected language");
            using var telegram=new Telegram();
            var initial=new Settings{Language="en",GsiToken="keep-gsi",Cs2Path="keep-path",SteamKeyCipher="keep-steam",FaceitKeyCipher="keep-faceit",LeetifyKeyCipher="keep-leetify"};
            var settings=new SettingsWindow(initial,telegram);
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var picker=(ComboBox)typeof(SettingsWindow).GetField("language",flags)!.GetValue(settings)!;picker.SelectedIndex=1;
            var collected=(Settings)typeof(SettingsWindow).GetMethod("Collect",flags)!.Invoke(settings,null)!;
            check(collected.Language=="uk"&&collected.GsiToken==initial.GsiToken&&collected.Cs2Path==initial.Cs2Path&&collected.SteamKeyCipher==initial.SteamKeyCipher&&collected.FaceitKeyCipher==initial.FaceitKeyCipher&&collected.LeetifyKeyCipher==initial.LeetifyKeyCipher,"Language settings preserve the GSI connection and existing encrypted fields");
            Store.Write("settings.json",collected);check(Store.Read<Settings>("settings.json").Language=="uk","Selected language survives saving and reloading");
            File.WriteAllText(Path.Combine(Store.Root,"settings.json"),"{\"Cs2Path\":\"legacy\"}");check(Store.Read<Settings>("settings.json").Language=="en","Settings from older releases migrate to English without losing their CS2 path");
            check(initial.Language=="en","Changing the language picker does not mutate unsaved settings");
            settings.Close();display.Close();window.Close();
        }
        finally{Store.Root=root;L.OverrideLanguage=previousOverride;L.Set(language);}
    }
}
