// SideNotch: edge-docked mini player driving Windows media sessions (YouTube in Chrome by default).
// Built with the in-box .NET Framework 4.8 compiler, see build.cmd.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

static class WinRt
{
    // The split per-namespace .winmd files can't bind System.Runtime.WindowsRuntime's AsTask, so adapt by hand.
    public static Task<T> AsTask<T>(this Windows.Foundation.IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (o, st) =>
        {
            try
            {
                if (st == Windows.Foundation.AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else tcs.TrySetException(new InvalidOperationException("WinRT operation " + st));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        return tcs.Task;
    }
}

class Settings
{
    public bool Right = true;
    public double YFrac = 0.4;   // vertical centre of the notch as a fraction of the work area
    public string Monitor = "";  // Screen.DeviceName; falls back to primary
    public string Source = "";   // preferred session AUMID; "" = automatic (prefer Chrome)
    // Last track, so the player can offer to resume when the source app is closed.
    public string LastTitle = "", LastSource = "", LastUrl = "";
    public double LastPos, LastDur;

    public static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideNotch");
    static readonly string FilePath = System.IO.Path.Combine(Dir, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        if (!File.Exists(FilePath)) return s;
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                double d;
                if (k == "Side") s.Right = v != "Left";
                else if (k == "Y" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0 && d <= 1) s.YFrac = d;
                else if (k == "Monitor") s.Monitor = v;
                else if (k == "Source") s.Source = v;
                else if (k == "LastTitle") s.LastTitle = v;
                else if (k == "LastSource") s.LastSource = v;
                else if (k == "LastUrl") s.LastUrl = v;
                else if (k == "LastPos" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0) s.LastPos = d;
                else if (k == "LastDur" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0) s.LastDur = d;
            }
        }
        catch (Exception ex) { Program.Log("Settings load: " + ex.Message); }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "Side=" + (Right ? "Right" : "Left"),
                "Y=" + YFrac.ToString("0.####", CultureInfo.InvariantCulture),
                "Monitor=" + Monitor,
                "Source=" + Source,
                "LastTitle=" + LastTitle.Replace('\r', ' ').Replace('\n', ' '),
                "LastSource=" + LastSource,
                "LastUrl=" + LastUrl,
                "LastPos=" + LastPos.ToString("0", CultureInfo.InvariantCulture),
                "LastDur=" + LastDur.ToString("0", CultureInfo.InvariantCulture) });
        }
        catch (Exception ex) { Program.Log("Settings save: " + ex.Message); }
    }
}

static class Program
{
    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var path = System.IO.Path.Combine(Settings.Dir, "sidenotch.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, DateTime.Now.ToString("s") + " " + msg + Environment.NewLine);
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "Local\\SideNotch.SingleInstance", out created))
        {
            if (!created) return; // already running; it lives in the tray
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log("Unhandled: " + e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("Fatal: " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (s, e) => { Log("Task: " + e.Exception); e.SetObserved(); };
            new NotchWindow().Show();
            app.Run();
        }
    }
}

class NotchWindow : Window
{
    const double TabW = 14, TabH = 56, PanelW = 320, PanelH = 152, Shadow = 12;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Colour tokens; keep in sync with the XAML resources below.
    static readonly Brush Accent = Frozen("#5B9DFF"), Muted = Frozen("#5E636B"), TabBg = Frozen("#1C1D20"), TabHover = Frozen("#2E3035");
    static readonly Geometry PlayIcon = Geometry.Parse("M0,0 L12,7 L0,14 Z"), PauseIcon = Geometry.Parse("M0,0 H4 V14 H0 Z M8,0 H12 V14 H8 Z");

    const string Xaml = @"
<Grid xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
      TextElement.FontFamily='Segoe UI Variable Text, Segoe UI'>
  <Grid.Resources>
    <SolidColorBrush x:Key='Bg' Color='#1C1D20'/>
    <SolidColorBrush x:Key='Stroke' Color='#34373D'/>
    <SolidColorBrush x:Key='Hover' Color='#2E3035'/>
    <SolidColorBrush x:Key='Pressed' Color='#383B41'/>
    <SolidColorBrush x:Key='Track' Color='#3A3D44'/>
    <SolidColorBrush x:Key='Text' Color='#ECEDEF'/>
    <SolidColorBrush x:Key='TextMuted' Color='#A2A7AF'/>
    <SolidColorBrush x:Key='Accent' Color='#5B9DFF'/>
    <SolidColorBrush x:Key='AccentHover' Color='#78AEFF'/>
    <SolidColorBrush x:Key='AccentPressed' Color='#4B8BEF'/>

    <Style x:Key='RingFocus'><Setter Property='Control.Template'><Setter.Value><ControlTemplate><Ellipse Margin='-3' Stroke='{StaticResource Accent}' StrokeThickness='2'/></ControlTemplate></Setter.Value></Setter></Style>
    <Style x:Key='TrackFocus'><Setter Property='Control.Template'><Setter.Value><ControlTemplate><Rectangle Margin='-4,-1' RadiusX='9' RadiusY='9' Stroke='{StaticResource Accent}' StrokeThickness='1'/></ControlTemplate></Setter.Value></Setter></Style>

