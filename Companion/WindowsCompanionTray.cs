#if WINDOWS
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;

namespace FederationCompanion;

/// <summary>
/// The Windows desktop shell: a tray icon that keeps the listener, the Plex
/// connection and the media mount manageable without a console window. The
/// browser dashboard remains the place for setup; this is the always-there
/// control surface plus the exit that a WinExe otherwise lacks.
/// </summary>
internal static class WindowsCompanionTray
{
    public static void Start(
        CompanionState state,
        LocalMediaMountService mount,
        IAutostartRegistration autostart,
        CompanionRuntime runtime,
        IHostApplicationLifetime lifetime)
    {
        var thread = new Thread(() => Run(state, mount, autostart, runtime, lifetime))
        {
            IsBackground = true,
            Name = "CompanionTray"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void Run(
        CompanionState state,
        LocalMediaMountService mount,
        IAutostartRegistration autostart,
        CompanionRuntime runtime,
        IHostApplicationLifetime lifetime)
    {
        try
        {
            ApplicationConfiguration.Initialize();

            using var icon = LoadIcon();
            using var menu = new ContextMenuStrip();
            using var notify = new NotifyIcon
            {
                Icon = icon,
                Text = "Federation Companion",
                Visible = true,
                ContextMenuStrip = menu
            };

            var header = new ToolStripMenuItem("Federation Companion") { Enabled = false };
            var open = new ToolStripMenuItem("Open dashboard") { Font = new Font(menu.Font, FontStyle.Bold) };
            var copyKey = new ToolStripMenuItem("Copy owner key");
            var plexStatus = new ToolStripMenuItem("Plex: checking…") { Enabled = false };
            var mountStatus = new ToolStripMenuItem("Media folder: checking…") { Enabled = false };
            var friendsStatus = new ToolStripMenuItem("Friends: 0") { Enabled = false };
            var start = new ToolStripMenuItem("Start media folder");
            var stop = new ToolStripMenuItem("Stop media folder");
            var signIn = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false, Checked = autostart.IsEnabled() };
            var logFolder = new ToolStripMenuItem("Open log folder");
            var installFolder = new ToolStripMenuItem("Open install folder");
            var exit = new ToolStripMenuItem("Exit Companion");

            menu.Items.AddRange(new ToolStripItem[]
            {
                header,
                open,
                copyKey,
                new ToolStripSeparator(),
                plexStatus,
                mountStatus,
                friendsStatus,
                new ToolStripSeparator(),
                start,
                stop,
                new ToolStripSeparator(),
                signIn,
                logFolder,
                installFolder,
                new ToolStripSeparator(),
                exit
            });

            void OpenDashboard()
            {
                if (!CompanionShell.OpenDashboard(runtime, state.AdminAccessKey))
                {
                    notify.ShowBalloonTip(4000, "Federation Companion", "Could not open a browser. Open http://127.0.0.1:" + runtime.Port + " and paste the owner key.", ToolTipIcon.Warning);
                }
            }

            open.Click += (_, _) => OpenDashboard();
            notify.DoubleClick += (_, _) => OpenDashboard();

            copyKey.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetDataObject(state.AdminAccessKey, copy: true);
                    notify.ShowBalloonTip(2000, "Federation Companion", "Owner key copied. Paste it after opening the dashboard URL.", ToolTipIcon.Info);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException)
                {
                    notify.ShowBalloonTip(3000, "Federation Companion", "Could not copy the owner key. Try again.", ToolTipIcon.Warning);
                }
            };

            start.Click += async (_, _) =>
            {
                start.Enabled = false;
                try
                {
                    await mount.StartMountAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Tray could not start the media folder.", ex);
                }
                finally
                {
                    Refresh();
                }
            };

            stop.Click += async (_, _) =>
            {
                stop.Enabled = false;
                try
                {
                    await mount.StopMountAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Tray could not stop the media folder.", ex);
                }
                finally
                {
                    Refresh();
                }
            };

            signIn.Click += (_, _) =>
            {
                var wanted = !signIn.Checked;
                if (!autostart.Set(wanted))
                {
                    notify.ShowBalloonTip(4000, "Federation Companion", "Windows did not accept the sign-in setting. Install Companion to your user folder and try again.", ToolTipIcon.Warning);
                }

                signIn.Checked = autostart.IsEnabled();
            };

            logFolder.Click += (_, _) => CompanionShell.OpenFolder(Path.GetDirectoryName(AppLog.LogPath) ?? runtime.InstallDirectory);
            installFolder.Click += (_, _) => CompanionShell.OpenFolder(runtime.InstallDirectory);

            exit.Click += (_, _) =>
            {
                notify.Visible = false;
                AppLog.Info("Exit requested from the tray.");
                lifetime.StopApplication();
                Application.ExitThread();
            };

            void Refresh()
            {
                try
                {
                    var mounted = MediaMount.IsMounted(state.MediaMountRoot, state.ClientIdentifier);
                    header.Text = "Federation Companion " + CompanionVersion.FederationPluginVersion();
                    plexStatus.Text = string.IsNullOrWhiteSpace(state.ServerName)
                        ? "Plex: not connected"
                        : "Plex: " + state.ServerName;
                    mountStatus.Text = mounted
                        ? "Media folder: ready"
                        : "Media folder: stopped";
                    friendsStatus.Text = "Friends: " + state.Peers.Count.ToString(CultureInfo.InvariantCulture)
                        + " · importing: " + state.ImportPeers.Count.ToString(CultureInfo.InvariantCulture);
                    start.Enabled = !mounted;
                    stop.Enabled = mounted || mount.HasOwnedProcess;
                    signIn.Checked = autostart.IsEnabled();
                    notify.Text = mounted
                        ? "Federation Companion — media folder ready"
                        : "Federation Companion — media folder stopped";
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // A status refresh must never kill the tray loop.
                }
            }

            using var timer = new System.Windows.Forms.Timer { Interval = 3000 };
            timer.Tick += (_, _) => Refresh();
            timer.Start();
            Refresh();

            if (runtime.BackgroundLaunch)
            {
                notify.ShowBalloonTip(3000, "Federation Companion is running",
                    "Right-click the tray icon to open the dashboard or manage the media folder.",
                    ToolTipIcon.Info);
            }

            Application.Run(new ApplicationContext());
            notify.Visible = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("The tray icon failed to start.", ex);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(WindowsCompanionTray).Assembly
                .GetManifestResourceStream("FederationCompanion.companion.ico");
            if (stream != null)
            {
                return new Icon(stream);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
        }

        return SystemIcons.Application;
    }
}
#endif
