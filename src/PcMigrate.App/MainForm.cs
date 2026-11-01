using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace PcMigrate.App;

/// <summary>
/// The window. Everything you see is the page in ui/index.html, shown with
/// WebView2 (the Edge engine built into Windows). This class only hosts it;
/// <see cref="Bridge"/> answers what the page asks for.
/// </summary>
internal sealed class MainForm : Form
{
    private const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private Bridge? _bridge;

    public MainForm()
    {
        Text = "pc-migrate";
        ClientSize = new Size(1040, 740);
        MinimumSize = new Size(820, 620);
        StartPosition = FormStartPosition.CenterScreen;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (icon is not null) Icon = new Icon(icon);

        // Match the page background so the window doesn't flash white while loading.
        var dark = IsDarkMode();
        BackColor = dark ? Color.FromArgb(0x0f, 0x12, 0x18) : Color.FromArgb(0xf4, 0xf6, 0xfa);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        HandleCreated += (_, _) => UseDarkTitleBar(dark);
        Load += async (_, _) => await StartAsync();
        FormClosing += OnClosing;
    }

    private async Task StartAsync()
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pc-migrate", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show(this,
                "pc-migrate needs the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and most up-to-date Windows 10 PCs.\n\nDownload it now?",
                "pc-migrate", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Process.Start(new ProcessStartInfo(WebView2Download) { UseShellExecute = true });
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = Debugger.IsAttached;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = Debugger.IsAttached;
        // The page never navigates anywhere; links would open inside the app otherwise.
        core.NewWindowRequested += (_, e) => e.Handled = true;

        _bridge = new Bridge(this, core);
        core.WebMessageReceived += (_, e) => _bridge.Handle(e.WebMessageAsJson);

        using var html = typeof(MainForm).Assembly.GetManifestResourceStream("ui.html")!;
        using var reader = new StreamReader(html);
        core.NavigateToString(await reader.ReadToEndAsync());
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_bridge is not { IsBusy: true }) { _bridge?.Dispose(); return; }
        var answer = MessageBox.Show(this, "pc-migrate is still copying. Stop and close?", "pc-migrate",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) { e.Cancel = true; return; }
        _bridge.Dispose();
    }

    private static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private void UseDarkTitleBar(bool dark)
    {
        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
