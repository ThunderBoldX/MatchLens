using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace MatchLens;
public class SettingsWindow : Window
{
    readonly TextBox path=new(),chat=new(),recover=new();
    readonly PasswordBox bot=new();
    readonly ComboBox language=new(){ItemsSource=new[]{"English","Українська"},Width=240,Height=46,HorizontalAlignment=HorizontalAlignment.Left};
    readonly Settings original;
    readonly CheckBox enabled=new(){Content=L.T("Оновлювати одне повідомлення в Telegram")};
    readonly TextBlock status=new(){Foreground=Brushes.LightGray,Margin=new(0,16,0,10)};
    readonly Telegram telegram;
    readonly string gsiToken;
    public Settings Value {get;private set;}
    public bool Saved {get;private set;}
    static TextBlock Text(string s,double size=12,string color="#96909F")=>new(){Text=s,FontSize=size,Foreground=(Brush)new BrushConverter().ConvertFromString(color)!,Margin=new(0,7,0,11),TextWrapping=TextWrapping.Wrap};
    public SettingsWindow(Settings settings,Telegram telegram)
    {
        Style=(Style)Application.Current.FindResource(typeof(Window));Value=settings;original=settings;this.telegram=telegram;gsiToken=settings.GsiToken;
        language.Style=(Style)Application.Current.FindResource("LanguagePicker");language.SelectedIndex=L.Normalize(settings.Language)=="uk"?1:0;
        Title=L.T("MatchLens · Налаштування");Width=740;Height=Math.Min(820,SystemParameters.WorkArea.Height*.91);MinWidth=560;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SourceInitialized+=(_,_)=>WindowAppearance.Apply(this);
        path.Text=settings.Cs2Path;chat.Text=settings.ChatId;bot.Password=settings.BotToken;enabled.IsChecked=settings.TelegramEnabled;
        var root=new DockPanel{Margin=new(28)};Content=root;
        var footer=new StackPanel{Margin=new(0,15,0,0)};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);footer.Children.Add(status);
        var save=new Button{Content=L.T("Зберегти"),BorderBrush=(Brush)new BrushConverter().ConvertFromString("#A63DFF")!};save.Click+=(_,_)=>Save();footer.Children.Add(save);
        var content=new StackPanel();root.Children.Add(new ScrollViewer{Content=content});
        content.Children.Add(Text(L.T("Налаштуйте MatchLens"),26,"#EEEAF4"));
        content.Children.Add(Text(L.T("Підключіть CS2 один раз. Профілі та відкриту статистику програма завантажуватиме сама.")));
        content.Children.Add(Text("Language / Мова",13,"#C3A3DC"));content.Children.Add(language);
        content.Children.Add(Text(L.T("Мова зміниться після збереження налаштувань."),11));
        content.Children.Add(Text(L.T("01 / ПІДКЛЮЧЕННЯ ДО CS2"),11,"#C3A3DC"));
        content.Children.Add(Text(L.T("Папка Counter-Strike Global Offensive")));content.Children.Add(path);
        var buttons=new WrapPanel{Margin=new(0,10,0,4)};content.Children.Add(buttons);
        void Action(string label,Action action){var b=new Button{Content=label,Margin=new(0,0,8,8)};b.Click+=(_,_)=>{try{action();}catch(Exception ex){status.Text=L.T(ex.Message);}};buttons.Children.Add(b);}
        Action(L.T("Обрати папку"),()=>{var d=new OpenFolderDialog{Title=L.T("Оберіть Counter-Strike Global Offensive")};if(d.ShowDialog(this)==true)path.Text=d.FolderName;});
        Action(L.T("Знайти автоматично"),()=>path.Text=Installation.FindCs2());
        Action(L.T("Встановити GSI"),()=>{var s=Collect();Installation.Install(s);Store.Write("settings.json",s);Value=s;Saved=true;status.Text=L.T("GSI встановлено. Перезапустіть CS2. Змінено лише gamestate_integration_matchlens.cfg.");});
        content.Children.Add(Text(L.T("GSI передає стан матчу, ваш профіль і поточні показники. Додаткові профілі знаходяться через останню групу Steam; вона може містити людей з попереднього сервера. Tab і знімки екрана не використовуються."),11));
        content.Children.Add(Text(L.T("02 / АВТОМАТИЧНІ ДЖЕРЕЛА"),11,"#C3A3DC"));
        content.Children.Add(Text(L.T("Steam, Leetify, CSStats і SCOPE.GG перевіряються автоматично. FACEIT-рейтинг показується, коли він є на відкритому профілі Leetify. Розширення браузера та API-ключі не потрібні."),11));
        content.Children.Add(Text(L.T("Якщо сайт приховав профіль, потребує входу або обмежив запити, відповідний блок покаже цей стан. Програма відображає лише отримані числа. Valve Trust Factor не публікується."),11));
        content.Children.Add(Text("03 / TELEGRAM",11,"#C3A3DC"));content.Children.Add(enabled);content.Children.Add(Text(L.T("Увесь склад — в одному повідомленні: вирівняні K/D, ADR, HS, Leetify, FACEIT Elo і траст. Після матчу воно редагується на «Ви не в грі❤️⚡»."),13));
        content.Children.Add(Text(L.T("Створіть бота через @BotFather → /newbot, відкрийте його чат і натисніть Start. Введіть токен бота та числовий Chat ID. Токен захищається локальним шифруванням Windows."),11));
        content.Children.Add(Text(L.T("Токен Telegram-бота")));content.Children.Add(bot);content.Children.Add(Text("Chat ID"));content.Children.Add(chat);
        content.Children.Add(Text(L.T("У матчі: статистика по два гравці на сторінці та кнопки перегортання. Після виходу: «Ви не в грі❤️⚡». Оновлюється одне повідомлення."),11));
        var test=new Button{Content=L.T("Перевірити Telegram"),HorizontalAlignment=HorizontalAlignment.Left};
        test.Click+=async(_,_)=>{try{var s=Collect();if(!s.TelegramEnabled){status.Text=L.T("Увімкніть Telegram вище.");return;}test.IsEnabled=false;await telegram.Update(s,new MatchState(),[],true);status.Text=L.T(telegram.Status)+" · Message ID: "+telegram.MessageId;}catch(Exception ex){status.Text=L.T(ex.Message);}finally{test.IsEnabled=true;}};content.Children.Add(test);
        var recovery=new StackPanel();content.Children.Add(new Expander{Header=L.T("Відновити наявне повідомлення Telegram"),Content=recovery,Foreground=Brushes.LightGray,Margin=new(0,18,0,0)});
        recovery.Children.Add(Text(L.T("Якщо надсилання перервалося з невідомим результатом, вкажіть Message ID уже надісланого повідомлення цього бота, щоб не створювати дубль."),11));recovery.Children.Add(recover);
        var recoverButton=new Button{Content=L.T("Використати повідомлення"),Margin=new(0,10,0,0),HorizontalAlignment=HorizontalAlignment.Left};recoverButton.Click+=(_,_)=>{try{if(!long.TryParse(recover.Text,out var id))throw new ArgumentException(L.T("Введіть числовий Message ID."));telegram.Recover(Collect(),id);status.Text=L.T("Повідомлення підключене. Збережіть налаштування.");}catch(Exception ex){status.Text=L.T(ex.Message);}};recovery.Children.Add(recoverButton);
    }
    Settings Collect()
    {
        var c=chat.Text.Trim();if(enabled.IsChecked==true&&(!long.TryParse(c,out var id)||id==0))throw new ArgumentException(L.T("Chat ID має бути числовим і не дорівнювати нулю."));
        var token=bot.Password.Trim();if(enabled.IsChecked==true&&!System.Text.RegularExpressions.Regex.IsMatch(token,@"^\d+:[A-Za-z0-9_-]+$"))throw new ArgumentException(L.T("Перевірте токен Telegram-бота."));
        return new(){Language=language.SelectedIndex==1?"uk":"en",Cs2Path=path.Text.Trim(),GsiToken=gsiToken,TelegramEnabled=enabled.IsChecked==true,ChatId=c,BotToken=token,SteamKeyCipher=original.SteamKeyCipher,FaceitKeyCipher=original.FaceitKeyCipher,LeetifyKeyCipher=original.LeetifyKeyCipher};
    }
    void Save(){try{Value=Collect();Store.Write("settings.json",Value);Saved=true;DialogResult=true;}catch(Exception ex){status.Text=L.T(ex.Message);}}
}