    <Style x:Key='Ghost' TargetType='Button'>
      <Setter Property='Width' Value='32'/><Setter Property='Height' Value='32'/>
      <Setter Property='FocusVisualStyle' Value='{StaticResource RingFocus}'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid>
            <Border x:Name='B' CornerRadius='16' Background='Transparent'/>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='{StaticResource Hover}'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='{StaticResource Pressed}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.3'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <Style x:Key='Primary' TargetType='Button'>
      <Setter Property='Width' Value='40'/><Setter Property='Height' Value='40'/>
      <Setter Property='FocusVisualStyle' Value='{StaticResource RingFocus}'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Grid>
            <Border x:Name='B' CornerRadius='20' Background='{StaticResource Accent}'/>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='{StaticResource AccentHover}'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='{StaticResource AccentPressed}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='B' Property='Background' Value='{StaticResource Track}'/><Setter Property='Opacity' Value='0.6'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>

    <ControlTemplate x:Key='Flat' TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
    <ControlTemplate x:Key='Fill' TargetType='RepeatButton'><Border Height='4' CornerRadius='2' Background='{StaticResource Accent}' VerticalAlignment='Center'/></ControlTemplate>
  </Grid.Resources>

  <Button x:Name='Tab' AutomationProperties.Name='SideNotch: open player' Cursor='Hand' FocusVisualStyle='{x:Null}'>
    <Button.Template><ControlTemplate TargetType='Button'><ContentPresenter/></ControlTemplate></Button.Template>
    <Border x:Name='TabBody' Background='{StaticResource Bg}' BorderBrush='{StaticResource Stroke}'>
      <Rectangle x:Name='TabMark' Width='3' Height='22' RadiusX='1.5' RadiusY='1.5' Fill='#5E636B'/>
    </Border>
  </Button>

  <Grid x:Name='Panel' Visibility='Collapsed' Width='320' Height='152'>
    <Border x:Name='ShadowBox' Background='{StaticResource Bg}'>
      <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='2' Direction='270' Opacity='0.5' Color='Black'/></Border.Effect>
    </Border>
    <Border x:Name='Card' Background='{StaticResource Bg}' BorderBrush='{StaticResource Stroke}' Padding='16,12,16,10'>
      <Grid>
        <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>

        <DockPanel>
          <Ellipse x:Name='Dot' Width='6' Height='6' Margin='0,1,7,0' VerticalAlignment='Center' Fill='#5E636B'/>
          <TextBlock x:Name='Source' FontSize='11.5' Foreground='{StaticResource TextMuted}' TextTrimming='CharacterEllipsis'/>
        </DockPanel>

        <TextBlock x:Name='Title' Grid.Row='1' Margin='0,5,0,0' Height='38' FontSize='14' FontWeight='SemiBold'
                   LineHeight='19' LineStackingStrategy='BlockLineHeight' TextWrapping='Wrap' TextTrimming='CharacterEllipsis'
                   Foreground='{StaticResource Text}'/>

        <Grid Grid.Row='2' Margin='0,8,0,0' Height='18'>
          <Slider x:Name='Seek' VerticalAlignment='Center' IsMoveToPointEnabled='True' SmallChange='5' LargeChange='15'
                  AutomationProperties.Name='Seek position' ToolTipService.ShowOnDisabled='True' FocusVisualStyle='{StaticResource TrackFocus}'>
            <Slider.Template>
              <ControlTemplate TargetType='Slider'>
                <Grid x:Name='G' Height='18' Background='Transparent'>
                  <Border Height='4' CornerRadius='2' Background='{StaticResource Track}' VerticalAlignment='Center'/>
                  <Track x:Name='PART_Track'>
                    <Track.DecreaseRepeatButton><RepeatButton Template='{StaticResource Fill}' Focusable='False' AutomationProperties.Name='Back 15 seconds'/></Track.DecreaseRepeatButton>
                    <Track.IncreaseRepeatButton><RepeatButton Template='{StaticResource Flat}' Focusable='False' AutomationProperties.Name='Forward 15 seconds'/></Track.IncreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
                        <Ellipse x:Name='E' Width='12' Height='12' Fill='{StaticResource Text}' StrokeThickness='2' Stroke='Transparent'/>
                        <ControlTemplate.Triggers>
                          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='E' Property='Stroke' Value='{StaticResource Accent}'/></Trigger>
                          <Trigger Property='IsDragging' Value='True'><Setter TargetName='E' Property='Stroke' Value='{StaticResource Accent}'/><Setter TargetName='E' Property='Fill' Value='White'/></Trigger>
                        </ControlTemplate.Triggers>
                      </ControlTemplate></Thumb.Template></Thumb>
                    </Track.Thumb>
                  </Track>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsEnabled' Value='False'><Setter TargetName='G' Property='Opacity' Value='0.35'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Slider.Template>
          </Slider>
          <Border x:Name='Loader' Visibility='Collapsed' Height='4' CornerRadius='2' Background='{StaticResource Track}'
                  VerticalAlignment='Center' ClipToBounds='True' AutomationProperties.Name='Loading'>
            <Rectangle x:Name='LoaderBar' Width='72' HorizontalAlignment='Left' RadiusX='2' RadiusY='2'>
              <Rectangle.Fill>
                <LinearGradientBrush StartPoint='0,0' EndPoint='1,0'>
                  <GradientStop Color='#005B9DFF' Offset='0'/><GradientStop Color='#FF5B9DFF' Offset='0.5'/><GradientStop Color='#005B9DFF' Offset='1'/>
                </LinearGradientBrush>
              </Rectangle.Fill>
            </Rectangle>
          </Border>
        </Grid>

