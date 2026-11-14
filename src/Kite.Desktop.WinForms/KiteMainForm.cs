using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kite.Management.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kite.Desktop.WinForms;

/// <summary>
/// Native WinForms host shell for the "Obsidian Telemetry" Design System.
/// Integrates Windows 11 DWM dark obsidian chrome, zero-port <see cref="WebView2InMemoryBridge"/>,
/// and native window/OS command marshaling via <see cref="CoreWebView2.WebMessageReceived"/>.
/// </summary>
public sealed class KiteMainForm : Form
{
    private static readonly Color ObsidianLowest = Color.FromArgb(14, 14, 18); // #0e0e12

    private readonly KiteInMemoryHost _inMemoryHost;
    private readonly WebView2InMemoryBridge _bridge;
    private readonly WebView2 _webView;
    private readonly Icon? _ownedAppIcon;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly TaskCompletionSource<bool> _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _customUserDataFolder;

    public KiteMainForm(KiteInMemoryHost inMemoryHost, string? customUserDataFolder = null)
    {
        _inMemoryHost = inMemoryHost;
        _customUserDataFolder = customUserDataFolder;
        _bridge = new WebView2InMemoryBridge(_inMemoryHost);

        Text = "Kite Manager Studio v10.0 — Obsidian Telemetry";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(1024, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = ObsidianLowest;
        ForeColor = Color.FromArgb(228, 225, 231); // #e4e1e7
        _ownedAppIcon = LoadEmbeddedAppIcon();
        Icon = _ownedAppIcon ?? SystemIcons.Application;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = ObsidianLowest
        };

        Controls.Add(_webView);

        // System Tray Menu & Icon (4 Obsidian Telemetry Views)
        _trayMenu = new ContextMenuStrip();
        _trayMenu.Items.Add("Open Kite Manager Studio", null, (_, _) => ShowAndActivate());
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("1. Dashboard & Telemetry", null, (_, _) => NavigateScope("server"));
        _trayMenu.Items.Add("2. Sites & App Pools", null, (_, _) => NavigateScope("sites"));
        _trayMenu.Items.Add("3. Configuration & Features", null, (_, _) => NavigateScope("config"));
        _trayMenu.Items.Add("4. Worker Processes & Tracing", null, (_, _) => NavigateScope("workers"));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Compact Desktop UI Heap (Gen2 GC)", null, (_, _) =>
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });
        _trayMenu.Items.Add("Exit Kite Manager", null, (_, _) => Close());

        _trayIcon = new NotifyIcon
        {
            Text = "Kite Manager Studio v10.0 (.NET 10)",
            Icon = _ownedAppIcon ?? SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowAndActivate();

        HandleCreated += (_, _) => ApplyObsidianDwmAttributes();
        Load += OnFormLoadAsync;
    }

    public WebView2 WebViewControl => _webView;
    public Task<bool> ReadyTask => _initTcs.Task;

    private async void OnFormLoadAsync(object? sender, EventArgs e)
    {
        try
        {
            string userDataFolder = _customUserDataFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kite",
                "WebView2UserData");

            var envOptions = new CoreWebView2EnvironmentOptions(
                additionalBrowserArguments: "--no-proxy-server --disable-background-networking --dns-prefetch-disable --disable-component-update");
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: envOptions);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _bridge.Attach(_webView.CoreWebView2);

            bool warmed = false;
            async void OnInitialNav(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (!warmed)
                {
                    warmed = true;
                    try
                    {
                        _ = await _webView.CoreWebView2.ExecuteScriptAsync("document.querySelector('a[href=\"/?scope=server\"]').click();");
                        return;
                    }
                    catch
                    {
                    }
                }

                _webView.CoreWebView2.NavigationCompleted -= OnInitialNav;
                _initTcs.TrySetResult(args.IsSuccess);
            }

            _webView.CoreWebView2.NavigationCompleted += OnInitialNav;
            _webView.CoreWebView2.Navigate($"{WebView2InMemoryBridge.VirtualOrigin}/?scope=pools");
        }
        catch (Exception ex)
        {
            _initTcs.TrySetException(ex);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? msg;
        try
        {
            msg = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(msg))
        {
            return;
        }

        switch (msg)
        {
            case "window:drag":
                ReleaseCapture();
                _ = SendMessage(Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, 0x0002 /* HTCAPTION */, 0);
                break;

            case "window:minimize":
                WindowState = FormWindowState.Minimized;
                break;

            case "window:maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;

            case "window:close":
                Close();
                break;

            default:
                if (msg.StartsWith("browser:open:", StringComparison.OrdinalIgnoreCase))
                {
                    string url = msg["browser:open:".Length..].Trim();
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
                    }
                }
                else if (msg.StartsWith("explorer:open:", StringComparison.OrdinalIgnoreCase))
                {
                    string path = msg["explorer:open:".Length..].Trim();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
                    }
                }
                break;
        }
    }

    private void ApplyObsidianDwmAttributes()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            int darkMode = 1;
            _ = DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref darkMode, sizeof(int));

            int cornerPref = 2; // DWMWCP_ROUND (8px radius on Windows 11)
            _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref cornerPref, sizeof(int));

            int borderColor = 0x002E292A; // COLORREF BGR for #2a292e (surface-container-high hairline)
            _ = DwmSetWindowAttribute(Handle, 34 /* DWMWA_BORDER_COLOR */, ref borderColor, sizeof(int));

            int captionColor = 0x00120E0E; // COLORREF BGR for #0e0e12 (surface-container-lowest)
            _ = DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref captionColor, sizeof(int));
        }
        catch
        {
            // Non-Windows 11 or DWM unavailable
        }
    }

    private void NavigateScope(string scope)
    {
        ShowAndActivate();
        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.Navigate($"{WebView2InMemoryBridge.VirtualOrigin}/?scope={scope}");
        }
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Activate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _webView.Dispose();
            _ownedAppIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon? LoadEmbeddedAppIcon()
    {
        try
        {
            using Stream? stream = typeof(KiteMainForm).Assembly.GetManifestResourceStream("Kite.Desktop.WinForms.Assets.kite.ico");
            if (stream is not null)
            {
                return new Icon(stream);
            }

            if (File.Exists(Application.ExecutablePath))
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
        }
        catch
        {
        }
        return null;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
