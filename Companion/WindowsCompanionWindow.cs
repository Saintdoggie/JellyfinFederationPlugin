#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace FederationCompanion;

/// <summary>Native Windows desktop. All views are WinForms controls; the optional
/// browser dashboard is never loaded by this window. Closing releases UI resources
/// while the tray and media services continue.</summary>
internal sealed class WindowsCompanionWindow : Form
{
    private static readonly Color Canvas = Color.FromArgb(16, 16, 16);
    private static readonly Color Surface = Color.FromArgb(24, 24, 24);
    private static readonly Color Ink = Color.FromArgb(245, 245, 245);
    private static readonly Color Muted = Color.FromArgb(166, 166, 166);
    private static readonly Color Line = Color.FromArgb(48, 48, 48);
    private readonly CompanionDesktopClient _api;
    private readonly CompanionRuntime _runtime;
    private readonly WindowsCompanionMotion _motion = new();
    private readonly CancellationTokenSource _closed = new();
    private readonly FlowLayoutPanel _content = new WindowsCompanionSurface() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(30, 18, 30, 24) };
    private readonly Label _title = new() { AutoSize = true, Text = "Overview", Margin = new Padding(0, 0, 0, 8) };
    private readonly Label _notice = new() { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(24, 12, 20, 8), Text = "Closing this window keeps sharing running in the tray.", AccessibleName = "App status" };
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly Label _live = new() { AutoSize = true, MaximumSize = new Size(160, 0), Margin = new Padding(0, 36, 0, 0), ForeColor = Muted, Text = "Checking connections…", AccessibleName = "Background connection status" };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 30000 };
    private bool _polling;
    private readonly System.Windows.Forms.Timer _accentTimer = new() { Interval = 16 };
    private Color _accent = Color.FromArgb(229, 160, 13), _targetAccent = Color.FromArgb(229, 160, 13);
    private string _view = "Overview";
    private int _generation;
    private readonly HashSet<Control> _edited = new();
    private bool _dirty => _edited.Any(c => !c.IsDisposed);
    private bool _actionBusy;
    private readonly Dictionary<(float, FontStyle), Font> _fonts = new();

    public WindowsCompanionWindow(CompanionRuntime runtime, string ownerKey, Icon icon)
    {
        _runtime = runtime; _api = new(runtime.Port, ownerKey);
        Text = "Federation Companion"; Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1140, 780); MinimumSize = new Size(820, 600);
        Font = MakeFont(10); BackColor = Canvas; ForeColor = Ink;
        _title.Font = MakeFont(23, FontStyle.Bold); _notice.ForeColor = Muted;
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 208, Padding = new Padding(20, 28, 16, 20), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface };
        sidebar.Controls.Add(Label("Federation", 16, true));
        sidebar.Controls.Add(Label("Companion", 11));
        sidebar.Controls.Add(new Panel { Height = 28, Width = 160 });
        foreach (var view in new[] { "Overview", "Friends", "Incoming", "Settings" })
        {
            var button = ActionButton(view, () => NavigateAsync(view));
            button.Width = 168; button.AutoSize = false; button.Height = 44; button.TextAlign = ContentAlignment.MiddleLeft;
            button.FlatAppearance.BorderSize = 0;
            button.Paint += (_, e) => { if (_view == view) { using var pen = new Pen(_accent, 2); e.Graphics.DrawLine(pen, 0, 10, 0, button.Height - 10); } };
            _navigation.Add(view, button); sidebar.Controls.Add(button);
        }
        sidebar.Controls.Add(_live);
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Canvas };
        var heading = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(30, 28, 30, 12) };
        _title.Dock = DockStyle.Left;
        var refresh = ActionButton("Refresh", () => NavigateAsync(_view)); refresh.Dock = DockStyle.Right;
        heading.Controls.Add(_title); heading.Controls.Add(refresh);
        main.Controls.Add(_content); main.Controls.Add(_notice); main.Controls.Add(heading);
        Controls.Add(main); Controls.Add(sidebar);
        _content.SizeChanged += (_, _) => ResizeRows();
        _accentTimer.Tick += (_, _) =>
        {
            if (!WindowsCompanionMotion.Enabled) _accent = _targetAccent;
            static int Step(int a, int b) => Math.Abs(b-a) < 3 ? b : a + Math.Sign(b-a) * Math.Max(1, (int)(Math.Abs(b-a) * .25));
            _accent = Color.FromArgb(Step(_accent.R,_targetAccent.R), Step(_accent.G,_targetAccent.G), Step(_accent.B,_targetAccent.B));
            foreach (var button in _navigation.Values) button.Invalidate();
            if (_accent == _targetAccent) _accentTimer.Stop();
        };
        _statusTimer.Tick += async (_, _) => await RefreshBackgroundStatus();
        Shown += async (_, _) => { await NavigateAsync("Overview"); await RefreshBackgroundStatus(); _statusTimer.Start(); };
    }

    private async Task RefreshBackgroundStatus()
    {
        if (_polling || _actionBusy || WindowState == FormWindowState.Minimized || IsDisposed) return;
        _polling = true;
        try
        {
            var status = await Get("/api/status");
            var requests = Rows(await Get("/api/companion/requests")).Count(r => !Flag(r,"outgoing") && TextOf(r,"status") == "pending");
            if (!IsDisposed)
            {
                _live.Text = (Flag(status,"serverConnected") ? TextOf(status,"serverName","Plex connected") : "Plex not connected") + Environment.NewLine + (requests > 0 ? $"{requests} pending friend request(s)" : "Running in the background");
                _navigation["Friends"].Text = requests > 0 ? $"Friends ({requests})" : "Friends";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        { if (!IsDisposed) _live.Text = "Connection needs attention. Use Refresh to retry."; }
        finally { _polling = false; }
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _dirty && MessageBox.Show(this, "Close without saving your changes?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        { int dark = 1; _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    private Font MakeFont(float size, FontStyle style = FontStyle.Regular) { if (!_fonts.TryGetValue((size, style), out var font)) _fonts[(size, style)] = font = new Font("Segoe UI", size, style); return font; }
    private Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, AutoSize = true, UseMnemonic = false, ForeColor = bold ? Ink : Muted, Font = MakeFont(size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 2, 0, 8), MaximumSize = new Size(750, 0) };
    private static string TextOf(JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    private static bool Flag(JsonNode? node, string key) => node?[key]?.ToString() == "true";
    private static IEnumerable<JsonNode> Rows(JsonNode? node) => node is JsonArray array ? array.OfType<JsonNode>() : Enumerable.Empty<JsonNode>();
    private static string Id(JsonNode node) => Uri.EscapeDataString(TextOf(node, "id"));
    private Task<JsonNode> Get(string path) => _api.SendAsync(path, ct: _closed.Token);
    private Task<JsonNode> Post(string path, object? body = null) => _api.SendAsync(path, body ?? new { }, HttpMethod.Post, _closed.Token);
    private Task<JsonNode> Delete(string path) => _api.SendAsync(path, method: HttpMethod.Delete, ct: _closed.Token);

    private Button ActionButton(string text, Func<Task> action, bool primary = false, bool danger = false)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(90, 38), Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, BackColor = primary ? Ink : Surface, ForeColor = primary ? Canvas : danger ? Color.LightCoral : Ink, Margin = new Padding(0, 4, 10, 4), UseVisualStyleBackColor = false, AccessibleName = text };
        button.FlatAppearance.BorderColor = Line;
        button.Click += async (_, _) =>
        {
            if (_actionBusy) return;
            _actionBusy = true; button.Enabled = false; UseWaitCursor = true;
            _content.Enabled = false;
            foreach (var navigation in _navigation.Values) navigation.Enabled = false;
            try { await action(); }
            catch (OperationCanceledException) { if (!IsDisposed) Notice("The action timed out. Check the connection and retry."); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or IOException)
            { if (!IsDisposed) Notice(ex is InvalidOperationException ? ex.Message : "Companion could not finish this action. Check the connection and retry."); }
            finally
            {
                _actionBusy = false;
                if (!IsDisposed) { UseWaitCursor = false; _content.Enabled = true; foreach (var navigation in _navigation.Values) navigation.Enabled = true; }
                if (!button.IsDisposed) button.Enabled = true;
            }
        };
        return button;
    }
    private void Notice(string message) { if (!IsDisposed) _notice.Text = message; }
    private FlowLayoutPanel Section(string title, string? description = null)
    {
        var panel = new WindowsCompanionSurface { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Surface, Padding = new Padding(20, 16, 20, 16), Margin = new Padding(0, 0, 0, 18), Width = 800 };
        panel.Controls.Add(Label(title, 12, true));
        if (!string.IsNullOrEmpty(description)) panel.Controls.Add(Label(description));
        return panel;
    }
    private FlowLayoutPanel Collapsed(FlowLayoutPanel section)
    {
        var title = section.Controls[0].Text;
        section.Controls[0].Dispose();
        var body = section.Controls.Cast<Control>().ToArray();
        foreach (var child in body) child.Visible = false;
        var expanded = false;
        Button? toggle = null;
        toggle = ActionButton("▸ " + title, async () =>
        {
            expanded = !expanded;
            toggle!.Text = (expanded ? "▾ " : "▸ ") + title;
            var before = section.Height;
            section.AutoSize = false;
            foreach (var child in body) child.Visible = expanded;
            var after = section.GetPreferredSize(new Size(section.Width, 0)).Height;
            foreach (var child in body) child.Visible = true;
            await _motion.Animate(section, progress => section.Height = before + (int)Math.Round((after - before) * progress));
            if (section.IsDisposed) return;
            foreach (var child in body) child.Visible = expanded;
            section.AutoSize = true;
            ResizeRows();
        });
        section.Controls.Add(toggle); section.Controls.SetChildIndex(toggle, 0);
        return section;
    }
    private static FlowLayoutPanel Actions(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 4), MaximumSize = new Size(740, 0) };
        row.Controls.AddRange(controls); return row;
    }
    private TextBox Field(FlowLayoutPanel parent, string label, string value = "", bool secret = false)
    {
        parent.Controls.Add(Label(label));
        var field = new TextBox { Text = value, Width = 500, BackColor = Canvas, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = secret, AccessibleName = label, Margin = new Padding(0, 0, 0, 12) };
        field.TextChanged += (_, _) => _edited.Add(field); parent.Controls.Add(field); return field;
    }
    private CheckBox Check(FlowLayoutPanel parent, string text, bool value, bool enabled = true)
    {
        var check = new CheckBox { Text = text, Checked = value, Enabled = enabled, AutoSize = true, UseMnemonic = false, ForeColor = Ink, Margin = new Padding(0, 6, 0, 6), AccessibleName = text };
        check.CheckedChanged += (_, _) => _edited.Add(check); parent.Controls.Add(check); return check;
    }
    private void Accent(string kind)
    {
        _targetAccent = kind == "Plex" ? Color.FromArgb(229, 160, 13) : Color.FromArgb(173, 137, 236);
        if (!WindowsCompanionMotion.Enabled) { _accent = _targetAccent; foreach (var b in _navigation.Values) b.Invalidate(); }
        else _accentTimer.Start();
    }
    private void BindService(Control row, string kind)
    {
        row.Enter += (_, _) => Accent(kind); row.MouseDown += (_, _) => Accent(kind);
        foreach (Control child in row.Controls) BindService(child, kind);
    }
    private Label ServiceLabel(string kind)
    { var label = Label(kind, 9, true); label.ForeColor = kind == "Plex" ? Color.FromArgb(229,160,13) : Color.FromArgb(173,137,236); return label; }
    private void ResizeRows()
    {
        var width = Math.Max(320, _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (Control row in _content.Controls)
        {
            row.Width = width; row.MaximumSize = new Size(width, 0);
            Fit(row, width - row.Padding.Horizontal);
        }
    }
    private static void Fit(Control parent, int width)
    {
        foreach (Control child in parent.Controls)
        {
            child.MaximumSize = new Size(Math.Max(150, width - child.Margin.Horizontal), 0);
            if (child is TextBox or ComboBox or CheckedListBox) child.Width = Math.Min(500, width);
            if (child is FlowLayoutPanel) Fit(child, width - child.Padding.Horizontal);
        }
    }
    private async Task NavigateAsync(string view, bool discard = false)
    {
        if (_dirty && !discard && MessageBox.Show(this, "Leave this page and discard unsaved changes?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _edited.Clear(); var generation = ++_generation; _view = view; _title.Text = view; Accent("Plex");
        foreach (var pair in _navigation) { pair.Value.BackColor = pair.Key == view ? Color.FromArgb(38,38,38) : Surface; pair.Value.Invalidate(); }
        Notice("Loading…");
        var sections = new List<Control>();
        try
        {
            switch (view)
            {
                case "Friends": await Friends(sections); break;
                case "Incoming": await Incoming(sections); break;
                case "Settings": await Settings(sections); break;
                default: await Overview(sections); break;
            }
            if (IsDisposed || generation != _generation) { foreach (var c in sections) c.Dispose(); return; }
            _content.SuspendLayout();
            foreach (Control old in _content.Controls.Cast<Control>().ToArray()) old.Dispose();
            _content.Controls.AddRange(sections.ToArray()); ResizeRows(); _content.ResumeLayout(true);
            var inset = _content.Padding;
            await _motion.Animate(_content, progress => _content.Padding = new Padding(inset.Left, 18 + (int)Math.Round(6 * (1-progress)), inset.Right, inset.Bottom), 150);
            if (IsDisposed || generation != _generation) return;
            _edited.Clear(); Notice("Ready · Sharing continues when this window is closed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        { foreach (var c in sections) c.Dispose(); if (!IsDisposed && generation == _generation) Notice("Could not load this page. Use Refresh to retry."); }
    }
    private async Task Saved(string message, string? view = null)
    {
        if (_dirty) { Notice(message + " Other unsaved choices have been kept."); return; }
        await NavigateAsync(view ?? _view, true); Notice(message);
    }

    private async Task Overview(List<Control> rows)
    {
        var status = await Get("/api/status"); var mount = await Get("/api/media-mount/status");
        var section = Section(Flag(status,"serverConnected") ? TextOf(status,"serverName","Your Plex") : "Connect your Plex server", "Share selected libraries with people you know. Each owner controls their own media.");
        section.Controls.Add(ServiceLabel("Plex"));
        section.Controls.Add(Label($"{TextOf(status,"peerCount","0")} friends     ·     {Rows(status["libraries"]).Count(l => Flag(l,"shared"))} shared libraries     ·     {TextOf(status,"importPeerCount","0")} incoming connections", 11, true));
        section.Controls.Add(Actions(ActionButton(Flag(status,"serverConnected") ? "Manage friends" : "Connect Plex", () => NavigateAsync(Flag(status,"serverConnected") ? "Friends" : "Settings"), true), ActionButton("Incoming libraries", () => NavigateAsync("Incoming"))));
        rows.Add(section);
        var setup = Section("Setup & status");
        setup.Controls.Add(Label((Flag(status,"serverConnected") ? "✓  " : "1.  ") + "Plex server: " + TextOf(status,"serverName","Not connected"), 10, true));
        var address = await Get("/api/public-url");
        setup.Controls.Add(Label("2.  Public address: " + TextOf(address,"publicUrl","Not configured")));
        setup.Controls.Add(Label("3.  Media folder: " + (Flag(mount,"ready") ? "Ready" : TextOf(mount,"message","Not started"))));
        setup.Controls.Add(Actions(ActionButton("Connection settings", () => NavigateAsync("Settings")), ActionButton("Set up imports", () => NavigateAsync("Incoming"))));
        rows.Add(setup);
    }

    private async Task Friends(List<Control> rows)
    {
        var status = await Get("/api/status");
        var library = Section("Libraries to share", "These choices apply to your connected friends. Imported libraries cannot be shared onward.");
        var libraryChoices = new Dictionary<CheckBox, (string Key, bool Saved)>();
        foreach (var item in Rows(status["libraries"]))
        {
            var imported = Flag(item,"imported"); var check = Check(library, TextOf(item,"title") + (imported ? " · imported, cannot share" : ""), Flag(item,"shared"), !imported);
            if (!imported) libraryChoices.Add(check, (TextOf(item,"sectionKey"), check.Checked));
        }
        library.Controls.Add(ActionButton("Save sharing", async () =>
        {
            foreach (var choice in libraryChoices.ToArray())
            {
                if (choice.Key.Checked != choice.Value.Saved)
                    await Post("/api/libraries/toggle", new { sectionKey = choice.Value.Key, shared = choice.Key.Checked });
                libraryChoices[choice.Key] = (choice.Value.Key, choice.Key.Checked);
                _edited.Remove(choice.Key);
            }
            Notice("Sharing updated. Only the checked libraries are available to your friends.");
        }, true));
        library.Controls.Add(ActionButton("Refresh Plex libraries", async () => { await Post("/api/libraries/refresh"); await Saved("Libraries refreshed."); })); rows.Add(library);
        var connect = Section("Add a friend", "Enter their server address. They accept the request, then both of you choose incoming libraries.");
        var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Canvas, ForeColor = Ink, AccessibleName = "Friend server type" };
        kind.Items.AddRange(new object[] { "Jellyfin", "Plex Companion" }); kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) => Accent(kind.SelectedIndex == 1 ? "Plex" : "Jellyfin"); connect.Controls.Add(kind);
        var url = Field(connect,"Their public HTTPS address");
        connect.Controls.Add(ActionButton("Send request", async () => { await Post(kind.SelectedIndex == 1 ? "/api/companion/invite" : "/api/connect/invite", new { url = url.Text.Trim() }); _edited.Remove(url); await Saved("Request sent. Your friend must accept it."); }, true)); rows.Add(connect);
        var requests = Section("Friend requests"); requests.Controls.Add(ActionButton("Check requests", async () => { await Post("/api/companion/check"); await Saved("Requests refreshed."); }));
        foreach (var request in Rows(await Get("/api/companion/requests")).Where(r => TextOf(r,"status") != "accepted"))
        {
            requests.Controls.Add(Label(TextOf(request,"name","Plex Companion") + " · " + TextOf(request,"status"), 10, true));
            requests.Controls.Add(Label(TextOf(request,"url")));
            if (!string.IsNullOrEmpty(TextOf(request,"error"))) requests.Controls.Add(Label(TextOf(request,"error")));
            if (TextOf(request,"status") == "pending")
                foreach (var decision in Flag(request,"outgoing") ? new[] { "cancel" } : new[] { "accept", "decline" })
                    requests.Controls.Add(ActionButton(char.ToUpperInvariant(decision[0]) + decision[1..] + " request", async () => { await Post($"/api/companion/requests/{Id(request)}/{decision}"); await Saved("Request updated."); }));
        }
        rows.Add(requests);
        foreach (var invite in Rows(await Get("/api/pools/invites")))
        {
            var pool = Section("Pool invite: " + TextOf(invite,"poolName"),TextOf(invite,"ownerName") + " · " + TextOf(invite,"memberCount") + " members");
            pool.Controls.Add(Actions(ActionButton("Accept pool invite",async()=>{await Post("/api/pools/invites/"+Uri.EscapeDataString(TextOf(invite,"inviteId"))+"/accept");await Saved("Pool invite accepted.");}),ActionButton("Decline",async()=>{await Post("/api/pools/invites/"+Uri.EscapeDataString(TextOf(invite,"inviteId"))+"/reject");await Saved("Pool invite declined.");})));
            rows.Add(pool);
        }
        foreach (var peer in Rows(await Get("/api/peers")).Where(p => !Flag(p,"pendingConnection")))
        {
            var service = Flag(peer,"companionConnection") ? "Plex" : "Jellyfin";
            var friend = Section(TextOf(peer,"name","Friend")); friend.Controls.Add(ServiceLabel(service));
            friend.Controls.Add(Label("Your Plex → them: " + string.Join(", ", Rows(peer["sharedLibraries"]).Select(n => n.ToString())), 10, true));
            friend.Controls.Add(Label("Their " + service + " → you: " + (string.IsNullOrEmpty(TextOf(peer,"returnImportId")) ? "Waiting for their return connection" : Flag(peer,"returnSharePending") ? "Choose libraries in Incoming" : "Import connected")));
            var downloads = Check(friend,"Allow individual downloads",Flag(peer,"allowDownloads"));
            var bulk = Check(friend,"Allow bulk downloads",Flag(peer,"allowBulkDownloads"),downloads.Checked);
            downloads.CheckedChanged += (_, _) => { bulk.Enabled = downloads.Checked; if (!downloads.Checked) bulk.Checked = false; };
            friend.Controls.Add(Actions(ActionButton("Save access", async () => { await Post($"/api/peers/{Id(peer)}/download-access",new { allowDownloads=downloads.Checked,allowBulkDownloads=bulk.Checked }); _edited.Remove(downloads); _edited.Remove(bulk); Notice("Access saved."); }), ActionButton("Incoming libraries", () => NavigateAsync("Incoming")), ActionButton("Disconnect", async () => { if (MessageBox.Show(this,"Disconnect this friend in both directions? Their access will stop and their import will leave your media folder.","Disconnect friend",MessageBoxButtons.YesNo,MessageBoxIcon.Warning) != DialogResult.Yes) return; await Delete($"/api/peers/{Id(peer)}"); await Saved("Friend disconnected."); },danger:true)));
            BindService(friend,service); rows.Add(friend);
        }
    }

    private async Task Incoming(List<Control> rows)
    {
        var mount = await Get("/api/media-mount/status");
        var folder = Section("Media folder", "Plex reads your selected imports here. First setup may download the media helper and ask Windows to approve the WinFsp driver.");
        folder.Controls.Add(Label(Flag(mount,"ready") ? "Ready · " + TextOf(mount,"mediaMountRoot") : TextOf(mount,"message","Not started"),10,true));
        folder.Controls.Add(Actions(ActionButton(Flag(mount,"ready") ? "Restart media folder" : "Set up / start media folder", async () => { if (Flag(mount,"ready")) await Post("/api/media-mount/stop"); var result=await Post("/api/media-mount/start"); await Saved(TextOf(result,"message",Flag(result,"ready") ? "Media folder ready." : "Media folder needs attention.")); },true), ActionButton("Stop media folder",async () => { var result=await Post("/api/media-mount/stop"); await Saved(TextOf(result,"message","Media folder stopped.")); })));
        rows.Add(folder);
        var peers = Rows(await Get("/api/import/peers")).ToArray();
        if (peers.Length == 0) rows.Add(Section("No incoming libraries", "Add a friend from Friends. Their return connection appears here after they accept."));
        foreach (var peer in peers)
        {
            var kind = TextOf(peer,"sourceKind","Jellyfin");
            if (peer == peers[0]) Accent(kind);
            var section = Section(TextOf(peer,"name","Friend"),TextOf(peer,"url")); section.Controls.Add(ServiceLabel(kind));
            section.Controls.Add(Label($"{TextOf(peer,"lastItemCount","0")} catalog items · {TextOf(peer,"mountedItemCount","0")} playable files",10,true));
            section.Controls.Add(Label(Flag(peer,"returnSharePending") ? "Choose libraries to begin. Nothing is imported yet." : "Last sync: " + TextOf(peer,"lastSyncUtc","Not synced yet")));
            if (!string.IsNullOrEmpty(TextOf(peer,"lastError"))) section.Controls.Add(Label(TextOf(peer,"lastError")));
            var picker = new FlowLayoutPanel { FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,Margin=new Padding(0) };
            var selection = new List<(string Id, CheckBox Check)>();
            void Fill(JsonNode source)
            {
                foreach (Control old in picker.Controls.Cast<Control>().ToArray()) { _edited.Remove(old); old.Dispose(); } selection.Clear();
                var ids = source["selectedLibraryIds"];
                foreach (var library in Rows(source["availableLibraries"]))
                {
                    var id=TextOf(library,"id");
                    var check=Check(picker,TextOf(library,"name"),!Flag(source,"returnSharePending") && (ids == null || Rows(ids).Any(n => n.ToString()==id)));
                    selection.Add((id,check));
                }
                if (selection.Count == 0) picker.Controls.Add(Label("No libraries listed. Refresh available libraries after your friend chooses what to share."));
            }
            Fill(peer); section.Controls.Add(picker);
            section.Controls.Add(Actions(ActionButton("Refresh available libraries",async () => { if (_dirty && MessageBox.Show(this,"Replace unsaved selections with the current saved choices?","Refresh libraries",MessageBoxButtons.YesNo) != DialogResult.Yes) return; var result=await Get($"/api/import/peers/{Id(peer)}/libraries"); if (picker.IsDisposed) return; Fill(result); ResizeRows(); Notice("Select libraries, then save and sync."); }),ActionButton("Save selection & sync",async () =>
            {
                Notice("Syncing selected libraries…");
                var result=await Post($"/api/import/peers/{Id(peer)}/libraries",new { libraryIds=selection.Where(s=>s.Check.Checked).Select(s=>s.Id).ToArray() });
                if (!string.IsNullOrEmpty(TextOf(result,"lastError"))) { Notice(TextOf(result,"lastError")); return; }
                foreach (var choice in selection) _edited.Remove(choice.Check);
                if (Flag(mount,"ready") && selection.Any(s=>s.Check.Checked)) await Post($"/api/import/peers/{Id(peer)}/add-to-plex");
                await Saved(Flag(mount,"ready") ? "Selected libraries synced and attached to Plex." : "Selection synced. Start the media folder, then choose Add to Plex.");
            },true)));
            section.Controls.Add(Actions(ActionButton("Add to Plex",async () => { await Post($"/api/import/peers/{Id(peer)}/add-to-plex"); await Saved("Libraries added to Plex. Plex will scan them."); }),ActionButton("Sync now",async () => { var result=await Post($"/api/import/peers/{Id(peer)}/sync"); await Saved(TextOf(result,"lastError","Sync complete.")); }),ActionButton("View import issues",async () =>
            {
                var catalog=await Get($"/api/import/peers/{Id(peer)}/catalog");
                ShowDetails("Source catalog",string.Join(Environment.NewLine,Rows(catalog["items"]).Select(item=>TextOf(item,"title") + " · " + TextOf(item,"issue","Ready"))));
            }),ActionButton("Remove import",async () => { if (MessageBox.Show(this,"Remove this import from your media folder? Your friend's original files will stay on their server.","Remove import",MessageBoxButtons.YesNo,MessageBoxIcon.Warning) != DialogResult.Yes) return; await Delete($"/api/import/peers/{Id(peer)}?removeFiles=true"); await Saved("Import removed."); },danger:true)));
            BindService(section,kind); rows.Add(section);
        }
        var manual=Section("Have a Jellyfin import code?", "Optional for an existing connection. New friend requests connect both directions automatically.");
        var code=Field(manual,"Import code",secret:true);
        var codePicker=new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false };
        var codeChoices=new List<(string Id,CheckBox Check)>(); string? previewedCode=null;
        var importButton=ActionButton("Import selected libraries",async () =>
        {
            if (previewedCode == null || code.Text.Trim()!=previewedCode) { Notice("Preview this code first."); return; }
            var chosen=codeChoices.Where(c=>c.Check.Checked).Select(c=>c.Id).ToArray();
            if (chosen.Length==0) { Notice("Select at least one library to import."); return; }
            await Post("/api/import/connect",new { code=previewedCode,libraryIds=chosen }); _edited.Remove(code); foreach(var choice in codeChoices) _edited.Remove(choice.Check); await Saved("Libraries imported. Use Add to Plex once the media folder is ready.");
        },true); importButton.Enabled=false;
        code.TextChanged+=(_,_)=>{ previewedCode=null; importButton.Enabled=false; };
        manual.Controls.Add(ActionButton("Preview libraries",async () =>
        {
            var submitted=code.Text.Trim();var preview=await Post("/api/import/preview",new { code=submitted });
            if (codePicker.IsDisposed || code.Text.Trim()!=submitted) return;
            foreach(Control old in codePicker.Controls.Cast<Control>().ToArray())old.Dispose();codeChoices.Clear();
            foreach(var library in Rows(preview["libraries"]))codeChoices.Add((TextOf(library,"id"),Check(codePicker,TextOf(library,"name"),false)));
            previewedCode=submitted;importButton.Enabled=codeChoices.Count>0;ResizeRows();
        }));manual.Controls.Add(codePicker);manual.Controls.Add(importButton);rows.Add(Collapsed(manual));
    }

    private async Task Settings(List<Control> rows)
    {
        var status=await Get("/api/status");
        var plex=Section("Plex server",TextOf(status,"serverName","Not connected"));plex.Controls.Add(ServiceLabel("Plex"));
        plex.Controls.Add(ActionButton("Sign in with Plex",async () =>
        {
            var start=await Post("/api/plex/start-signin");
            if (!Flag(start,"openedExternally")) { Notice("Could not open Plex sign-in. Check your default browser and retry.");return; }
            Notice("Approve access in your browser. Waiting for Plex…");
            for(var attempt=0;attempt<100;attempt++)
            { await Task.Delay(3000,_closed.Token); if(Flag(await Get("/api/plex/poll-signin"),"complete")){await Saved("Plex connected.","Settings");return;} }
            Notice("Plex sign-in timed out. Click Sign in with Plex to retry.");
        },true));
        plex.Controls.Add(ActionButton("Choose another owned server",async () =>
        {
            var servers=Rows(await Get("/api/plex/servers")).Where(s=>Flag(s,"owned")).ToArray();
            using var dialog=Dialog("Choose your Plex server");
            var list=new ListBox { Dock=DockStyle.Fill,BackColor=Canvas,ForeColor=Ink,BorderStyle=BorderStyle.None,DisplayMember=nameof(ServerChoice.Name) };
            foreach(var server in servers)list.Items.Add(new ServerChoice(TextOf(server,"id"),TextOf(server,"name")));
            var select=new Button { Text="Use selected server",Dock=DockStyle.Bottom,Height=42,DialogResult=DialogResult.OK };
            dialog.Controls.Add(list);dialog.Controls.Add(select);dialog.AcceptButton=select;
            if(dialog.ShowDialog(this)==DialogResult.OK && list.SelectedItem is ServerChoice selected){await Post("/api/plex/servers/select",new { id=selected.Id });await Saved("Plex server selected.");}
        }));rows.Add(plex);
        var address=await Get("/api/public-url");
        var network=Section("Public connection","For friends outside your home. Open Tailscale, sign in, then enable Funnel. Companion configures its own listening port.");
        var publicUrl=Field(network,"Your public HTTPS address",TextOf(address,"publicUrl"));
        network.Controls.Add(Actions(ActionButton("Save address",async()=>{await Post("/api/public-url",new { url=publicUrl.Text.Trim() });_edited.Remove(publicUrl);Notice("Public address saved.");}),ActionButton("Open Tailscale",async()=>{Notice(TextOf(await Post("/api/tailscale/open"),"message"));}),ActionButton("Get Tailscale",()=>{CompanionShell.OpenUrl("https://tailscale.com/download/windows");return Task.CompletedTask;}),ActionButton("Enable Funnel",async()=>{var result=await Post("/api/tailscale/funnel");await Saved(TextOf(result,"message","Funnel configured."));})));
        network.Controls.Add(ActionButton("Test connection",async()=>{var result=await Post("/api/diagnostics");ShowDetails("Connection checks",string.Join(Environment.NewLine,Rows(result).Select(c=>TextOf(c,"stage")+": "+TextOf(c,"message"))));}));rows.Add(network);
        var app=await Get("/api/app/info");
        var preferences=Section("App","Closing the window leaves the tray and media folder running. Exit Companion stops sharing until you open it again.");
        preferences.Controls.Add(Label("Version "+TextOf(app,"version")+" · "+TextOf(app,"revision")+" · "+TextOf(app,"workingSetMb")+" MB"));
        var startup=Check(preferences,"Start with Windows",Flag(app,"autostartEnabled"),Flag(app,"autostartSupported"));
        preferences.Controls.Add(Actions(ActionButton("Save startup preference",async()=>{await Post("/api/app/autostart",new { enabled=startup.Checked });_edited.Remove(startup);Notice("Startup preference saved.");}),ActionButton("Open log folder",()=>{CompanionShell.OpenFolder(Path.GetDirectoryName(_runtime.LogPath) ?? _runtime.InstallDirectory);return Task.CompletedTask;}),ActionButton("Check for updates",async()=>
        {
            var update=await Get("/api/update/status");
            if(!Flag(update,"updateAvailable")){Notice(TextOf(update,"error","This build is up to date."));return;}
            if(MessageBox.Show(this,"Install the available update and restart Companion?","Update Companion",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
            Notice(TextOf(await Post("/api/update/apply"),"message","Restarting…"));
        }),ActionButton("Exit Companion",async()=>{if(MessageBox.Show(this,"Stop sharing and close Companion?","Exit Companion",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;await Post("/api/app/exit");},danger:true)));
        rows.Add(preferences);
        var advanced=Section("Advanced connection","Use a local server token only if Plex sign-in cannot reach your own server.");
        var localUrl=Field(advanced,"Local Plex address","http://127.0.0.1:32400");var token=Field(advanced,"Plex server token",secret:true);
        advanced.Controls.Add(ActionButton("Connect local Plex",async()=>{await Post("/api/plex/connect-local",new { url=localUrl.Text.Trim(),token=token.Text.Trim() });token.Clear();_edited.Remove(token);_edited.Remove(localUrl);await Saved("Local Plex connected.");}));
        var playback=Field(advanced,"Internal playback address",TextOf(status,"playbackBaseUrl"));
        advanced.Controls.Add(ActionButton("Save playback address",async()=>{await Post("/api/playback-url",new { url=playback.Text.Trim() });_edited.Remove(playback);Notice("Playback address saved.");}));rows.Add(Collapsed(advanced));
    }
    private sealed record ServerChoice(string Id,string Name);
    private Form Dialog(string title)=>new() { Text=title,StartPosition=FormStartPosition.CenterParent,Size=new Size(640,440),BackColor=Canvas,ForeColor=Ink,Font=Font,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false };
    private void ShowDetails(string title,string text)
    { if(IsDisposed)return;using var dialog=Dialog(title);dialog.Controls.Add(new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Canvas,ForeColor=Ink,Text=text,BorderStyle=BorderStyle.None});dialog.ShowDialog(this); }
    protected override void Dispose(bool disposing)
    {
        if(disposing && !IsDisposed){_closed.Cancel();_motion.Dispose();_accentTimer.Dispose();_statusTimer.Dispose();_api.Dispose();_closed.Dispose();}
        base.Dispose(disposing);
        if(disposing)foreach(var font in _fonts.Values)font.Dispose();
    }
}
#endif