        <Grid Grid.Row='3' Margin='0,2,0,0'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
          <TextBlock x:Name='Elapsed' FontSize='11' Foreground='{StaticResource TextMuted}' VerticalAlignment='Center'/>
          <StackPanel Grid.Column='1' Orientation='Horizontal'>
            <Button x:Name='Prev' Style='{StaticResource Ghost}' Margin='0,0,10,0' AutomationProperties.Name='Previous track' ToolTip='Previous' ToolTipService.ShowOnDisabled='True'>
              <Path Fill='{StaticResource Text}' Data='M0,0 H2 V12 H0 Z M12,0 L3,6 L12,12 Z'/>
            </Button>
            <Button x:Name='PlayPause' Style='{StaticResource Primary}' AutomationProperties.Name='Play'>
              <Path x:Name='PlayIcon' Fill='#0E1116'/>
            </Button>
            <Button x:Name='Next' Style='{StaticResource Ghost}' Margin='10,0,0,0' AutomationProperties.Name='Next track' ToolTip='Next' ToolTipService.ShowOnDisabled='True'>
              <Path Fill='{StaticResource Text}' Data='M0,0 L9,6 L0,12 Z M10,0 H12 V12 H10 Z'/>
            </Button>
          </StackPanel>
          <TextBlock x:Name='Duration' Grid.Column='2' FontSize='11' Foreground='{StaticResource TextMuted}' VerticalAlignment='Center' HorizontalAlignment='Right'/>
        </Grid>
      </Grid>
    </Border>
  </Grid>
</Grid>";

    readonly Settings settings = Settings.Load();
    readonly Button tab, playPause, prevButton, nextButton;
    readonly Border tabBody, card, shadowBox, loader;
    readonly Grid panel;
    readonly Rectangle tabMark, loaderBar;
    readonly Ellipse statusDot;
    readonly TextBlock sourceText, titleText, elapsedText, durationText;
    readonly Path playIcon;
    readonly Slider seek;
    readonly ScaleTransform panelScale = new ScaleTransform();
    readonly TranslateTransform loaderX = new TranslateTransform();
    readonly DispatcherTimer ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    // Deactivated misses outside clicks when Windows refused us focus (e.g. opened from the tray), so also watch presses while open.
    readonly DispatcherTimer outsideWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
    readonly WF.NotifyIcon tray;
    double dpi = 1;
    bool expanded, seekDragging, suppressSeek, pressing, movedTab, loaderRunning;
    Point pressPt;

    // media state
    GlobalSystemMediaTransportControlsSessionManager mgr;
    string mgrError;
    Session session;
    string sessionId = "", title = "", artist = "", notice;
    Status status = Status.Closed;
    bool canPlay, canPause, canToggle, canSeek, canPrev, canNext;
    double rate = 1;
    TimeSpan start, end, basePos;
    DateTimeOffset baseAt, localSeekAt;
    int propsVersion;
    readonly Windows.Foundation.TypedEventHandler<Session, MediaPropertiesChangedEventArgs> onProps;
    readonly Windows.Foundation.TypedEventHandler<Session, PlaybackInfoChangedEventArgs> onPlayback;
    readonly Windows.Foundation.TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> onTimeline;

    static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

