// Fox Spirit Codex Boot. See LICENSE.txt and THIRD-PARTY-NOTICES.txt.
// Native app discovery / DWM approach adapted from Dragon Codex Boot (MIT).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;

[assembly: AssemblyTitle("涂山苏苏 · Codex 启动动画")]
[assembly: AssemblyProduct("Fox Spirit Codex Boot")]
[assembly: AssemblyDescription("Personal Tu Shan Susu themed startup launcher for Codex")]
[assembly: AssemblyVersion("1.0.4.0")]
[assembly: AssemblyFileVersion("1.0.4.0")]

namespace FoxSpiritCodexBoot {
    public sealed class Config {
        public Config() { WindowSizing="MatchCodex"; MaxWaitSeconds=120; MaximizeColdStart=true; }
        public string DisplayName { get; set; }
        public string Artwork { get; set; }
        public string AppLaunch { get; set; }
        public string ProcessPathContains { get; set; }
        public string[] ProcessNames { get; set; }
        public double DurationSeconds { get; set; }
        public double HoldAtSeconds { get; set; }
        public double TransitionSeconds { get; set; }
        public double MaxWaitSeconds { get; set; }
        public int PlayerWidth { get; set; }
        public int PlayerHeight { get; set; }
        public string WindowSizing { get; set; }
        public bool MaximizeColdStart { get; set; }
        public string ThemeRuntime { get; set; }
        public string ThemeDataRoot { get; set; }
        public string ThemeId { get; set; }
        public string ThemeVersion { get; set; }
        // Kept explicit for review. This launcher never replaces entrypoints itself.
        public bool AutoReplaceEntrypoints { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Bounds {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public Bounds(int x, int y, int w, int h) { Left=x; Top=y; Right=x+w; Bottom=y+h; }
        public override string ToString() { return Left+","+Top+" "+Width+"x"+Height; }
    }

    public static class Geometry {
        public static double Clamp(double n) { return Math.Max(0,Math.Min(1,n)); }
        public static double Ease(double n) { n=Clamp(n); return n*n*(3-2*n); }
        public static Bounds Player(Bounds work, int maxWidth, int maxHeight) {
            // Physical pixels. Fit a 16:9 scene inside <=90% of the selected work area.
            double scale=Math.Min(1,Math.Min(work.Width*.9/maxWidth,work.Height*.9/maxHeight));
            int w=Math.Max(1,(int)Math.Floor(maxWidth*scale));
            int h=Math.Max(1,(int)Math.Floor(maxHeight*scale));
            return new Bounds(work.Left+(work.Width-w)/2,work.Top+(work.Height-h)/2,w,h);
        }
        public static bool ValidTarget(Bounds target) {
            return target.Width>=450&&target.Height>=280&&target.Left>-30000&&target.Top>-30000;
        }
        public static Bounds MatchTarget(Bounds work,Bounds target) {
            return ValidTarget(target)?target:work;
        }
        public static Bounds Fit(int sourceWidth, int sourceHeight, Bounds viewport) {
            if(sourceWidth<=0||sourceHeight<=0||viewport.Width<=0||viewport.Height<=0)throw new ArgumentOutOfRangeException("viewport");
            double fit=Math.Min((double)viewport.Width/sourceWidth,(double)viewport.Height/sourceHeight);
            int w=Math.Max(1,(int)Math.Floor(sourceWidth*fit));
            int h=Math.Max(1,(int)Math.Floor(sourceHeight*fit));
            return new Bounds(viewport.Left+(viewport.Width-w)/2,viewport.Top+(viewport.Height-h)/2,w,h);
        }
        public static Bounds Lerp(Bounds a, Bounds b, double t) {
            t=Clamp(t);
            return new Bounds((int)Math.Round(a.Left+(b.Left-a.Left)*t),(int)Math.Round(a.Top+(b.Top-a.Top)*t),
                Math.Max(1,(int)Math.Round(a.Width+(b.Width-a.Width)*t)),Math.Max(1,(int)Math.Round(a.Height+(b.Height-a.Height)*t)));
        }
        public static Bounds Thumbnail(int sourceWidth,int sourceHeight,int hostWidth,int hostHeight,double progress) {
            Bounds whole=new Bounds(0,0,hostWidth,hostHeight);
            Bounds final=Fit(sourceWidth,sourceHeight,whole);
            Bounds seed=Fit(sourceWidth,sourceHeight,new Bounds((int)(hostWidth*.58),(int)(hostHeight*.47),(int)(hostWidth*.32),(int)(hostHeight*.34)));
            return Lerp(seed,final,Ease(progress));
        }
        public static bool Finite(double d) { return !Double.IsNaN(d)&&!Double.IsInfinity(d); }
    }

    internal enum HandoffState { Waiting, Ready, Failed, TimedOut }

    // Readiness is driven by this launch's helper process, never a persisted status
    // file. A responsive native window can still contain the official loading page.
    internal sealed class BootReadiness {
        private readonly bool themeRequired;
        private readonly double deadline,settleSeconds;
        private double bothReadyAt=-1;
        public BootReadiness(bool themeRequired,double deadline) {
            this.themeRequired=themeRequired;this.deadline=deadline;
            settleSeconds=themeRequired ? 0.4 : 0;
        }
        public HandoffState Evaluate(double now,bool windowStable,bool themeFinished,bool themeSucceeded,bool launchFailed) {
            if(launchFailed||(themeRequired&&themeFinished&&!themeSucceeded))return HandoffState.Failed;
            bool complete=windowStable&&(!themeRequired||(themeFinished&&themeSucceeded));
            if(!complete)bothReadyAt=-1;
            else {
                if(bothReadyAt<0)bothReadyAt=now;
                if(now-bothReadyAt>=settleSeconds)return HandoffState.Ready;
            }
            return now>=deadline?HandoffState.TimedOut:HandoffState.Waiting;
        }
    }

    internal sealed class PrimaryWindowInfo {
        public IntPtr Handle;
        public uint ProcessId;
        public int Style,ExtendedStyle;
        public bool Visible,Minimized,Maximized,RestoreMaximized,Owned,Cloaked,Responsive;
        public Bounds RestoredBounds;
        public bool Tool { get { return (ExtendedStyle&0x80)!=0; } }
        public bool Minimizable { get { return (Style&0x20000)!=0; } }
        public bool Maximizable { get { return (Style&0x10000)!=0; } }
    }

    internal sealed class PrimaryIdentity {
        public string Handle { get; set; }
        public int ProcessId { get; set; }
        public string StartedAtUtcTicks { get; set; }
        public string Token { get; set; }
    }

    internal enum ReuseMode { None, OfficialLifecycle }

    internal static class PrimaryWindowPolicy {
        public static bool Eligible(PrimaryWindowInfo window) {
            // Visibility is deliberately not required: X hides the primary window
            // while Codex remains in the tray. Mini lacks both caption controls.
            // A brief WM_NULL response delay must not trigger another cold start.
            return window!=null&&!window.Owned&&!window.Tool&&!window.Cloaked
                &&window.Minimizable&&window.Maximizable&&Geometry.ValidTarget(window.RestoredBounds);
        }
        public static PrimaryWindowInfo Best(IEnumerable<PrimaryWindowInfo> windows) {
            return windows.Where(Eligible).OrderByDescending(w=>w.Visible).ThenByDescending(w=>!w.Minimized)
                .ThenByDescending(w=>(long)w.RestoredBounds.Width*w.RestoredBounds.Height).FirstOrDefault();
        }
        public static ReuseMode Mode(bool preview,PrimaryWindowInfo window,bool officialProcessAlive,bool identityTrusted) {
            if(preview||!officialProcessAlive||!Eligible(window))return ReuseMode.None;
            return window.Visible||identityTrusted?ReuseMode.OfficialLifecycle:ReuseMode.None;
        }
        public static bool IdentityMatches(PrimaryIdentity identity,PrimaryWindowInfo window,string startedAtUtcTicks,long token,bool officialOwnerAlive) {
            long handle,savedToken;
            return identity!=null&&Eligible(window)&&officialOwnerAlive
                &&Int64.TryParse(identity.Handle,NumberStyles.Integer,CultureInfo.InvariantCulture,out handle)&&handle==window.Handle.ToInt64()
                &&identity.ProcessId==(int)window.ProcessId&&identity.StartedAtUtcTicks==startedAtUtcTicks
                &&Int64.TryParse(identity.Token,NumberStyles.Integer,CultureInfo.InvariantCulture,out savedToken)&&savedToken>0&&savedToken==token;
        }
        public static bool WaitForPreviousExit(PrimaryIdentity identity,bool ownerAlive,bool identityTrusted) { return identity!=null&&ownerAlive&&!identityTrusted; }
        public static bool MaximizeCold(bool preview,bool reusing,bool configured) { return configured&&!preview&&!reusing; }
        public static bool ColdWindowReady(bool maximizeCold,bool maximized) { return !maximizeCold||maximized; }
        public static bool LaunchAfterExit(bool waiting,bool ownerAlive) { return !waiting||!ownerAlive; }
        public static bool ReuseSurvived(bool windowExists,bool ownerAlive,bool tokenMatches,bool visible,bool minimized) {
            return windowExists&&ownerAlive&&tokenMatches&&visible&&!minimized;
        }
    }

    internal static class Native {
        public delegate bool EnumWindowsProc(IntPtr h,IntPtr l);
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
        [StructLayout(LayoutKind.Sequential)] public struct WindowPlacement {
            public int Length,Flags,ShowCommand;
            public Point MinPosition,MaxPosition;
            public Bounds NormalPosition;
        }
        [StructLayout(LayoutKind.Sequential)] public struct ThumbProperties {
            public uint Flags;
            public Bounds Destination, Source;
            public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
        }
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb,IntPtr param);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Bounds b);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h,ref WindowPlacement placement);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h,out Bounds b);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h,ref Point p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h,uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h,int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h,int index,int val);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h,int how);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SetPropW",SetLastError=true)] public static extern bool SetProp(IntPtr h,string name,IntPtr value);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetPropW")] public static extern IntPtr GetProp(IntPtr h,string name);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint flags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h,uint color,byte alpha,uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,IntPtr l,uint flags,uint ms,out IntPtr result);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h,uint flags,StringBuilder path,ref int length);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest,IntPtr source,out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb,ref ThumbProperties props);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h,int attr,out int val,int size);
        public static Bounds ClientBounds(IntPtr h) {
            Bounds b; Point p=new Point();
            if(!GetClientRect(h,out b)||!ClientToScreen(h,ref p))return new Bounds();
            return new Bounds(p.X,p.Y,b.Width,b.Height);
        }
        public static bool Responsive(IntPtr h) {
            IntPtr result;
            return h!=IntPtr.Zero&&SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,80,out result)!=IntPtr.Zero;
        }
        public static string ProcessPath(int pid) {
            IntPtr process=OpenProcess(0x1000,false,pid);
            if(process==IntPtr.Zero)return "";
            try {
                StringBuilder buffer=new StringBuilder(4096); int count=buffer.Capacity;
                return QueryFullProcessImageName(process,0,buffer,ref count)?buffer.ToString():"";
            } finally { CloseHandle(process); }
        }
        public const string PrimaryIdentityProperty="FoxSpiritCodexBoot.PrimaryIdentity";
        public static string ProcessStartedAt(int pid) {
            try { using(Process process=Process.GetProcessById(pid))return process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        public static bool IdentityOwnerAlive(Config c,PrimaryIdentity identity) {
            if(identity==null||identity.ProcessId<=0||String.IsNullOrWhiteSpace(identity.StartedAtUtcTicks))return false;
            string path=ProcessPath(identity.ProcessId);
            return !String.IsNullOrEmpty(path)&&path.IndexOf(c.ProcessPathContains,StringComparison.OrdinalIgnoreCase)>=0
                &&ProcessStartedAt(identity.ProcessId)==identity.StartedAtUtcTicks;
        }
        public static HashSet<int> AppProcesses(Config c) {
            HashSet<int> ids=new HashSet<int>();
            foreach(string name in c.ProcessNames)foreach(Process p in Process.GetProcessesByName(name)) {
                try {
                    string path=ProcessPath(p.Id);
                    if(!String.IsNullOrEmpty(path)&&path.IndexOf(c.ProcessPathContains,StringComparison.OrdinalIgnoreCase)>=0)ids.Add(p.Id);
                } catch { } finally { p.Dispose(); }
            }
            return ids;
        }
        public static List<IntPtr> Windows(Config c) {
            HashSet<int> pids=AppProcesses(c); List<IntPtr> windows=new List<IntPtr>();
            EnumWindows(delegate(IntPtr h,IntPtr unused) {
                uint pid; GetWindowThreadProcessId(h,out pid);
                if(!pids.Contains((int)pid)||!IsWindowVisible(h)||GetWindow(h,4)!=IntPtr.Zero||(GetWindowLong(h,-20)&0x80)!=0)return true;
                int cloaked;
                if(DwmGetWindowAttribute(h,14,out cloaked,4)==0&&cloaked!=0)return true;
                // Minimized Chromium windows can have zero client dimensions. They are
                // eligible for final restore, but never count as render-ready until restored.
                Bounds b=ClientBounds(h);
                if(!IsIconic(h)&&(b.Width<450||b.Height<280))return true;
                windows.Add(h); return true;
            },IntPtr.Zero);
            return windows;
        }
        public static IntPtr Find(Config c) {
            List<IntPtr> windows=Windows(c);
            return windows.Count>0?windows[0]:IntPtr.Zero;
        }
        public static List<PrimaryWindowInfo> ReusablePrimaryWindows(Config c) {
            HashSet<int> pids=AppProcesses(c);List<PrimaryWindowInfo> windows=new List<PrimaryWindowInfo>();
            EnumWindows(delegate(IntPtr h,IntPtr unused) {
                uint pid;GetWindowThreadProcessId(h,out pid);
                if(!pids.Contains((int)pid))return true;
                WindowPlacement placement=new WindowPlacement { Length=Marshal.SizeOf(typeof(WindowPlacement)) };
                Bounds bounds;
                if(GetWindowPlacement(h,ref placement))bounds=placement.NormalPosition;
                else if(!GetWindowRect(h,out bounds))return true;
                int cloaked;
                PrimaryWindowInfo window=new PrimaryWindowInfo { Handle=h,ProcessId=pid,
                    Style=GetWindowLong(h,-16),ExtendedStyle=GetWindowLong(h,-20),Visible=IsWindowVisible(h),
                    Minimized=IsIconic(h),Maximized=IsZoomed(h),RestoreMaximized=(placement.Flags&2)!=0,Owned=GetWindow(h,4)!=IntPtr.Zero,
                    Cloaked=DwmGetWindowAttribute(h,14,out cloaked,4)==0&&cloaked!=0,
                    Responsive=Responsive(h),RestoredBounds=bounds };
                if(PrimaryWindowPolicy.Eligible(window))windows.Add(window);
                return true;
            },IntPtr.Zero);
            return windows;
        }
        public static PrimaryWindowInfo FindReusablePrimary(Config c) { return PrimaryWindowPolicy.Best(ReusablePrimaryWindows(c)); }
        public static PrimaryWindowInfo FindTrustedPrimary(Config c,PrimaryIdentity identity) {
            if(!IdentityOwnerAlive(c,identity))return null;
            foreach(PrimaryWindowInfo window in ReusablePrimaryWindows(c)) {
                if(PrimaryWindowPolicy.IdentityMatches(identity,window,ProcessStartedAt((int)window.ProcessId),GetProp(window.Handle,PrimaryIdentityProperty).ToInt64(),true))return window;
            }
            return null;
        }
        public static bool ConfirmReuse(Config c,PrimaryWindowInfo window,PrimaryIdentity identity) {
            // Official second-instance activation is asynchronous. Wait for its
            // show/restore lifecycle and then confirm the same identity survives.
            Stopwatch elapsed=Stopwatch.StartNew();double visibleSince=-1;
            do {
                long token;
                bool matches=identity!=null&&Int64.TryParse(identity.Token,out token)&&token>0&&GetProp(window.Handle,PrimaryIdentityProperty).ToInt64()==token;
                if(!IsWindow(window.Handle)||!IdentityOwnerAlive(c,identity)||!matches)return false;
                bool shown=PrimaryWindowPolicy.ReuseSurvived(true,true,true,IsWindowVisible(window.Handle),IsIconic(window.Handle));
                if(!shown)visibleSince=-1;
                else if(visibleSince<0)visibleSince=elapsed.Elapsed.TotalMilliseconds;
                else if(elapsed.Elapsed.TotalMilliseconds-visibleSince>=400)return true;
                if(elapsed.Elapsed.TotalSeconds>=5)return false;
                Thread.Sleep(40);
            } while(true);
        }
        public static bool ActivateOpeningWindow(IntPtr h) {
            if(h==IntPtr.Zero||!IsWindow(h))return false;
            WindowPlacement placement=new WindowPlacement { Length=Marshal.SizeOf(typeof(WindowPlacement)) };
            bool restoreMaximized=IsIconic(h)&&GetWindowPlacement(h,ref placement)&&(placement.Flags&2)!=0;
            // SW_SHOW preserves a hidden window's current placement; minimized
            // windows restore their previous normal/maximized state explicitly.
            ShowWindowAsync(h,IsIconic(h)?(restoreMaximized?3:9):5);
            for(int i=0;i<10&&IsWindow(h)&&(!IsWindowVisible(h)||IsIconic(h));i++)Thread.Sleep(40);
            return IsWindow(h)&&IsWindowVisible(h)&&(GetForegroundWindow()==h||SetForegroundWindow(h));
        }
        public static IntPtr FindExistingOpening(string executable) {
            int current;using(Process process=Process.GetCurrentProcess())current=process.Id;
            IntPtr found=IntPtr.Zero;
            EnumWindows(delegate(IntPtr h,IntPtr unused) {
                uint pid;GetWindowThreadProcessId(h,out pid);
                if(pid==(uint)current||!IsWindowVisible(h)||GetWindow(h,4)!=IntPtr.Zero)return true;
                if(!String.Equals(ProcessPath((int)pid),executable,StringComparison.OrdinalIgnoreCase))return true;
                found=h;return false;
            },IntPtr.Zero);
            return found;
        }
    }

    internal sealed class Petal {
        public System.Windows.Shapes.Path Shape;
        public double X,Y,Speed,Phase,Size;
        public RotateTransform Rotation;
    }

    // A deterministic vector overlay animates an independently generated bitmap.
    // There are no screenshot APIs, image manipulation routines or browser dependencies.
    internal sealed class OpeningScene : Canvas {
        private readonly Image artwork;
        private readonly ScaleTransform artScale=new ScaleTransform(1,1);
        private readonly TranslateTransform artMove=new TranslateTransform();
        private readonly Canvas typography=new Canvas();
        private readonly Canvas petals=new Canvas();
        private readonly List<Petal> petalList=new List<Petal>();
        private readonly Rectangle progress;
        private readonly TextBlock status;
        private readonly TextBlock title;
        private readonly TextBlock signature;
        private readonly RotateTransform ringRotate=new RotateTransform(0,230,230);
        private readonly Canvas ring=new Canvas();
        public event Action SkipRequested;
        private readonly double duration;
        private string failureMessage;

        private static Brush Solid(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static TextBlock Text(string value,double x,double y,double size,string color,string font) {
            TextBlock t=new TextBlock { Text=value,FontSize=size,Foreground=Solid(color),FontFamily=new FontFamily(font) };
            SetLeft(t,x); SetTop(t,y); return t;
        }
        public OpeningScene(string artworkPath,Config c) {
            Width=1920; Height=1080; ClipToBounds=true; Background=Solid("#132F38"); duration=c.DurationSeconds;
            BitmapImage bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.UriSource=new Uri(artworkPath,UriKind.Absolute); bitmap.EndInit(); bitmap.Freeze();
            artwork=new Image { Source=bitmap, Width=1920,Height=1080,Stretch=Stretch.UniformToFill,RenderTransformOrigin=new Point(.42,.48) };
            TransformGroup artTransforms=new TransformGroup(); artTransforms.Children.Add(artScale); artTransforms.Children.Add(artMove);
            artwork.RenderTransform=artTransforms; Children.Add(artwork);
            Rectangle shade=new Rectangle { Width=1920,Height=1080,Fill=new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(0,16,43,47),0),new GradientStop(Color.FromArgb(12,16,43,47),.4),
                new GradientStop(Color.FromArgb(150,16,43,47),.68),new GradientStop(Color.FromArgb(205,16,43,47),1)
            },new Point(0,0),new Point(1,0)) };
            Children.Add(shade);
            Rectangle edgeShade=new Rectangle { Width=1920,Height=1080,Fill=new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb(80,9,27,31),0),new GradientStop(Color.FromArgb(0,9,27,31),.25),
                new GradientStop(Color.FromArgb(0,9,27,31),.65),new GradientStop(Color.FromArgb(120,9,27,31),1)
            },new Point(0,0),new Point(0,1)) };
            Children.Add(edgeShade);

            ring.Width=460; ring.Height=460; SetLeft(ring,1255); SetTop(ring,251); ring.RenderTransform=ringRotate;
            Ellipse outer=new Ellipse { Width=460,Height=460,Stroke=Solid("#FFE3AF"),StrokeThickness=1.1,Opacity=.24,StrokeDashArray=new DoubleCollection { 7,19 } };
            ring.Children.Add(outer);
            Ellipse inner=new Ellipse { Width=426,Height=426,Stroke=Solid("#FFE3AF"),StrokeThickness=.7,Opacity=.14 };
            SetLeft(inner,17);SetTop(inner,17);ring.Children.Add(inner);
            for(int i=0;i<4;i++) {
                Rectangle mark=new Rectangle { Width=7,Height=7,Fill=Solid("#FFE3AF"),Opacity=.45,RenderTransform=new RotateTransform(45,3.5,3.5) };
                double a=i*Math.PI/2;SetLeft(mark,226.5+230*Math.Cos(a));SetTop(mark,226.5+230*Math.Sin(a));ring.Children.Add(mark);
            }
            Children.Add(ring);

            TextBlock top=Text("F O X   S P I R I T   M A T C H M A K E R",1120,282,18,"#EACDA7","Segoe UI");
            typography.Children.Add(top);
            TextBlock series=Text("狐妖小红娘",1120,322,27,"#F4D4C3","Microsoft YaHei UI"); typography.Children.Add(series);
            title=Text("缘起涂山",1108,374,118,"#FFF5E5","KaiTi");
            title.Effect=new DropShadowEffect { Color=Color.FromRgb(13,31,35),BlurRadius=30,ShadowDepth=2,Opacity=.4 };
            typography.Children.Add(title);
            Rectangle accent=new Rectangle { Width=48,Height=3,Fill=Solid("#EFA788") }; SetLeft(accent,1120);SetTop(accent,542); typography.Children.Add(accent);
            TextBlock subtitle=Text("涂山苏苏 · 陪你开启新的灵感",1120,574,27,"#F1D7C5","Microsoft YaHei UI"); typography.Children.Add(subtitle);
            signature=Text("红线轻牵，灵感相逢。",1120,624,22,"#BCBFB4","Microsoft YaHei UI"); typography.Children.Add(signature);
            Children.Add(typography);

            Random random=new Random(7291);
            for(int i=0;i<26;i++) {
                double size=8+random.NextDouble()*15;
                System.Windows.Shapes.Path shape=new System.Windows.Shapes.Path {
                    Data=System.Windows.Media.Geometry.Parse("M 0,-10 C 13,-13 17,0 2,11 C -10,9 -11,-2 0,-10 Z"),
                    Fill=Solid(i%3==0?"#FFF1D9":"#F6B9BA"),Opacity=.28+random.NextDouble()*.42,
                    RenderTransformOrigin=new Point(.5,.5)
                };
                TransformGroup transform=new TransformGroup();transform.Children.Add(new ScaleTransform(size/20,size/20));
                RotateTransform rotation=new RotateTransform(); transform.Children.Add(rotation); shape.RenderTransform=transform;
                Petal p=new Petal { Shape=shape,X=random.NextDouble()*2100,Y=random.NextDouble()*1350,Speed=20+random.NextDouble()*42,
                    Phase=random.NextDouble()*Math.PI*2,Size=size,Rotation=rotation };
                petalList.Add(p);petals.Children.Add(shape);
            }
            petals.IsHitTestVisible=false; Children.Add(petals);
            TextBlock codex=Text("C O D E X",1120,848,25,"#FFF0DA","Segoe UI Semibold");Children.Add(codex);
            status=Text("正在结缘新的灵感",1120,892,20,"#CCD5CC","Microsoft YaHei UI");Children.Add(status);
            Rectangle progressTrack=new Rectangle { Width=550,Height=2,Fill=Solid("#446468") };SetLeft(progressTrack,1120);SetTop(progressTrack,936);Children.Add(progressTrack);
            progress=new Rectangle { Width=0,Height=2,Fill=Solid("#F0C39E") };SetLeft(progress,1120);SetTop(progress,936);Children.Add(progress);
            TextBlock corner=Text("涂山苏苏 · 主题开场",75,989,18,"#D5D8CB","Microsoft YaHei UI");corner.Opacity=.6;Children.Add(corner);

            Border skip=new Border { Width=143,Height=43,CornerRadius=new CornerRadius(22),Background=Solid("#263B4F50"),
                BorderBrush=Solid("#6CDAE2D5"),BorderThickness=new Thickness(1),Cursor=Cursors.Hand };
            skip.Child=new TextBlock { Text="跳过  Esc",FontSize=18,Foreground=Solid("#E4E9DF"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
            SetLeft(skip,1703);SetTop(skip,48);skip.MouseLeftButtonUp+=delegate { if(SkipRequested!=null)SkipRequested(); };Children.Add(skip);
            SetTime(0,false);
        }
        public void SetTime(double t,bool waiting) {
            double p=Geometry.Clamp(t/duration);
            artwork.Opacity=.55+.45*Geometry.Ease(t/.75);
            artScale.ScaleX=artScale.ScaleY=1.025+.035*p;
            artMove.X=-7*Math.Sin(t*.22); artMove.Y=3*Math.Sin(t*.28);
            typography.Opacity=Geometry.Ease((t-.45)/1.2);
            typography.RenderTransform=new TranslateTransform(22*(1-Geometry.Ease((t-.45)/1.2)),0);
            ring.Opacity=.65*Geometry.Ease((t-.3)/1.5);ringRotate.Angle=t*1.8;
            signature.Opacity=.8*Geometry.Ease((t-1.8)/1.1);
            progress.Width=550*Geometry.Ease(p);
            status.Text=failureMessage??(waiting?"正在准备 Codex 界面与苏苏主题…":"正在结缘新的灵感");
            foreach(Petal petal in petalList) {
                double x=((petal.X-t*petal.Speed*.55+2400)%2400)-120+28*Math.Sin(t*.5+petal.Phase);
                double y=((petal.Y+t*petal.Speed+1600)%1400)-140;
                SetLeft(petal.Shape,x);SetTop(petal.Shape,y);
                petal.Rotation.Angle=(t*18+petal.Phase*100)%360;
            }
        }
        public void SetTransition(double p) {
            // The DWM surface is composed above WPF, so this gradient-free darkening
            // makes its native contents easy to see as they expand across the art.
            Opacity=1-.38*Geometry.Ease(p);
        }
        public void SetFailure() { SetFailure("主题暂未启用，请查看主题运行日志"); }
        public void SetFailure(string message) { failureMessage=message; }
    }

    internal static class OpeningLayout {
        public static Grid Create(OpeningScene scene,string artworkPath) {
            Grid layout=new Grid { Background=new SolidColorBrush(Color.FromRgb(19,47,56)),ClipToBounds=true };
            BitmapImage bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.UriSource=new Uri(artworkPath,UriKind.Absolute);bitmap.EndInit();bitmap.Freeze();
            BlurEffect blur=new BlurEffect { Radius=28,RenderingBias=RenderingBias.Performance };blur.Freeze();
            Image backdrop=new Image { Source=bitmap,Stretch=Stretch.UniformToFill,Opacity=.4,Effect=blur,IsHitTestVisible=false,
                RenderTransformOrigin=new Point(.5,.5),RenderTransform=new ScaleTransform(1.08,1.08) };
            layout.Children.Add(backdrop);
            layout.Children.Add(new Viewbox { Stretch=Stretch.Uniform,Child=scene,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center });
            return layout;
        }
    }

    internal sealed class BootWindow : Window {
        private readonly Config cfg;
        private readonly string root;
        private readonly bool preview;
        private readonly bool maximizeCold;
        private readonly PrimaryIdentity previousIdentity;
        private bool waitingForPreviousExit;
        private readonly OpeningScene scene;
        private readonly Stopwatch wall=new Stopwatch();
        private readonly DispatcherTimer timer;
        private Bounds playerBounds;
        private IntPtr host,target,stable,thumbnail;
        private double stableAt,lastProbe=-1,transitionAt=-1,holdBegan=-1,pauseTotal,lastMaximize=-1;
        private bool ready,closing,thumbnailAttempted,launchFailed,layered,skipRequested;
        private Bounds originalTarget;
        private bool originalMaximized,geometryCaptured;
        private Process themeLaunch;
        private bool themeLaunchChecked,themeLaunchSucceeded;
        private readonly BootReadiness readiness;
        private HandoffState handoffState=HandoffState.Waiting;
        public BootWindow(Config c,string baseDir,bool previewOnly,PrimaryIdentity waitForIdentity) {
            cfg=c;root=baseDir;preview=previewOnly;
            maximizeCold=PrimaryWindowPolicy.MaximizeCold(preview,false,c.MaximizeColdStart);
            previousIdentity=waitForIdentity;waitingForPreviousExit=!preview&&waitForIdentity!=null;
            readiness=new BootReadiness(!String.IsNullOrWhiteSpace(c.ThemeRuntime),c.MaxWaitSeconds-c.TransitionSeconds);
            Title=c.DisplayName;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
            AllowsTransparency=false;Topmost=true;Background=Brushes.Black;WindowStartupLocation=WindowStartupLocation.Manual;
            target=waitingForPreviousExit?IntPtr.Zero:Native.Find(c);
            System.Windows.Forms.Screen screen=target!=IntPtr.Zero?System.Windows.Forms.Screen.FromHandle(target):System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            System.Drawing.Rectangle work=screen.WorkingArea;
            Bounds workBounds=new Bounds(work.Left,work.Top,work.Width,work.Height);
            playerBounds=maximizeCold?workBounds:c.WindowSizing=="MatchCodex"?Geometry.MatchTarget(workBounds,TargetBounds()):Geometry.Player(workBounds,c.PlayerWidth,c.PlayerHeight);
            Width=playerBounds.Width;Height=playerBounds.Height;
            scene=new OpeningScene(Entry.ArtworkPath(root,c),c);
            Content=OpeningLayout.Create(scene,Entry.ArtworkPath(root,c));scene.SkipRequested+=Skip;
            PreviewKeyDown+=delegate(object sender,KeyEventArgs e) { if(e.Key==Key.Escape) { e.Handled=true;Skip(); } };
            SourceInitialized+=delegate {
                host=new WindowInteropHelper(this).Handle;
                // Position only the animation host here. Cold primary windows are
                // maximized later through ShowWindow; warm reuse keeps placement.
                Native.SetWindowPos(host,new IntPtr(-1),playerBounds.Left,playerBounds.Top,playerBounds.Width,playerBounds.Height,0x10);
            };
            timer=new DispatcherTimer(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(16) };timer.Tick+=Tick;
            Loaded+=delegate {
                wall.Start();
                if(!preview) {
                    if(waitingForPreviousExit)Entry.Log(root,"waiting-for-previous-exit pid="+previousIdentity.ProcessId);
                    else if(target==IntPtr.Zero||!String.IsNullOrWhiteSpace(cfg.ThemeRuntime))LaunchApp();
                    if(!maximizeCold&&!waitingForPreviousExit&&target!=IntPtr.Zero&&Native.IsIconic(target)) {
                        // SW_RESTORE only for a minimized window. Maximized windows
                        // keep their maximized state and all existing bounds.
                        Native.ShowWindowAsync(target,9);
                    }
                }
                timer.Start();Entry.Log(root,(preview?"preview-start":"start")+" player="+playerBounds+" actual="+Native.ClientBounds(host)+" sizing="+c.WindowSizing);
            };
            Closed+=delegate { timer.Stop();DropThumbnail();if(themeLaunch!=null)themeLaunch.Dispose(); };
        }
        private Bounds TargetBounds() {
            return target!=IntPtr.Zero&&Native.IsWindow(target)&&!Native.IsIconic(target)?Native.ClientBounds(target):new Bounds();
        }
        private void SyncPlayerToTarget() {
            if(cfg.WindowSizing!="MatchCodex"||transitionAt>=0||host==IntPtr.Zero)return;
            if(maximizeCold&&!Native.IsZoomed(target))return;
            Bounds desired=TargetBounds();
            if(maximizeCold) {
                System.Drawing.Rectangle work=System.Windows.Forms.Screen.FromHandle(target).WorkingArea;
                desired=new Bounds(work.Left,work.Top,work.Width,work.Height);
            }
            if(!Geometry.ValidTarget(desired))return;
            Bounds actual=Native.ClientBounds(host);
            if(actual.Left==desired.Left&&actual.Top==desired.Top&&actual.Width==desired.Width&&actual.Height==desired.Height)return;
            if(Native.SetWindowPos(host,new IntPtr(-1),desired.Left,desired.Top,desired.Width,desired.Height,0x10)) {
                playerBounds=desired;
                Bounds measured=Native.ClientBounds(host);
                Entry.Log(root,"player-matched-to-codex desired="+desired+" actual="+measured+" exact="+
                    (measured.Left==desired.Left&&measured.Top==desired.Top&&measured.Width==desired.Width&&measured.Height==desired.Height));
            }
        }
        private void CaptureGeometry() {
            if(geometryCaptured||target==IntPtr.Zero||Native.IsIconic(target))return;
            geometryCaptured=Native.GetWindowRect(target,out originalTarget);
            originalMaximized=Native.IsZoomed(target);
            if(geometryCaptured)Entry.Log(root,"target-before bounds="+originalTarget+" maximized="+originalMaximized);
        }
        private void LaunchApp() {
            try {
                if(!String.IsNullOrWhiteSpace(cfg.ThemeRuntime)) {
                    string runtime=Entry.ThemePath(root,cfg.ThemeRuntime);
                    if(!File.Exists(runtime))throw new FileNotFoundException("未找到主题运行工具，请保留完整的 TushanSusuTheme 目录。",runtime);
                    string dataRoot=Entry.ThemePath(root,cfg.ThemeDataRoot);
                    string helper=IOPath.GetFullPath(IOPath.Combine(IOPath.GetDirectoryName(runtime),"..","Launch-Theme-Runtime.ps1"));
                    if(!File.Exists(helper))throw new FileNotFoundException("主题启动脚本缺失。",helper);
                    string powershell=IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
                    ProcessStartInfo info=new ProcessStartInfo(powershell,"-NoProfile -ExecutionPolicy Bypass -File \""+helper+"\" -RuntimeExe \""+runtime+"\" -DataRoot \""+dataRoot.TrimEnd('\\','/')+"\" -ThemeId "+cfg.ThemeId+" -ThemeVersion "+cfg.ThemeVersion) {
                        UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=IOPath.GetDirectoryName(helper)
                    };
                    // Windows PowerShell must build its own Desktop module path.
                    // An inherited PowerShell Core path can select incompatible
                    // versions of the system signing and CIM modules.
                    info.EnvironmentVariables.Remove("PSModulePath");
                    themeLaunch=new Process { StartInfo=info };
                    themeLaunch.Start();
                    Entry.Log(root,"theme-launch-requested "+cfg.ThemeId+"@"+cfg.ThemeVersion);return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe",cfg.AppLaunch) { UseShellExecute=true });
                Entry.Log(root,"app-launch-requested");
            } catch(Exception e) { launchFailed=true;scene.SetFailure();Entry.Log(root,"app-launch-failed "+e.Message); }
        }
        private void Probe() {
            double now=wall.Elapsed.TotalSeconds;
            if(now-lastProbe<.3)return;lastProbe=now;
            if(waitingForPreviousExit) {
                bool ownerAlive=Native.IdentityOwnerAlive(cfg,previousIdentity);
                if(!PrimaryWindowPolicy.LaunchAfterExit(true,ownerAlive)) {
                    handoffState=readiness.Evaluate(now,false,false,false,false);ready=false;
                    if(handoffState==HandoffState.TimedOut) { scene.SetFailure("Codex 仍在退出，请稍后重试");Entry.Log(root,"previous-exit-wait-timeout"); }
                    return;
                }
                waitingForPreviousExit=false;target=IntPtr.Zero;stable=IntPtr.Zero;geometryCaptured=false;
                Entry.Log(root,"previous-exit-complete-launching-codex");LaunchApp();
            }
            if(themeLaunch!=null&&!themeLaunchChecked&&themeLaunch.HasExited) {
                themeLaunchChecked=true;
                themeLaunchSucceeded=themeLaunch.ExitCode==0;
                Entry.Log(root,"theme-launch-exit-code "+themeLaunch.ExitCode);
                if(!themeLaunchSucceeded) {
                    launchFailed=true;
                    if(themeLaunch.ExitCode==2)scene.SetFailure("请完整退出 Codex，再从“Codex - 涂山苏苏”打开");
                    else scene.SetFailure();
                }
            }
            IntPtr candidate=Native.Find(cfg);
            bool windowStable=false;
            if(candidate==IntPtr.Zero||(!maximizeCold&&Native.IsIconic(candidate))||!Native.Responsive(candidate))stable=IntPtr.Zero;
            else {
                if(candidate!=target) { DropThumbnail();thumbnailAttempted=false;target=candidate;geometryCaptured=false; }
                bool maximized=Native.IsZoomed(candidate);
                if(!PrimaryWindowPolicy.ColdWindowReady(maximizeCold,maximized)) {
                    stable=IntPtr.Zero;
                    if(now-lastMaximize>=.6) { lastMaximize=now;Native.ShowWindowAsync(candidate,3);Entry.Log(root,"cold-target-maximize-requested"); }
                } else {
                    CaptureGeometry();
                    SyncPlayerToTarget();
                    if(candidate!=stable) { stable=candidate;stableAt=now; }
                    else windowStable=now-stableAt>=.55;
                }
            }
            handoffState=readiness.Evaluate(now,windowStable,themeLaunchChecked,themeLaunchSucceeded,launchFailed);
            ready=handoffState==HandoffState.Ready;
        }
        private void RegisterThumbnail() {
            if(thumbnailAttempted||target==IntPtr.Zero)return;thumbnailAttempted=true;
            int hr=Native.DwmRegisterThumbnail(host,target,out thumbnail);
            if(hr!=0) { thumbnail=IntPtr.Zero;Entry.Log(root,"dwm-unavailable hr="+hr); }
            else Entry.Log(root,"live-thumbnail-connected");
        }
        private void DropThumbnail() {
            if(thumbnail==IntPtr.Zero)return;
            Native.DwmUnregisterThumbnail(thumbnail);thumbnail=IntPtr.Zero;
        }
        private void HostAlpha(double alpha) {
            // Apply alpha only to our HWND. The source window is never made transparent.
            if(!layered) { Native.SetWindowLong(host,-20,Native.GetWindowLong(host,-20)|0x80000);layered=true; }
            Native.SetLayeredWindowAttributes(host,0,(byte)Math.Round(255*Geometry.Clamp(alpha)),2);
        }
        private void Tick(object sender,EventArgs e) {
            if(closing)return;
            double now=wall.Elapsed.TotalSeconds;
            if(preview) {
                scene.SetTime(Math.Min(now,cfg.DurationSeconds),false);
                if(now>cfg.DurationSeconds-.5)HostAlpha(1-Geometry.Ease((now-cfg.DurationSeconds+.5)/.5));
                if(now>=cfg.DurationSeconds)HandOff();return;
            }
            Probe();
            if(skipRequested) {
                // Skipping dismisses the artwork immediately but keeps this one
                // launch alive through previous-process exit, maximize and marking.
                if(handoffState!=HandoffState.Waiting)HandOff(true);
                return;
            }
            double sceneTime=now-pauseTotal;
            if(transitionAt<0&&sceneTime>=cfg.HoldAtSeconds&&handoffState==HandoffState.Waiting) {
                if(holdBegan<0) { holdBegan=now;Entry.Log(root,"waiting-for-window-and-theme"); }
                scene.SetTime(cfg.HoldAtSeconds+(now-holdBegan)*.15,true);
                return;
            }
            if(holdBegan>=0&&transitionAt<0) { pauseTotal+=now-holdBegan;holdBegan=-1;sceneTime=now-pauseTotal;Entry.Log(root,"handoff-readiness "+handoffState); }
            if(transitionAt<0&&handoffState==HandoffState.TimedOut) { Entry.Log(root,"window-and-theme-wait-timeout");BeginTransition(now); }
            scene.SetTime(Math.Min(sceneTime,cfg.DurationSeconds),false);
            if(transitionAt<0&&sceneTime>=cfg.DurationSeconds-cfg.TransitionSeconds)BeginTransition(now);
            if(transitionAt<0)return;
            double progress=Geometry.Clamp((now-transitionAt)/cfg.TransitionSeconds);
            scene.SetTransition(progress);
            if(ready&&target!=IntPtr.Zero&&!Native.IsIconic(target)) {
                RegisterThumbnail();
                if(thumbnail!=IntPtr.Zero) {
                    Bounds source=Native.ClientBounds(target),hostRect=Native.ClientBounds(host);
                    if(source.Width>0&&source.Height>0&&hostRect.Width>0&&hostRect.Height>0) {
                        Native.ThumbProperties props=new Native.ThumbProperties { Flags=1|4|8|16,
                            Destination=Geometry.Thumbnail(source.Width,source.Height,hostRect.Width,hostRect.Height,progress),
                            Opacity=(byte)Math.Round(255*Geometry.Ease(progress/.65)),Visible=true,ClientOnly=true };
                        int hr=Native.DwmUpdateThumbnailProperties(thumbnail,ref props);
                        if(hr!=0) { DropThumbnail();Entry.Log(root,"dwm-update-failed hr="+hr); }
                    }
                }
            }
            // Last 0.4 seconds dissolve the player into the true desktop window.
            // If composition is unavailable this is the complete transition.
            if(thumbnail==IntPtr.Zero)HostAlpha(1-Geometry.Ease(progress));
            else if(progress>.72)HostAlpha(1-Geometry.Ease((progress-.72)/.28));
            if(progress>=1)HandOff();
        }
        private void BeginTransition(double now) { if(transitionAt<0)transitionAt=now; }
        private void Skip() {
            if(preview) { HandOff(true);return; }
            if(skipRequested||closing)return;
            skipRequested=true;Hide();Entry.Log(root,"opening-skipped-launch-continues");
        }
        private void HandOff() { HandOff(false); }
        private void HandOff(bool manualSkip) {
            if(closing)return;
            IntPtr app=preview||waitingForPreviousExit?IntPtr.Zero:Native.Find(cfg);
            if(!manualSkip&&maximizeCold&&app!=IntPtr.Zero&&themeLaunchSucceeded&&!Native.IsZoomed(app)&&wall.Elapsed.TotalSeconds<cfg.MaxWaitSeconds) {
                if(wall.Elapsed.TotalSeconds-lastMaximize>=.6) { lastMaximize=wall.Elapsed.TotalSeconds;Native.ShowWindowAsync(app,3);Entry.Log(root,"cold-handoff-waiting-for-maximized-target"); }
                return;
            }
            closing=true;timer.Stop();DropThumbnail();
            if(app!=IntPtr.Zero) {
                if(Native.IsIconic(app))Native.ShowWindowAsync(app,9);
                Bounds after;
                if(geometryCaptured&&app==target&&Native.GetWindowRect(app,out after))Entry.Log(root,
                    "target-after bounds="+after+" maximized="+Native.IsZoomed(app)+" geometryPreserved="+
                    (after.Left==originalTarget.Left&&after.Top==originalTarget.Top&&after.Right==originalTarget.Right&&after.Bottom==originalTarget.Bottom&&Native.IsZoomed(app)==originalMaximized));
                if(!preview&&ready)Entry.RememberPrimary(root,cfg,app);
                Close();Native.SetForegroundWindow(app);Entry.Log(root,"handed-off-to-real-window");
            } else { Close();Entry.Log(root,preview?"preview-closed":"closed-without-window"); }
        }
    }

    public static class Entry {
        private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
        public static string ArtworkPath(string root,Config c) {
            string path=IOPath.GetFullPath(IOPath.Combine(root,c.Artwork));
            string prefix=IOPath.GetFullPath(root).TrimEnd('\\')+"\\";
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Artwork 必须位于启动器目录内。");
            return path;
        }
        public static string ThemePath(string root,string relative) {
            if(String.IsNullOrWhiteSpace(relative))throw new ArgumentException("主题路径配置不完整。");
            string path=IOPath.GetFullPath(IOPath.Combine(root,relative));
            string allowed=IOPath.GetFullPath(IOPath.Combine(root,"..")).TrimEnd('\\','/')+"\\";
            if(!path.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("主题路径必须位于相邻主题目录内。");
            return path;
        }
        public static Config ReadConfig(string root) {
            Config c=Json.Deserialize<Config>(File.ReadAllText(IOPath.Combine(root,"launcher.json"),Encoding.UTF8));ValidateConfig(c);return c;
        }
        public static void ValidateConfig(Config c) {
            if(c==null||String.IsNullOrWhiteSpace(c.Artwork)||String.IsNullOrWhiteSpace(c.AppLaunch)||String.IsNullOrWhiteSpace(c.ProcessPathContains)
                ||c.ProcessNames==null||c.ProcessNames.Length==0)throw new ArgumentException("启动器配置不完整。");
            const string prefix="shell:AppsFolder\\";
            if(!c.AppLaunch.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(c.AppLaunch.Substring(prefix.Length),@"^[A-Za-z0-9_.-]+![A-Za-z0-9_.-]+$"))throw new ArgumentException("AppLaunch 必须是已安装应用的 AppsFolder 标识。");
            if(c.ProcessNames.Any(n=>String.IsNullOrWhiteSpace(n)||!Regex.IsMatch(n,@"^[A-Za-z0-9_.-]+$")))throw new ArgumentException("进程匹配无效。");
            if(c.AutoReplaceEntrypoints)throw new ArgumentException("本启动器不自动替换快捷方式，请将 AutoReplaceEntrypoints 设为 false。");
            if(c.WindowSizing!="MatchCodex"&&c.WindowSizing!="Fixed")throw new ArgumentException("WindowSizing 必须为 MatchCodex 或 Fixed。");
            if(!String.IsNullOrWhiteSpace(c.ThemeRuntime)) {
                if(!String.Equals(IOPath.GetFileName(c.ThemeRuntime),"OpenChatGPTSkin.exe",StringComparison.OrdinalIgnoreCase)
                    ||String.IsNullOrWhiteSpace(c.ThemeDataRoot)||String.IsNullOrWhiteSpace(c.ThemeId)||!Regex.IsMatch(c.ThemeId,@"^[a-z0-9]+(?:-[a-z0-9]+)*$")
                    ||String.IsNullOrWhiteSpace(c.ThemeVersion)||!Regex.IsMatch(c.ThemeVersion,@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"))
                    throw new ArgumentException("主题启动配置无效。");
            }
            if(!Geometry.Finite(c.DurationSeconds)||!Geometry.Finite(c.HoldAtSeconds)||!Geometry.Finite(c.TransitionSeconds)||!Geometry.Finite(c.MaxWaitSeconds)
                ||c.DurationSeconds<3||c.DurationSeconds>30||c.TransitionSeconds<.2||c.TransitionSeconds>3
                ||c.HoldAtSeconds<1||c.HoldAtSeconds>=c.DurationSeconds-c.TransitionSeconds||c.MaxWaitSeconds<c.DurationSeconds||c.MaxWaitSeconds>120)
                throw new ArgumentException("动画或等待时长无效（最长等待 120 秒）。");
            if(c.WindowSizing=="Fixed"&&(c.PlayerWidth<640||c.PlayerWidth>1280||c.PlayerHeight<360||c.PlayerHeight>720||Math.Abs((double)c.PlayerWidth/c.PlayerHeight-16.0/9)>.003))
                throw new ArgumentException("播放窗口需为 16:9，最大 1280×720。");
        }
        public static void Log(string root,string message) {
            try { string dir=IOPath.Combine(root,"logs");Directory.CreateDirectory(dir);
                File.AppendAllText(IOPath.Combine(dir,"launcher.log"),DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture)+" "+message+Environment.NewLine,Encoding.UTF8);
            } catch { }
        }
        private static void SaveJson(string path,object value) {
            Directory.CreateDirectory(IOPath.GetDirectoryName(IOPath.GetFullPath(path)));File.WriteAllText(path,Json.Serialize(value),new UTF8Encoding(false));
        }
        internal static PrimaryIdentity ReadPrimaryIdentity(string root) {
            try {
                string path=IOPath.Combine(root,"state","primary-window.json");
                return File.Exists(path)?Json.Deserialize<PrimaryIdentity>(File.ReadAllText(path,Encoding.UTF8)):null;
            } catch(Exception e) { Log(root,"primary-identity-read-failed "+e.Message);return null; }
        }
        internal static PrimaryIdentity RememberPrimary(string root,Config c,IntPtr h) {
            try {
                PrimaryWindowInfo window=Native.ReusablePrimaryWindows(c).FirstOrDefault(w=>w.Handle==h&&w.Visible);
                if(window==null)return null;
                string startedAt=Native.ProcessStartedAt((int)window.ProcessId);
                if(String.IsNullOrEmpty(startedAt))return null;
                long token=BitConverter.ToInt32(Guid.NewGuid().ToByteArray(),0)&Int32.MaxValue;
                if(token==0)token=1;
                if(!Native.SetProp(h,Native.PrimaryIdentityProperty,new IntPtr(token)))throw new InvalidOperationException("Cannot mark the confirmed primary window.");
                PrimaryIdentity identity=new PrimaryIdentity {Handle=h.ToInt64().ToString(CultureInfo.InvariantCulture),ProcessId=(int)window.ProcessId,
                    StartedAtUtcTicks=startedAt,Token=token.ToString(CultureInfo.InvariantCulture)};
                SaveJson(IOPath.Combine(root,"state","primary-window.json"),identity);
                Log(root,"primary-identity-recorded handle=0x"+h.ToInt64().ToString("X")+" pid="+window.ProcessId);
                return identity;
            } catch(Exception e) { Log(root,"primary-identity-save-failed "+e.Message);return null; }
        }
        private static void Assert(bool condition,string name) { if(!condition)throw new Exception("Self-test failed: "+name); }
        private static int SelfTest(string root) {
            Config c=ReadConfig(root);int placements=0,thumbnails=0,matchedPlacements=0,readinessCases=0,reuseCases=0,activationPolicyCases=0;
            Func<PrimaryWindowInfo> primary=delegate { return new PrimaryWindowInfo { Handle=new IntPtr(1),
                Style=0x30000,Visible=true,Responsive=true,RestoredBounds=new Bounds(20,30,1200,800) }; };
            Action<bool,string> checkReuse=delegate(bool condition,string name) { Assert(condition,name);reuseCases++; };
            PrimaryWindowInfo candidate=primary();
            checkReuse(PrimaryWindowPolicy.Eligible(candidate),"visible primary is reusable");
            candidate.Visible=false;checkReuse(PrimaryWindowPolicy.Eligible(candidate),"hidden tray primary is reusable");
            candidate=primary();candidate.Minimized=true;checkReuse(PrimaryWindowPolicy.Eligible(candidate),"minimized primary uses valid restored bounds");
            candidate.Visible=false;checkReuse(PrimaryWindowPolicy.Eligible(candidate),"hidden minimized primary is reusable");
            candidate=primary();candidate.Owned=true;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"owned popup cannot be reused as primary");
            candidate=primary();candidate.ExtendedStyle=0x80;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"tool window cannot be reused as primary");
            candidate=primary();candidate.Style=0;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"Mini lacks primary caption controls");
            candidate=primary();candidate.Style=0x10000;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"window without minimize control is excluded");
            candidate=primary();candidate.Style=0x20000;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"window without maximize control is excluded");
            candidate=primary();candidate.RestoredBounds=new Bounds(0,0,330,141);checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"small overlay bounds are excluded");
            candidate=primary();candidate.Responsive=false;checkReuse(PrimaryWindowPolicy.Eligible(candidate),"background response delay does not block primary reuse");
            candidate=primary();candidate.Cloaked=true;checkReuse(!PrimaryWindowPolicy.Eligible(candidate),"cloaked window is excluded");
            candidate=primary();checkReuse(PrimaryWindowPolicy.Mode(true,candidate,true,false)==ReuseMode.None,"preview does not reuse a primary window");
            checkReuse(PrimaryWindowPolicy.Mode(false,candidate,true,false)==ReuseMode.OfficialLifecycle,"visible primary is restored through the official lifecycle");
            checkReuse(PrimaryWindowPolicy.Mode(false,null,true,false)==ReuseMode.None,"cold launch without target continues to opening");
            checkReuse(PrimaryWindowPolicy.Mode(false,candidate,false,false)==ReuseMode.None,"exited official owner cannot trigger reuse");
            PrimaryWindowInfo hidden=primary();hidden.Visible=false;hidden.RestoredBounds=new Bounds(0,0,2560,1440);
            checkReuse(PrimaryWindowPolicy.Best(new PrimaryWindowInfo[] {hidden,candidate})==candidate,"visible primary is preferred over hidden secondary");
            candidate.Minimized=true;
            checkReuse(PrimaryWindowPolicy.Best(new PrimaryWindowInfo[] {hidden,candidate})==candidate,"visible minimized primary outranks larger hidden auxiliary");
            checkReuse(PrimaryWindowPolicy.Mode(false,candidate,true,false)==ReuseMode.OfficialLifecycle,"minimized primary is restored through the official lifecycle");
            checkReuse(PrimaryWindowPolicy.Mode(false,hidden,true,false)==ReuseMode.None,"unknown hidden auxiliary never triggers ordinary activation");
            checkReuse(PrimaryWindowPolicy.Mode(false,hidden,true,true)==ReuseMode.OfficialLifecycle,"marked tray primary is restored through the official lifecycle");
            checkReuse(PrimaryWindowPolicy.Mode(false,hidden,false,true)==ReuseMode.None,"hidden candidate with exited owner cannot activate official app");
            checkReuse(PrimaryWindowPolicy.Mode(true,hidden,true,true)==ReuseMode.None,"preview never activates tray-only app");
            candidate.Minimized=false;
            PrimaryWindowInfo larger=primary();larger.RestoredBounds=new Bounds(0,0,1920,1080);
            checkReuse(PrimaryWindowPolicy.Best(new PrimaryWindowInfo[] {candidate,larger})==larger,"largest equivalent primary is preferred");
            candidate.Style=0;checkReuse(PrimaryWindowPolicy.Best(new PrimaryWindowInfo[] {candidate})==null,"Mini-only set continues to cold opening");
            PrimaryWindowInfo marked=primary();marked.ProcessId=42;marked.Visible=false;
            PrimaryIdentity saved=new PrimaryIdentity {Handle="1",ProcessId=42,StartedAtUtcTicks="123456",Token="73"};
            checkReuse(PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",73,true),"exact window process timestamp and token identity is trusted");
            checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123457",73,true),"reused PID with new start time is rejected");
            checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",74,true),"recycled HWND with different property token is rejected");
            checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",0,true),"destroyed window loses property token and trust");
            checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",73,false),"exited official process identity is rejected");
            marked.ProcessId=43;checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",73,true),"different window PID is rejected");
            marked.ProcessId=42;marked.Handle=new IntPtr(2);checkReuse(!PrimaryWindowPolicy.IdentityMatches(saved,marked,"123456",73,true),"different HWND is rejected");
            checkReuse(PrimaryWindowPolicy.WaitForPreviousExit(saved,true,false),"live previous owner with lost primary waits for natural exit");
            checkReuse(!PrimaryWindowPolicy.LaunchAfterExit(true,true),"previous live process prevents managed cold activation");
            checkReuse(PrimaryWindowPolicy.LaunchAfterExit(true,false),"same cold request continues after previous process exits");
            checkReuse(!PrimaryWindowPolicy.WaitForPreviousExit(saved,false,false),"already exited previous owner needs no wait");
            checkReuse(!PrimaryWindowPolicy.WaitForPreviousExit(saved,true,true),"trusted existing primary does not enter exit wait");
            checkReuse(PrimaryWindowPolicy.ReuseSurvived(true,true,true,true,false),"same marked visible window survives warm confirmation");
            checkReuse(!PrimaryWindowPolicy.ReuseSurvived(false,true,true,true,false),"window destruction during warm confirmation continues request");
            checkReuse(!PrimaryWindowPolicy.ReuseSurvived(true,false,true,true,false),"owner exit during warm confirmation continues request");
            checkReuse(PrimaryWindowPolicy.MaximizeCold(false,false,true),"configured cold launch maximizes");
            checkReuse(!PrimaryWindowPolicy.MaximizeCold(false,true,true),"warm reuse preserves its current window placement");
            checkReuse(!PrimaryWindowPolicy.MaximizeCold(true,false,true),"preview never maximizes the app");
            checkReuse(!PrimaryWindowPolicy.ColdWindowReady(true,false),"cold window is not ready before maximized confirmation");
            checkReuse(PrimaryWindowPolicy.ColdWindowReady(true,true),"maximized cold window can become ready");
            checkReuse(PrimaryWindowPolicy.ColdWindowReady(false,false),"warm normal-size window remains valid");
            Action<bool,string> checkActivation=delegate(bool condition,string name) { Assert(condition,name);activationPolicyCases++; };
            int parsedPort;
            checkActivation(OfficialWarmActivation.TryParseEndpoint("\"C:\\Program Files\\WindowsApps\\Codex\\ChatGPT.exe\" --remote-debugging-address=127.0.0.1 --remote-debugging-port=58386",out parsedPort)&&parsedPort==58386,"quoted executable and managed endpoint parse");
            checkActivation(OfficialWarmActivation.TryParseEndpoint("ChatGPT.exe --remote-debugging-address \"127.0.0.1\" --remote-debugging-port \"1024\"",out parsedPort)&&parsedPort==1024,"separate quoted endpoint values parse");
            checkActivation(OfficialWarmActivation.TryParseEndpoint("ChatGPT.exe --remote-debugging-port=65535",out parsedPort)&&parsedPort==65535,"default bind requires separately verified loopback owner");
            foreach(string command in new string[] {
                "ChatGPT.exe", "ChatGPT.exe --remote-debugging-port=0", "ChatGPT.exe --remote-debugging-port=65536",
                "ChatGPT.exe --remote-debugging-port=-1", "ChatGPT.exe --remote-debugging-port=58386abc",
                "ChatGPT.exe --remote-debugging-port=58386 --remote-debugging-port=58387",
                "ChatGPT.exe --remote-debugging-port=58386 --remote-debugging-address=0.0.0.0",
                "ChatGPT.exe --remote-debugging-port=58386 --remote-debugging-address=127.0.0.1 --remote-debugging-address=127.0.0.1",
                "ChatGPT.exe --remote-debugging-port=58386 --type=renderer", "ChatGPT.exe --remote-debugging-port=58386 --type gpu-process",
                "ChatGPT.exe --remote-debugging-port=58386 --remote-debugging-pipe", "ChatGPT.exe --remote-debugging-port=58386 --remote-debugging-pipe=yes",
                "ChatGPT.exe --remote-debugging-port=\"58386", "ChatGPT.exe -- --remote-debugging-port=58386" }) {
                checkActivation(!OfficialWarmActivation.TryParseEndpoint(command,out parsedPort),"invalid or ambiguous managed endpoint is rejected");
            }
            checkActivation(OfficialWarmActivation.IsOfficialRootPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),"official installed root path is accepted");
            checkActivation(!OfficialWarmActivation.IsOfficialRootPath(@"C:\Users\test\OpenAI.Codex_26.1013.4894.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),"lookalike unofficial root path is rejected");
            checkActivation(!OfficialWarmActivation.IsOfficialRootPath(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1013.4894.0_x64__other\app\ChatGPT.exe"),"wrong package publisher suffix is rejected");
            OfficialWarmActivation.VerifiedListener owned=new OfficialWarmActivation.VerifiedListener { Address="127.0.0.1",Port=58386,ProcessId=42 };
            checkActivation(OfficialWarmActivation.OwnsExclusiveLoopback(new[] {owned},42,58386),"live exact process owns loopback activation port");
            checkActivation(!OfficialWarmActivation.OwnsExclusiveLoopback(new OfficialWarmActivation.VerifiedListener[0],42,58386),"missing listener cannot authorize activation");
            checkActivation(!OfficialWarmActivation.OwnsExclusiveLoopback(new[] {owned},43,58386),"foreign process cannot authorize activation");
            checkActivation(!OfficialWarmActivation.OwnsExclusiveLoopback(new[] {owned,new OfficialWarmActivation.VerifiedListener {Address="0.0.0.0",Port=58386,ProcessId=42}},42,58386),"non-loopback listener rejects activation");
            checkActivation(!OfficialWarmActivation.OwnsExclusiveLoopback(new[] {owned,new OfficialWarmActivation.VerifiedListener {Address="127.0.0.1",Port=58386,ProcessId=43}},42,58386),"mixed-owner listener rejects activation");
            checkActivation(OfficialWarmActivation.OwnsExclusiveLoopback(new[] {owned,new OfficialWarmActivation.VerifiedListener {Address="0.0.0.0",Port=58387,ProcessId=43}},42,58386),"unrelated port does not affect exact endpoint ownership");
            Action<BootReadiness,double,bool,bool,bool,bool,HandoffState,string> checkReadiness=delegate(BootReadiness gate,double now,bool windowStable,bool themeFinished,bool themeSucceeded,bool failed,HandoffState expected,string name) {
                Assert(gate.Evaluate(now,windowStable,themeFinished,themeSucceeded,failed)==expected,name);readinessCases++;
            };
            BootReadiness themed=new BootReadiness(true,118.6);
            checkReadiness(themed,1,true,false,false,false,HandoffState.Waiting,"native window ready before current theme helper finishes");
            checkReadiness(themed,17,true,false,true,false,HandoffState.Waiting,"success flag without current helper completion cannot hand off");
            checkReadiness(themed,25,true,true,true,false,HandoffState.Waiting,"current theme success starts paint settling");
            checkReadiness(themed,25.39,true,true,true,false,HandoffState.Waiting,"theme paint settling is at least 400ms");
            checkReadiness(themed,25.41,true,true,true,false,HandoffState.Ready,"current helper success and stable window permit handoff");
            checkReadiness(themed,26,false,true,true,false,HandoffState.Waiting,"lost stable window cancels readiness");
            checkReadiness(themed,26.5,true,true,true,false,HandoffState.Waiting,"new stable window restarts paint settling");
            checkReadiness(themed,26.91,true,true,true,false,HandoffState.Ready,"new stable window can complete handoff");
            checkReadiness(new BootReadiness(true,118.6),30,true,false,false,false,HandoffState.Waiting,"new launch does not inherit prior helper success");
            checkReadiness(new BootReadiness(true,118.6),20,true,true,false,false,HandoffState.Failed,"failed current theme helper releases opening");
            checkReadiness(new BootReadiness(true,118.6),20,false,false,false,true,HandoffState.Failed,"launch exception releases opening without a window");
            BootReadiness timed=new BootReadiness(true,10);
            checkReadiness(timed,9.99,true,false,false,false,HandoffState.Waiting,"theme wait is bounded but does not release early");
            checkReadiness(timed,10,true,false,false,false,HandoffState.TimedOut,"theme timeout releases opening");
            checkReadiness(new BootReadiness(true,10),10,false,true,true,false,HandoffState.TimedOut,"window timeout releases opening");
            BootReadiness unthemed=new BootReadiness(false,118.6);
            checkReadiness(unthemed,1,false,false,false,false,HandoffState.Waiting,"no-theme mode still waits for stable window");
            checkReadiness(unthemed,2,true,false,false,false,HandoffState.Ready,"no-theme mode retains native window readiness");
            checkReadiness(new BootReadiness(false,10),10,false,false,false,false,HandoffState.TimedOut,"no-theme timeout still releases opening");
            BootReadiness exitWait=new BootReadiness(true,10);
            checkReadiness(exitWait,9.99,false,false,false,false,HandoffState.Waiting,"previous-process exit wait uses the original deadline");
            checkReadiness(exitWait,10,false,false,false,false,HandoffState.TimedOut,"previous-process exit wait cannot hold the opening indefinitely");
            Assert(new Config().MaxWaitSeconds==120,"default maximum wait is 120 seconds");
            Assert(new Config().MaximizeColdStart,"cold starts maximize by default");
            Assert(Geometry.Ease(-1)==0&&Geometry.Ease(2)==1&&Math.Abs(Geometry.Ease(.5)-.5)<1e-10,"ease endpoints");
            foreach(Bounds work in new Bounds[] { new Bounds(0,0,1920,1040),new Bounds(-1920,0,1920,1040),new Bounds(0,0,1366,728),
                new Bounds(1920,-1440,2560,1400),new Bounds(0,0,800,560),new Bounds(0,0,1080,1880),new Bounds(0,0,3840,2120) }) {
                Bounds p=Geometry.Player(work,c.PlayerWidth,c.PlayerHeight);
                Assert(p.Width>0&&p.Height>0&&p.Width<=1280&&p.Height<=720,"maximum size");
                Assert(p.Left>=work.Left&&p.Top>=work.Top&&p.Right<=work.Right&&p.Bottom<=work.Bottom,"work area fit");
                Assert(p.Width<=work.Width*.9+1&&p.Height<=work.Height*.9+1,"90 percent fit");
                Assert(Math.Abs((p.Left-work.Left)*2+p.Width-work.Width)<=1&&Math.Abs((p.Top-work.Top)*2+p.Height-work.Height)<=1,"centered position");placements++;
                foreach(int[] source in new int[][] { new int[] {1920,1080},new int[] {1600,1000},new int[] {1000,1600},new int[] {3440,1440} }) {
                    for(int i=0;i<=100;i++) {
                        Bounds b=Geometry.Thumbnail(source[0],source[1],p.Width,p.Height,i/100.0);
                        Assert(b.Left>=0&&b.Top>=0&&b.Right<=p.Width+1&&b.Bottom<=p.Height+1&&b.Width>0&&b.Height>0,"thumbnail viewport");thumbnails++;
                    }
                    Bounds end=Geometry.Thumbnail(source[0],source[1],p.Width,p.Height,1),fit=Geometry.Fit(source[0],source[1],new Bounds(0,0,p.Width,p.Height));
                    Assert(end.Left==fit.Left&&end.Top==fit.Top&&end.Right==fit.Right&&end.Bottom==fit.Bottom,"final native aspect ratio");
                }
            }
            foreach(Bounds target in new Bounds[] {new Bounds(6,117,2180,1520),new Bounds(-2560,0,2560,1400),new Bounds(0,0,3840,2120),new Bounds(1920,-1080,1080,1800)}) {
                Bounds result=Geometry.MatchTarget(new Bounds(0,0,2736,1734),target);
                Assert(result.Left==target.Left&&result.Top==target.Top&&result.Width==target.Width&&result.Height==target.Height,"exact Codex bounds");
                matchedPlacements++;
            }
            Bounds fallbackWork=new Bounds(-1920,0,1920,1040);
            foreach(Bounds unavailable in new Bounds[] {new Bounds(),new Bounds(-32000,-32000,314,50)}) {
                Bounds result=Geometry.MatchTarget(fallbackWork,unavailable);
                Assert(result.Left==fallbackWork.Left&&result.Top==fallbackWork.Top&&result.Width==fallbackWork.Width&&result.Height==fallbackWork.Height,"full work area fallback");
                matchedPlacements++;
            }
            int invalid=0;
            Action<Config> rejects=delegate(Config bad) { bool threw=false;try { ValidateConfig(bad); }catch { threw=true; }Assert(threw,"reject invalid config");invalid++; };
            Func<Config> clone=delegate { return Json.Deserialize<Config>(Json.Serialize(c)); };
            Config v=clone();v.MaxWaitSeconds=Double.NaN;rejects(v);v=clone();v.AppLaunch="cmd.exe";rejects(v);
            v=clone();v.MaxWaitSeconds=120.01;rejects(v);
            v=clone();v.AutoReplaceEntrypoints=true;rejects(v);v=clone();v.HoldAtSeconds=v.DurationSeconds;rejects(v);v=clone();v.ProcessNames=new string[] {"x;whoami"};rejects(v);
            v=clone();v.WindowSizing="Unknown";rejects(v);
            bool outside=false;v=clone();v.Artwork="..\\escape.png";try { ArtworkPath(root,v); }catch { outside=true; }Assert(outside,"artwork path confinement");
            outside=false;try { ThemePath(root,"..\\..\\escape.exe"); }catch { outside=true; }Assert(outside,"theme path confinement");
            v=clone();v.ThemeRuntime="..\\TushanSusuTheme\\runtime\\OpenChatGPTSkin.exe";v.ThemeDataRoot="..\\TushanSusuTheme\\runtime-data";v.ThemeId="bad id";v.ThemeVersion="1.0.0";rejects(v);
            SaveJson(IOPath.Combine(root,"qa","self-test.json"),new { ok=true,placementCases=placements,matchedPlacementCases=matchedPlacements,thumbnailGeometryCases=thumbnails,readinessCases=readinessCases,reuseCases=reuseCases,activationPolicyCases=activationPolicyCases,invalidConfigsRejected=invalid,
                artworkExists=File.Exists(ArtworkPath(root,c)),autoReplaceEntrypoints=false,coldMaximizes=true,warmGeometryPreservedPolicy=true,nativeUiTested=false,
                scope="Official warm lifecycle, unambiguous loopback arguments and exclusive process-owned activation port, marked primary identity, PID/start-time/token mismatch rejection, warm confirmation and geometry policy, previous-process exit continuation, cold-only maximization and readiness, current-launch theme handoff, failures/timeouts, monitor and thumbnail geometry, path confinement; no desktop window opened." });
            return 0;
        }
        private static int Diagnose(string root,string destination) {
            Config c=ReadConfig(root);List<object> windows=new List<object>();List<object> processes=new List<object>();
            List<PrimaryWindowInfo> reusable=Native.ReusablePrimaryWindows(c);
            PrimaryIdentity identity=ReadPrimaryIdentity(root);
            PrimaryWindowInfo trusted=Native.FindTrustedPrimary(c,identity);
            PrimaryWindowInfo selected=PrimaryWindowPolicy.Best(reusable.Where(w=>w.Visible))??trusted;
            bool previousOwnerAlive=Native.IdentityOwnerAlive(c,identity);
            foreach(string name in c.ProcessNames)foreach(Process process in Process.GetProcessesByName(name)) {
                try {
                    string path=Native.ProcessPath(process.Id);
                    processes.Add(new { processId=process.Id,name=process.ProcessName,processPath=path,
                        matchesPath=!String.IsNullOrEmpty(path)&&path.IndexOf(c.ProcessPathContains,StringComparison.OrdinalIgnoreCase)>=0 });
                } catch { } finally { process.Dispose(); }
            }
            foreach(IntPtr h in Native.Windows(c)) {
                uint pid;Native.GetWindowThreadProcessId(h,out pid);Bounds bounds;Native.GetWindowRect(h,out bounds);
                Bounds client=Native.ClientBounds(h);
                windows.Add(new { handle="0x"+h.ToInt64().ToString("X"),processId=pid,processPath=Native.ProcessPath((int)pid),
                    bounds=new { x=bounds.Left,y=bounds.Top,width=bounds.Width,height=bounds.Height },
                    clientBounds=new { x=client.Left,y=client.Top,width=client.Width,height=client.Height },
                    maximized=Native.IsZoomed(h),minimized=Native.IsIconic(h),responsive=Native.Responsive(h) });
            }
            SaveJson(destination,new { ok=true,readOnly=true,windowCount=windows.Count,windows=windows,processes=processes,appLaunch=c.AppLaunch,
                reusablePrimaryCount=reusable.Count,identityRecorded=identity!=null,identityTrusted=trusted!=null,previousOwnerAlive=previousOwnerAlive,
                selectedReuseMode=PrimaryWindowPolicy.Mode(false,selected,selected!=null,trusted!=null&&selected!=null&&selected.Handle==trusted.Handle).ToString(),
                reusablePrimaryWindows=reusable.Select(w=>new {
                    handle="0x"+w.Handle.ToInt64().ToString("X"),processId=w.ProcessId,visible=w.Visible,minimized=w.Minimized,maximized=w.Maximized,restoreMaximized=w.RestoreMaximized,
                    identityTrusted=trusted!=null&&w.Handle==trusted.Handle,
                    reuseMode=PrimaryWindowPolicy.Mode(false,w,true,trusted!=null&&w.Handle==trusted.Handle).ToString(),
                    style="0x"+w.Style.ToString("X8"),extendedStyle="0x"+w.ExtendedStyle.ToString("X8"),owned=w.Owned,tool=w.Tool,cloaked=w.Cloaked,
                    minimizable=w.Minimizable,maximizable=w.Maximizable,responsive=w.Responsive,validBounds=Geometry.ValidTarget(w.RestoredBounds),
                    restoredBounds=new {x=w.RestoredBounds.Left,y=w.RestoredBounds.Top,width=w.RestoredBounds.Width,height=w.RestoredBounds.Height} }).ToArray(),
                artworkExists=File.Exists(ArtworkPath(root,c)),nativeUiOpened=false });return 0;
        }
        private static void Render(string root,Config c,string destination,double seconds,int width=1920,int height=1080) {
            OpeningScene scene=new OpeningScene(ArtworkPath(root,c),c);scene.SetTime(seconds,false);
            Grid layout=OpeningLayout.Create(scene,ArtworkPath(root,c));
            layout.Measure(new Size(width,height));layout.Arrange(new Rect(0,0,width,height));layout.UpdateLayout();
            RenderTargetBitmap bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(layout);
            PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(IOPath.GetDirectoryName(IOPath.GetFullPath(destination)));
            using(FileStream stream=File.Create(destination))encoder.Save(stream);
        }
        private static string After(string[] args,string key,string fallback) {
            int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length&&!args[i+1].StartsWith("--")?args[i+1]:fallback;
        }
        [STAThread]
        public static int Main(string[] args) {
            string root=AppDomain.CurrentDomain.BaseDirectory;bool headless=args.Any(a=>a=="--self-test"||a=="--diagnose"||a=="--render-preview"||a=="--render-frames");
            try {
                if(args.Contains("--self-test"))return SelfTest(root);
                Native.SetProcessDPIAware();
                if(args.Contains("--diagnose"))return Diagnose(root,After(args,"--diagnose",IOPath.Combine(root,"qa","diagnostics.json")));
                Config c=ReadConfig(root);
                if(!File.Exists(ArtworkPath(root,c)))throw new FileNotFoundException("未找到涂山苏苏主题插画。请保留完整的 media 文件夹。",ArtworkPath(root,c));
                if(args.Contains("--render-preview")) {
                    double time=Double.Parse(After(args,"--time","2.5"),CultureInfo.InvariantCulture);
                    if(!Geometry.Finite(time)||time<0||time>c.DurationSeconds)throw new ArgumentException("预览时刻无效。");
                    int width=Int32.Parse(After(args,"--width","1920"),CultureInfo.InvariantCulture),height=Int32.Parse(After(args,"--height","1080"),CultureInfo.InvariantCulture);
                    if(width<320||height<240||width>7680||height>4320)throw new ArgumentException("预览尺寸无效。");
                    Render(root,c,After(args,"--render-preview",IOPath.Combine(root,"preview.png")),time,width,height);return 0;
                }
                if(args.Contains("--render-frames")) {
                    string directory=After(args,"--render-frames",IOPath.Combine(root,"qa","frames"));
                    foreach(double t in new double[] {0,1,2.5,5})Render(root,c,IOPath.Combine(directory,"frame-"+t.ToString("0.0",CultureInfo.InvariantCulture)+".png"),t);return 0;
                }
                bool owns;
                using(Mutex mutex=new Mutex(true,"Local\\FoxSpiritCodexBoot",out owns)) {
                    if(!owns) {
                        // A cold-start opening already owns the handoff. Bring that
                        // opening forward instead of bypassing its readiness gate.
                        IntPtr opening=Native.FindExistingOpening(IOPath.Combine(root,"FoxSpiritCodexBoot.exe"));
                        bool activated=Native.ActivateOpeningWindow(opening);
                        Log(root,"duplicate-launch-existing-opening activated="+activated);return 0;
                    }
                    bool preview=args.Contains("--preview");
                    PrimaryIdentity identity=preview?null:ReadPrimaryIdentity(root);
                    PrimaryWindowInfo trusted=preview?null:Native.FindTrustedPrimary(c,identity);
                    PrimaryWindowInfo visible=preview?null:PrimaryWindowPolicy.Best(Native.ReusablePrimaryWindows(c).Where(w=>w.Visible));
                    PrimaryWindowInfo reusable=visible??trusted;
                    bool ownerAlive=reusable!=null&&Native.AppProcesses(c).Contains((int)reusable.ProcessId);
                    ReuseMode reuseMode=PrimaryWindowPolicy.Mode(preview,reusable,ownerAlive,reusable!=null&&trusted!=null&&reusable.Handle==trusted.Handle);
                    if(reuseMode!=ReuseMode.None) {
                        bool activated=false,stableReuse=false;
                        try {
                            PrimaryIdentity confirmed=trusted!=null&&reusable.Handle==trusted.Handle?identity:RememberPrimary(root,c,reusable.Handle);
                            if(confirmed==null)throw new InvalidOperationException("无法核验当前 Codex 主窗口，未改变它的显示状态。");
                            identity=confirmed;
                            // Native ShowWindow bypasses Electron's show/hide
                            // notifications and can expose a frozen compositor.
                            // Let the official single-instance handler restore it.
                            uint activationPid=OfficialWarmActivation.Activate(c,identity);
                            activated=activationPid!=0;
                            Log(root,"official-warm-activation-requested pid="+identity.ProcessId);
                            stableReuse=Native.ConfirmReuse(c,reusable,identity);
                        } catch(Exception e) {
                            Log(root,"reuse-activation-failed mode=official-lifecycle "+e.Message);
                            // Never cold-reload or maximize an existing live main
                            // window merely because its restore is delayed.
                            if(Native.FindTrustedPrimary(c,identity)!=null)throw;
                        }
                        Log(root,"reused-existing-primary mode=official-lifecycle handle=0x"+reusable.Handle.ToInt64().ToString("X")+
                            " wasVisible="+reusable.Visible+" wasMinimized="+reusable.Minimized+" wasMaximized="+reusable.Maximized+" activated="+activated+" stable="+stableReuse);
                        if(stableReuse)return 0;
                        if(Native.FindTrustedPrimary(c,identity)!=null)throw new InvalidOperationException("Codex 已收到恢复请求，但窗口尚未准备好。请稍候再试，当前窗口大小和会话已保留。");
                        Log(root,"reuse-did-not-survive-continuing-cold-request");
                    }
                    trusted=preview?null:Native.FindTrustedPrimary(c,identity);
                    bool previousOwnerAlive=!preview&&Native.IdentityOwnerAlive(c,identity);
                    PrimaryIdentity waitForIdentity=PrimaryWindowPolicy.WaitForPreviousExit(identity,previousOwnerAlive,trusted!=null)?identity:null;
                    Application app=new Application();app.ShutdownMode=ShutdownMode.OnMainWindowClose;
                    app.DispatcherUnhandledException+=delegate(object sender,DispatcherUnhandledExceptionEventArgs e) { Log(root,"runtime-error "+e.Exception);e.Handled=true;app.Shutdown(1); };
                    return app.Run(new BootWindow(c,root,preview,waitForIdentity));
                }
            } catch(Exception e) {
                Log(root,"error "+e);
                if(!headless)MessageBox.Show(e.Message,"涂山苏苏 · Codex 启动动画",MessageBoxButton.OK,MessageBoxImage.Information);
                else { try { SaveJson(IOPath.Combine(root,"qa","last-error.json"),new {ok=false,error=e.ToString()}); }catch { } }
                return 1;
            }
        }
    }
}
