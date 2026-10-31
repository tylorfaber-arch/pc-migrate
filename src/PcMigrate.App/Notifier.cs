using System.Media;
using System.Runtime.InteropServices;

namespace PcMigrate.App;

/// <summary>
/// Lets people walk away from a long copy. While it runs, the taskbar button fills up
/// like a progress bar. When it ends, Windows shows a notification (also kept in the
/// notification centre), a sound plays, and the taskbar button flashes until the window
/// is looked at. Clicking the notification opens the report.
/// </summary>
internal sealed class Notifier : IDisposable
{
    private readonly Form _form;
    private readonly NotifyIcon _tray;
    private ITaskbarList3? _taskbar;
    private string? _openOnClick;

    public Notifier(Form form)
    {
        _form = form;
        _tray = new NotifyIcon { Icon = form.Icon, Text = "pc-migrate", Visible = false };
        _tray.BalloonTipClicked += (_, _) => OnClicked();
        _tray.Click += (_, _) => OnClicked();
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (COMException)
        {
            _taskbar = null;
        }
    }

    /// <summary>Fills the taskbar button. Unknown totals show a moving bar instead.</summary>
    public void Progress(long done, long total) => OnUi(() =>
    {
        if (_taskbar is null) return;
        if (total <= 0) { _taskbar.SetProgressState(_form.Handle, TaskbarState.Indeterminate); return; }
        _taskbar.SetProgressState(_form.Handle, TaskbarState.Normal);
        _taskbar.SetProgressValue(_form.Handle, (ulong)Math.Min(done, total), (ulong)total);
    });

    public void Finished(string title, string text, string? openOnClick = null) => Show(title, text, openOnClick, error: false);

    public void Failed(string title, string text, string? openOnClick = null) => Show(title, text, openOnClick, error: true);

    private void Show(string title, string text, string? openOnClick, bool error) => OnUi(() =>
    {
        _openOnClick = openOnClick;
        if (_taskbar is not null)
        {
            if (error) _taskbar.SetProgressState(_form.Handle, TaskbarState.Error);
            else _taskbar.SetProgressState(_form.Handle, TaskbarState.NoProgress);
        }
        _tray.Visible = true;
        _tray.ShowBalloonTip(10_000, title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info);
        (error ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        if (Form.ActiveForm != _form) Flash();
    });

    private void OnClicked()
    {
        _form.Activate();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        if (_openOnClick is { } path && (File.Exists(path) || Directory.Exists(path)))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        _tray.Visible = false;
    }

    private void Flash()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = _form.Handle,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private void OnUi(Action action)
    {
        if (_form.IsDisposed || !_form.IsHandleCreated) return;
        if (_form.InvokeRequired) _form.BeginInvoke(action);
        else action();
    }

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Dispose();
    }

    private const uint FlashAll = 0x3;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private enum TaskbarState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList;

    // Windows' taskbar button interface. The methods must stay in this order: it's how COM finds them.
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, TaskbarState state);
    }
}
