using System;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows;

namespace ExplorerTabUtility.UI.Views;

// ReSharper disable once RedundantExtendsListEntry
public partial class AboutView : UserControl
{
    private bool _isSupportersLoaded;
    private bool _isAvatarLoaded;
    private bool _animationsRunning;
    private Window? _hostWindow;
    private Storyboard? _iconStoryboard;
    private Storyboard? _titleStoryboard;
    private Storyboard? _starStoryboard;
    private Storyboard? _developerNameStoryboard;

    public string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v1.0.0";
        }
    }

    public AboutView()
    {
        InitializeComponent();
        IsVisibleChanged += AboutView_IsVisibleChanged;
        Unloaded += AboutView_Unloaded;
    }

    private void AboutView_Loaded(object sender, RoutedEventArgs e)
    {
        AttachHostWindowEvents();

        if (!_isAvatarLoaded)
            LoadDeveloperAvatar();

        if (!_isSupportersLoaded)
            _ = LoadSupporters();

        UpdateAnimationState();
    }

    private void AboutView_Unloaded(object sender, RoutedEventArgs e)
    {
        StopAmbientAnimations();
        DetachHostWindowEvents();
    }

    private void AboutView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (!_isAvatarLoaded)
                LoadDeveloperAvatar();
            if (!_isSupportersLoaded)
                _ = LoadSupporters();
        }

        UpdateAnimationState();
    }

    private void AttachHostWindowEvents()
    {
        var window = Window.GetWindow(this);
        if (ReferenceEquals(_hostWindow, window)) return;

        DetachHostWindowEvents();
        _hostWindow = window;
        if (_hostWindow != null)
        {
            _hostWindow.IsVisibleChanged += HostWindow_VisibilityOrStateChanged;
            _hostWindow.StateChanged += HostWindow_StateChanged;
        }
    }

    private void DetachHostWindowEvents()
    {
        if (_hostWindow == null) return;
        _hostWindow.IsVisibleChanged -= HostWindow_VisibilityOrStateChanged;
        _hostWindow.StateChanged -= HostWindow_StateChanged;
        _hostWindow = null;
    }

    private void HostWindow_VisibilityOrStateChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateAnimationState();
    private void HostWindow_StateChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private void UpdateAnimationState()
    {
        var shouldRun = IsLoaded &&
                        IsVisible &&
                        _hostWindow is { IsVisible: true, WindowState: not WindowState.Minimized };

        if (shouldRun)
            StartAmbientAnimations();
        else
            StopAmbientAnimations();
    }

    private void StartAmbientAnimations()
    {
        if (_animationsRunning) return;
        _animationsRunning = true;

        _iconStoryboard ??= Resources["IconAnimation"] as Storyboard;
        _titleStoryboard ??= Resources["TitleAnimation"] as Storyboard;
        _starStoryboard ??= Resources["StarContinuousAnimation"] as Storyboard;
        _developerNameStoryboard ??= Resources["DeveloperNameAnimation"] as Storyboard;

        _iconStoryboard?.Begin(LogoBorder, isControllable: true);
        _titleStoryboard?.Begin(TitleText, isControllable: true);
        _starStoryboard?.Begin(BtnGitHub, isControllable: true);
        _developerNameStoryboard?.Begin(DeveloperNameText, isControllable: true);
    }

    private void StopAmbientAnimations()
    {
        if (!_animationsRunning) return;
        _animationsRunning = false;

        _iconStoryboard?.Remove(LogoBorder);
        _titleStoryboard?.Remove(TitleText);
        _starStoryboard?.Remove(BtnGitHub);
        _developerNameStoryboard?.Remove(DeveloperNameText);
    }

    private void LoadDeveloperAvatar()
    {
        _isAvatarLoaded = true;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri("https://avatars.githubusercontent.com/u/6666887?s=160");
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            DeveloperAvatarBorder.Background = new ImageBrush(bitmap);
        }
        catch
        {
            // Offline or load error
        }
    }

    private async Task LoadSupporters()
    {
        try
        {
            var supporters = await Helpers.Helper.GetSupporters().ConfigureAwait(true);
            if (supporters.Count > 0) _isSupportersLoaded = true;

            SupportersItemsControl.ItemsSource = supporters;
        }
        catch
        {
            // Ignored
        }
    }

    private void StarClickAnimation_Completed(object _, EventArgs __)
    {
        // Restart the continuous animation only if AboutView is still actively visible
        if (_animationsRunning)
            _starStoryboard?.Begin(BtnGitHub, isControllable: true);
    }

    private void BtnGitHubSponsors_Click(object _, RoutedEventArgs __) => OpenUrl("https://github.com/sponsors/w4po");
    private void BtnPatreon_Click(object _, RoutedEventArgs __) => OpenUrl("https://www.patreon.com/w4po");
    private void BtnBuyMeACoffee_Click(object _, RoutedEventArgs __) => OpenUrl("https://www.buymeacoffee.com/w4po");
    private void BtnPayPal_Click(object _, RoutedEventArgs __) => OpenUrl("https://paypal.me/w4po77");
    private void OpenDeveloperPage(object _, RoutedEventArgs __) => OpenUrl("https://github.com/w4po");
    private void OpenProjectPage(object _, RoutedEventArgs __) => OpenUrl("https://github.com/w4po/ExplorerTabUtility");

    private void OpenSupportPage(object sender, RoutedEventArgs _)
    {
        if (sender is Border { Tag: string profileUrl } && !string.IsNullOrWhiteSpace(profileUrl))
            OpenUrl(profileUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore
        }
    }
}