    public NotchWindow()
    {
        Title = "SideNotch";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        UseLayoutRounding = true;

        var root = (Grid)XamlReader.Parse(Xaml);
        Content = root;
        tab = (Button)root.FindName("Tab");
        tabBody = (Border)root.FindName("TabBody");
        tabMark = (Rectangle)root.FindName("TabMark");
        panel = (Grid)root.FindName("Panel");
        card = (Border)root.FindName("Card");
        shadowBox = (Border)root.FindName("ShadowBox");
        loader = (Border)root.FindName("Loader");
        loaderBar = (Rectangle)root.FindName("LoaderBar");
        statusDot = (Ellipse)root.FindName("Dot");
        loaderBar.RenderTransform = loaderX;
        sourceText = (TextBlock)root.FindName("Source");
        titleText = (TextBlock)root.FindName("Title");
        playPause = (Button)root.FindName("PlayPause");
        prevButton = (Button)root.FindName("Prev");
        nextButton = (Button)root.FindName("Next");
        playIcon = (Path)root.FindName("PlayIcon");
        elapsedText = (TextBlock)root.FindName("Elapsed");
        durationText = (TextBlock)root.FindName("Duration");
        seek = (Slider)root.FindName("Seek");
        panel.RenderTransform = panelScale;

        // Tab: click expands, drag moves; Click also covers keyboard and UI Automation Invoke.
        tab.PreviewMouseLeftButtonDown += (s, e) => { pressing = true; movedTab = false; pressPt = CursorDip(); tab.CaptureMouse(); e.Handled = true; };
        tab.PreviewMouseMove += (s, e) =>
        {
            if (!pressing) return;
            var p = CursorDip();
            if (!movedTab && (p - pressPt).Length < 4) return;
            movedTab = true;
            Left = p.X - Width / 2;
            Top = p.Y - Height / 2;
        };
        tab.PreviewMouseLeftButtonUp += (s, e) => { e.Handled = true; EndPress(); };
        tab.LostMouseCapture += (s, e) => EndPress();
        tab.Click += (s, e) => Expand();
        tab.MouseEnter += (s, e) => tabBody.Background = TabHover;
        tab.MouseLeave += (s, e) => tabBody.Background = TabBg;

        playPause.Click += (s, e) => TogglePlay();
        prevButton.Click += (s, e) => Skip(false);
        nextButton.Click += (s, e) => Skip(true);
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => seekDragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) => { seekDragging = false; SeekTo(seek.Value); }));
        seek.ValueChanged += (s, e) =>
        {
            if (suppressSeek) return;
            if (seekDragging) { elapsedText.Text = Fmt(TimeSpan.FromSeconds(seek.Value)); return; }
            SeekTo(seek.Value);
        };

        Deactivated += (s, e) => Collapse();
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) { Collapse(); e.Handled = true; }
            else if (e.Key == Key.Space && !(Keyboard.FocusedElement is ButtonBase)) { TogglePlay(); e.Handled = true; }
        };
        ticker.Tick += (s, e) => UpdateTimes();
        outsideWatch.Tick += (s, e) =>
        {
            if (WF.Control.MouseButtons == WF.MouseButtons.None) return;
            var p = CursorDip();
            if (p.X < Left || p.X > Left + Width || p.Y < Top || p.Y > Top + Height) Collapse();
        };

        SystemEvents.DisplaySettingsChanged += (s, e) => Ui(() => { UpdateDpi(); Place(); });
        UpdateDpi();
        Place();

        onProps = (s, e) => Ui(RefreshProps);
        onPlayback = (s, e) => Ui(() => { RefreshPlayback(); Remember(); Render(); });
        onTimeline = (s, e) => Ui(() => { RefreshTimeline(); Render(); });

        tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "SideNotch", Visible = true, ContextMenuStrip = new WF.ContextMenuStrip() };
        tray.ContextMenuStrip.Opening += (s, e) => { BuildMenu(tray.ContextMenuStrip); e.Cancel = false; };
        tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) { Show(); Expand(); } };

        Render();
        InitMedia();
    }

    void Ui(Action a) { Dispatcher.BeginInvoke(a); }

    // ---------- placement ----------

    void UpdateDpi()
    {
        // ponytail: system-DPI scale only; mixed-DPI monitors get Windows bitmap scaling. Per-monitor awareness if that looks blurry.
        dpi = WF.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
    }

    Point CursorDip() { var p = WF.Control.MousePosition; return new Point(p.X / dpi, p.Y / dpi); }

    WF.Screen CurrentScreen()
    {
        foreach (var s in WF.Screen.AllScreens) if (s.DeviceName == settings.Monitor) return s;
        return WF.Screen.PrimaryScreen;
    }

    void Place()
    {
        var wa = CurrentScreen().WorkingArea;
        double l = wa.Left / dpi, t = wa.Top / dpi, w = wa.Width / dpi, h = wa.Height / dpi;
        // The expanded window carries a transparent margin on its inner sides for the drop shadow (click-through).
        double W = expanded ? PanelW + Shadow : TabW, H = expanded ? PanelH + 2 * Shadow : TabH;
        Width = W;
        Height = H;
        Left = settings.Right ? l + w - W : l;
        Top = Math.Max(t, Math.Min(t + h - H, t + settings.YFrac * h - H / 2));

        bool r = settings.Right;
        tabBody.CornerRadius = r ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(0, 8, 8, 0);
        tabBody.BorderThickness = r ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);
        card.CornerRadius = shadowBox.CornerRadius = r ? new CornerRadius(14, 0, 0, 14) : new CornerRadius(0, 14, 14, 0);
        card.BorderThickness = tabBody.BorderThickness;
        panel.Margin = r ? new Thickness(Shadow, Shadow, 0, Shadow) : new Thickness(0, Shadow, Shadow, Shadow);
        panel.RenderTransformOrigin = new Point(r ? 1 : 0, 0.5);
    }

    void EndPress()
    {
        if (!pressing) return;
        pressing = false;
        if (tab.IsMouseCaptured) tab.ReleaseMouseCapture();
        if (!movedTab) { Expand(); return; }
        // Snap to the nearest edge of whichever monitor the cursor ended on.
        var p = WF.Control.MousePosition;
        var scr = WF.Screen.FromPoint(p);
        var wa = scr.WorkingArea;
        settings.Monitor = scr.DeviceName;
        settings.Right = p.X > wa.Left + wa.Width / 2;
        settings.YFrac = Math.Max(0, Math.Min(1, (p.Y - wa.Top) / (double)wa.Height));
        settings.Save();
        Place();
    }

    void Expand()
    {
        if (!IsVisible) Show();
        if (expanded) { Activate(); return; }
        expanded = true;
        tab.Visibility = Visibility.Collapsed;
        panel.Visibility = Visibility.Visible;
        Place();
        Activate();
        outsideWatch.Start();
        CaptureUrl();
        // Enter decelerates; Windows' "animation effects" setting turns motion off.
        var dur = new Duration(TimeSpan.FromMilliseconds(Motion ? 150 : 0));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        Render();
    }

    void Collapse()
    {
        if (!expanded || seekDragging) return;
        expanded = false;
        outsideWatch.Stop();
        UpdateTicker();
        // Exit is shorter than enter and accelerates away.
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(Motion ? 90 : 0))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (s, e) =>
        {
            if (expanded) return; // re-opened mid-fade
            panel.Visibility = Visibility.Collapsed;
            tab.Visibility = Visibility.Visible;
            Place();
            SetLoaderRunning(false);
        };
        panel.BeginAnimation(OpacityProperty, fade);
    }

    static bool Motion { get { return SystemParameters.ClientAreaAnimation; } }

    // ---------- media sessions ----------

    async void InitMedia()
    {
        try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(); }
        catch (Exception ex) { mgrError = "Media sessions unavailable"; Program.Log("Manager: " + ex); Render(); return; }
        mgr.SessionsChanged += (s, e) => Ui(SelectSession);
        mgr.CurrentSessionChanged += (s, e) => Ui(SelectSession);
        SelectSession();
    }

    static string Id(Session s) { try { return s.SourceAppUserModelId ?? ""; } catch { return ""; } }
    static bool IsChrome(string id) { return id.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0; }

    static string AppName(string id)
    {
        if (IsChrome(id)) return "Chrome";
        string n = id.Split('!')[0];
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
        n = n.Split('_')[0];
        n = n.Substring(n.LastIndexOfAny(new[] { '.', '\\' }) + 1);
        return n.Length == 0 ? id : n;
    }

    void SelectSession()
    {
        if (mgr == null) return;
        IReadOnlyList<Session> list;
        try { list = mgr.GetSessions(); }
        catch (Exception ex) { Program.Log("GetSessions: " + ex.Message); list = new Session[0]; }

        // Priority: user-chosen source > the session we already follow (stability) > Chrome > system current > anything.
        Session pick = null;
        if (settings.Source != "") pick = list.FirstOrDefault(x => Id(x) == settings.Source);
        if (pick == null && session != null) pick = list.FirstOrDefault(x => ReferenceEquals(x, session)) ?? list.FirstOrDefault(x => Id(x) == sessionId);
        if (pick == null) pick = list.FirstOrDefault(x => IsChrome(Id(x)));
        if (pick == null) { try { pick = mgr.GetCurrentSession(); } catch { } }
        if (pick == null) pick = list.FirstOrDefault();

        if (ReferenceEquals(pick, session)) return;
        // YouTube tears its session down between videos; bridge the gap instead of flashing "Nothing playing".
        if (pick == null && session != null && !Loading) BeginLoading("Waiting for the next track…", 6, "");
        Attach(pick);
    }

    void Attach(Session s)
    {
        if (session != null)
        {
            Remember(); // e.g. Chrome closed: keep where the track got to
            try
            {
                session.MediaPropertiesChanged -= onProps;
                session.PlaybackInfoChanged -= onPlayback;
                session.TimelinePropertiesChanged -= onTimeline;
            }
            catch { } // session already gone
        }
        session = s;
        sessionId = s == null ? "" : Id(s);
        title = artist = "";
        status = Status.Closed;
        start = end = basePos = TimeSpan.Zero;
        localSeekAt = default(DateTimeOffset);
        if (s != null)
        {
            try
            {
                s.MediaPropertiesChanged += onProps;
                s.PlaybackInfoChanged += onPlayback;
                s.TimelinePropertiesChanged += onTimeline;
            }
            catch (Exception ex) { Program.Log("Attach: " + ex.Message); }
            RefreshPlayback();
            RefreshTimeline();
            RefreshProps();
        }
        Render();
    }

    async void RefreshProps()
    {
        var s = session;
        if (s == null) return;
        int v = ++propsVersion;
        try
        {
            var p = await s.TryGetMediaPropertiesAsync().AsTask(); // never touches p.Thumbnail: no artwork by design
            if (v != propsVersion || !ReferenceEquals(s, session)) return;
            title = (p.Title ?? "").Trim();
            artist = p.Artist ?? "";
            if (title == "" && lastTitle != "" && !Loading) BeginLoading("Loading…", 6, "");
            if (title != "")
            {
                if (Loading && title != loadingFrom) loadingUntil = DateTime.MinValue;
                lastTitle = title;
                if (title != settings.LastTitle || sessionId != settings.LastSource)
                {
                    settings.LastTitle = title;
                    settings.LastSource = sessionId;
                    settings.LastUrl = "";
                    settings.LastPos = settings.LastDur = 0;
                    settings.Save();
                }
                CaptureUrl();
            }
        }
        catch (Exception ex) { Program.Log("Props: " + ex.Message); }
        Render();
    }

    void RefreshPlayback()
    {
        if (session == null) return;
        try
        {
            var info = session.GetPlaybackInfo();
            // Rebase the extrapolated position so a pause freezes where it is, even before a timeline event arrives.
            basePos = start + CurrentPos();
            baseAt = DateTimeOffset.Now;
            status = info.PlaybackStatus;
            var c = info.Controls;
            canPlay = c.IsPlayEnabled;
            canPause = c.IsPauseEnabled;
            canToggle = c.IsPlayPauseToggleEnabled;
            canSeek = c.IsPlaybackPositionEnabled;
            canPrev = c.IsPreviousEnabled;
            canNext = c.IsNextEnabled;
            rate = info.PlaybackRate.HasValue && info.PlaybackRate.Value > 0 ? info.PlaybackRate.Value : 1;
        }
        catch (Exception ex) { Program.Log("Playback: " + ex.Message); }
    }

    void RefreshTimeline()
    {
        if (session == null) return;
        try
        {
            var tl = session.GetTimelineProperties();
            start = tl.StartTime;
            end = tl.EndTime;
            // A skip that lands on the same title (e.g. restart) still resets the position: treat that as loaded.
            if (Loading && skipAt != default(DateTimeOffset) && tl.LastUpdatedTime > skipAt && tl.Position - tl.StartTime < TimeSpan.FromSeconds(3) && title != "")
                loadingUntil = DateTime.MinValue;
            if (tl.LastUpdatedTime < localSeekAt) return; // older than our own seek; keep the optimistic position
            basePos = tl.Position;
            baseAt = tl.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : tl.LastUpdatedTime;
        }
        catch (Exception ex) { Program.Log("Timeline: " + ex.Message); }
    }

    bool HasDuration { get { return session != null && end > start; } }

    TimeSpan CurrentPos()
    {
        var p = basePos;
        if (status == Status.Playing)
        {
            var since = DateTimeOffset.Now - baseAt;
            if (since > TimeSpan.Zero) p += TimeSpan.FromTicks((long)(since.Ticks * rate));
        }
        if (HasDuration && p > end) p = end;
        p -= start;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    // ---------- resume after the source closed ----------

    bool capturing;

    void Remember()
    {
        if (session == null || title == "" || title != settings.LastTitle) return;
        settings.LastPos = Math.Floor(CurrentPos().TotalSeconds);
        settings.LastDur = HasDuration ? Math.Floor((end - start).TotalSeconds) : 0;
        settings.Save();
        CaptureUrl();
    }

    async void CaptureUrl()
    {
        string t = settings.LastTitle;
        if (capturing || t == "" || settings.LastUrl != "" || !IsChrome(settings.LastSource)) return;
        capturing = true;
        string url = await Task.Run(() => FindChromeUrl(t));
        capturing = false;
        if (url != null && settings.LastTitle == t) { settings.LastUrl = url; settings.Save(); }
    }

    // Reads (never clicks) the address bar of the Chrome window whose active tab shows this title.
    // ponytail: only finds the URL while the video's tab is the active one in its window; on miss, resume falls back to a YouTube search.
    static string FindChromeUrl(string title)
    {
        try
        {
            var wins = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"));
            foreach (AutomationElement w in wins)
            {
                if (w.Current.Name.IndexOf(title, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var edit = w.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                object vp;
                if (edit == null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out vp)) continue;
                var url = ((ValuePattern)vp).Current.Value ?? "";
                if (url.IndexOf("youtu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                return url.Contains("://") ? url : "https://" + url;
            }
        }
        catch (Exception ex) { Program.Log("Url: " + ex.Message); }
        return null;
    }

    bool Resumable { get { return session == null && mgrError == null && settings.LastTitle != ""; } }

    void Resume()
    {
        var st = settings;
        string app = AppName(st.LastSource);
        try
        {
            if (st.LastSource == "" || IsChrome(st.LastSource))
            {
                string url;
                if (st.LastUrl == "") url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(st.LastTitle);
                else
                {
                    url = System.Text.RegularExpressions.Regex.Replace(st.LastUrl, @"&t=\d+s?", "");
                    // Pick up where it stopped, unless it had (nearly) finished.
                    if (st.LastPos >= 5 && (st.LastDur == 0 || st.LastPos < st.LastDur - 5))
                        url += (url.Contains("?") ? "&" : "?") + "t=" + st.LastPos.ToString("0", CultureInfo.InvariantCulture) + "s";
                }
                try { System.Diagnostics.Process.Start("chrome.exe", "\"" + url + "\""); }
                catch { System.Diagnostics.Process.Start(url); } // no Chrome: default browser
            }
            else System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + st.LastSource);
        }
        catch (Exception ex) { Program.Log("Resume: " + ex.Message); Flash("Couldn't open " + app); return; }
        lastTitle = st.LastTitle;
        BeginLoading("Opening in " + (app == "" ? "Chrome" : app) + "…", 15, "");
    }

    async void TogglePlay()
    {
        var s = session;
        if (s == null) { if (Resumable && !Loading) Resume(); return; }
        bool ok = false;
        try
        {
            if (status == Status.Playing) ok = canPause ? await s.TryPauseAsync().AsTask() : await s.TryTogglePlayPauseAsync().AsTask();
            else ok = canPlay ? await s.TryPlayAsync().AsTask() : await s.TryTogglePlayPauseAsync().AsTask();
        }
        catch (Exception ex) { Program.Log("Toggle: " + ex.Message); }
        if (!ok) Flash("Play/pause not accepted by " + AppName(sessionId));
    }

    async void Skip(bool next)
    {
        var s = session;
        if (s == null || Loading) return;
        skipAt = DateTimeOffset.Now;
        BeginLoading(next ? "Loading next track…" : "Loading previous track…", 8, title);
        bool ok = false;
        try { ok = next ? await s.TrySkipNextAsync().AsTask() : await s.TrySkipPreviousAsync().AsTask(); }
        catch (Exception ex) { Program.Log("Skip: " + ex.Message); }
        if (!ok)
        {
            loadingUntil = DateTime.MinValue;
            Flash((next ? "Next" : "Previous") + " not accepted by " + AppName(sessionId));
        }
    }

    // ---------- loading state ----------

    DateTime loadingUntil;
    DateTimeOffset skipAt;
    string loadingText = "", loadingFrom = "", lastTitle = "";

    bool Loading { get { return DateTime.Now < loadingUntil; } }

    async void BeginLoading(string text, int seconds, string fromTitle)
    {
        loadingText = text;
        loadingFrom = fromTitle;
        var until = loadingUntil = DateTime.Now.AddSeconds(seconds);
        Render();
        await Task.Delay(TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(50));
        if (loadingUntil == until) Render(); // timed out: fall through to whatever is true now
    }

    void SetLoaderRunning(bool on)
    {
        if (on == loaderRunning) return;
        loaderRunning = on;
        if (!on) { loaderX.BeginAnimation(TranslateTransform.XProperty, null); return; }
        // Indeterminate shimmer travelling across the track (seek row is PanelW minus 32px padding).
        var a = new DoubleAnimation(-72, PanelW - 32, new Duration(TimeSpan.FromMilliseconds(1200)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        loaderX.BeginAnimation(TranslateTransform.XProperty, a);
    }

    async void SeekTo(double seconds)
    {
        var s = session;
        if (s == null || !canSeek || !HasDuration) return;
        var target = start + TimeSpan.FromSeconds(seconds);
        basePos = target;
        baseAt = localSeekAt = DateTimeOffset.Now;
        UpdateTimes();
        bool ok = false;
        try { ok = await s.TryChangePlaybackPositionAsync(target.Ticks).AsTask(); }
        catch (Exception ex) { Program.Log("Seek: " + ex.Message); }
        if (!ok)
        {
            localSeekAt = default(DateTimeOffset);
            RefreshTimeline();
            Flash("Seek not accepted by " + AppName(sessionId));
            UpdateTimes();
        }
    }

    async void Flash(string msg)
    {
        notice = msg;
        Render();
        await Task.Delay(2500);
        if (notice == msg) { notice = null; Render(); }
    }

    // ---------- rendering ----------

    static string Fmt(TimeSpan t)
    {
        return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                                 : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
    }

    void Render()
    {
        bool loading = Loading && mgrError == null;
        bool has = session != null;
        bool playing = has && !loading && status == Status.Playing;
        bool resume = !loading && Resumable;
        string resumeIn = settings.LastSource == "" || IsChrome(settings.LastSource) ? "Chrome" : AppName(settings.LastSource);

        if (loading) titleText.Text = lastTitle != "" ? lastTitle : "Loading…";
        else if (resume) titleText.Text = settings.LastTitle;
        else titleText.Text = !has ? "Nothing playing" : (title == "" ? "Untitled" : title);
        titleText.Opacity = loading ? 0.45 : resume ? 0.7 : 1;
        titleText.ToolTip = titleText.Text;
        if (notice != null) sourceText.Text = notice;
        else if (mgrError != null) sourceText.Text = mgrError;
        else if (loading) sourceText.Text = loadingText;
        else if (resume) sourceText.Text = "Last played · Play resumes in " + resumeIn;
        else if (!has) sourceText.Text = "Start a video in Chrome";
        else sourceText.Text = AppName(sessionId) + (status == Status.Paused ? " · Paused" : "") + (artist != "" ? " · " + artist : "");
        statusDot.Fill = playing ? Accent : Muted;

        playIcon.Data = playing ? PauseIcon : PlayIcon;
        playIcon.Margin = new Thickness(playing ? 0 : 3, 0, 0, 0); // optical centring of the triangle
        string playName = playing ? "Pause" : resume ? "Resume in " + resumeIn : "Play";
        AutomationProperties.SetName(playPause, playName);
        playPause.ToolTip = playName;
        // Controls are disabled while a skip is in flight so a second press can't stack up.
        playPause.IsEnabled = resume || has && !loading && (playing ? canPause || canToggle : canPlay || canToggle);
        prevButton.IsEnabled = has && !loading && canPrev;
        nextButton.IsEnabled = has && !loading && canNext;
        prevButton.ToolTip = prevButton.IsEnabled || loading ? "Previous" : "No previous track in this source";
        nextButton.ToolTip = nextButton.IsEnabled || loading ? "Next" : "No next track in this source";

        seek.Visibility = loading ? Visibility.Hidden : Visibility.Visible;
        loader.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        SetLoaderRunning(loading && expanded);
        seek.IsEnabled = !loading && HasDuration && canSeek;
        seek.ToolTip = seek.IsEnabled ? null : (has && !loading ? "Seeking isn't available for this media" : null);

        tabMark.Fill = playing || loading ? Accent : Muted;
        tabMark.Opacity = loading ? 0.5 : 1;
        tab.ToolTip = has || loading || resume ? titleText.Text : "SideNotch";
        UpdateTimes();
        UpdateTicker();
    }

    void UpdateTimes()
    {
        if (Loading) { elapsedText.Text = durationText.Text = ""; return; }
        if (session == null && Resumable)
        {
            // Saved position from last time; shown, not seekable (seek.IsEnabled is false without a session).
            elapsedText.Text = Fmt(TimeSpan.FromSeconds(settings.LastPos));
            durationText.Text = settings.LastDur > 0 ? Fmt(TimeSpan.FromSeconds(settings.LastDur)) : "--:--";
            if (settings.LastDur > 0) SetSeek(Math.Min(settings.LastPos, settings.LastDur), settings.LastDur); else SetSeek(0, 1);
            return;
        }
        if (session == null) { elapsedText.Text = durationText.Text = "--:--"; SetSeek(0, 1); return; }
        var pos = CurrentPos();
        durationText.Text = HasDuration ? Fmt(end - start) : "--:--"; // never invent a duration
        if (seekDragging) return;
        elapsedText.Text = Fmt(pos);
        if (HasDuration) SetSeek(pos.TotalSeconds, (end - start).TotalSeconds); else SetSeek(0, 1);
    }

    void SetSeek(double value, double max)
    {
        suppressSeek = true;
        seek.Maximum = max;
        seek.Value = value;
        suppressSeek = false;
    }

    void UpdateTicker() { ticker.IsEnabled = expanded && session != null && status == Status.Playing; }

    // ---------- tray ----------

    static bool StartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("SideNotch") != null;
    }

    static void SetStartup(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("SideNotch", "\"" + WF.Application.ExecutablePath + "\"");
                else k.DeleteValue("SideNotch", false);
            }
        }
        catch (Exception ex) { Program.Log("Startup: " + ex.Message); }
    }

    void BuildMenu(WF.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(IsVisible ? "Hide notch" : "Show notch", null, (s, e) => { if (IsVisible) Hide(); else Show(); });
        menu.Items.Add("Open player", null, (s, e) => Expand());
        menu.Items.Add(new WF.ToolStripSeparator());

        var side = new WF.ToolStripMenuItem("Side");
        side.DropDownItems.Add(new WF.ToolStripMenuItem("Left edge", null, (s, e) => SetSide(false)) { Checked = !settings.Right });
        side.DropDownItems.Add(new WF.ToolStripMenuItem("Right edge", null, (s, e) => SetSide(true)) { Checked = settings.Right });
        menu.Items.Add(side);

        var src = new WF.ToolStripMenuItem("Media source");
        src.DropDownItems.Add(new WF.ToolStripMenuItem("Automatic (prefer Chrome)", null, (s, e) => SetSource("")) { Checked = settings.Source == "" });
        src.DropDownItems.Add(new WF.ToolStripSeparator());
        var ids = new List<string>();
        try { if (mgr != null) ids = mgr.GetSessions().Select(Id).Where(x => x != "").Distinct().ToList(); } catch { }
        foreach (var id in ids)
        {
            string captured = id;
            var label = AppName(id) + (id == sessionId ? "  (following)" : "");
            src.DropDownItems.Add(new WF.ToolStripMenuItem(label, null, (s, e) => SetSource(captured)) { Checked = settings.Source == id, ToolTipText = id });
        }
        if (ids.Count == 0) src.DropDownItems.Add(new WF.ToolStripMenuItem("No media sessions") { Enabled = false });
        menu.Items.Add(src);

        menu.Items.Add(new WF.ToolStripMenuItem("Start with Windows", null, (s, e) => SetStartup(!StartupEnabled())) { Checked = StartupEnabled() });
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit SideNotch", null, (s, e) => Quit());
    }

    void SetSide(bool right) { settings.Right = right; settings.Save(); Place(); }

    void SetSource(string id)
    {
        settings.Source = id;
        settings.Save();
        Attach(null); // drop the current follow so selection re-runs with the new preference
        SelectSession();
    }

    void Quit()
    {
        Remember();
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    static SD.Icon MakeIcon()
    {
        using (var bmp = new SD.Bitmap(32, 32))
        using (var g = SD.Graphics.FromImage(bmp))
        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(0x2B, 0x2D, 0x31)))
        using (var accent = new SD.SolidBrush(SD.Color.FromArgb(0x5B, 0x9D, 0xFF)))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(bg, 1, 1, 30, 30);
            g.FillPolygon(accent, new[] { new SD.Point(12, 9), new SD.Point(23, 16), new SD.Point(12, 23) });
            return SD.Icon.FromHandle(bmp.GetHicon());
        }
    }
}
