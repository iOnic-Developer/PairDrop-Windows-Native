using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Text;
using System.Text.Json;

namespace PairDropNative;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Control dispatcher;
    private readonly string settingsDir;
    private readonly string settingsFile;

    private AppSettings settings;
    private PairDropClient? client;
    private ToolStripMenuItem statusItem = null!;
    private ToolStripMenuItem peersItem = null!;
    private ToolStripMenuItem sendClipboardItem = null!;
    private ToolStripMenuItem sendFilesItem = null!;
    private bool quitting;

    public TrayAppContext()
    {
        dispatcher = new Control();
        dispatcher.CreateControl();

        settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PairDropNative");
        settingsFile = Path.Combine(settingsDir, "settings.json");

        settings = LoadSettings();

        var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                   ?? SystemIcons.Application;

        tray = new NotifyIcon
        {
            Icon = icon,
            Text = "PairDrop Native",
            Visible = true
        };

        BuildMenu();

        if (string.IsNullOrWhiteSpace(settings.PairDropUrl))
        {
            if (!ShowSettings(firstRun: true))
            {
                Quit();
                return;
            }
        }

        ApplyStartup(settings.StartWithWindows);
        StartClient();
    }

    private void BuildMenu()
    {
        var menu = new ContextMenuStrip();

        statusItem = new ToolStripMenuItem("Disconnected") { Enabled = false };
        menu.Items.Add(statusItem);

        peersItem = new ToolStripMenuItem("Devices");
        menu.Items.Add(peersItem);

        menu.Items.Add(new ToolStripSeparator());

        sendClipboardItem = new ToolStripMenuItem("Send clipboard to");
        sendFilesItem = new ToolStripMenuItem("Send files to");
        menu.Items.Add(sendClipboardItem);
        menu.Items.Add(sendFilesItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Open downloads", null, (_, _) => OpenDownloads());
        menu.Items.Add("Open PairDrop website", null, (_, _) => OpenPairDropWebsite());
        menu.Items.Add("Settings...", null, (_, _) =>
        {
            if (ShowSettings(firstRun: false))
                RestartClient();
        });

        menu.Items.Add("Reconnect", null, (_, _) => RestartClient());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenDownloads();

        RefreshPeerMenus(Array.Empty<PeerInfo>());
    }

    private void StartClient()
    {
        if (quitting) return;

        client = new PairDropClient(
            settings.PairDropUrl,
            settings.PeerId,
            settings.PeerIdHash,
            () => settings.AutoAccept,
            () => settings.DownloadFolder);

        client.ConnectionChanged += connected => Ui(() =>
        {
            statusItem.Text = connected ? "Connected" : "Disconnected";
            tray.Text = connected ? "PairDrop Native - Connected" : "PairDrop Native";
        });

        client.IdentityChanged += (peerId, peerIdHash, displayName, deviceName) => Ui(() =>
        {
            settings.PeerId = peerId;
            settings.PeerIdHash = peerIdHash;
            SaveSettings();
            statusItem.Text = $"Connected as {displayName}";
        });

        client.PeersChanged += peers => Ui(() => RefreshPeerMenus(peers));

        client.TextReceived += (peer, text) => Ui(() =>
        {
            if (settings.CopyReceivedTextToClipboard)
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch { }
            }

            PlayReceiveSound();

            if (settings.Notifications)
            {
                var preview = text.Replace("\r", " ").Replace("\n", " ");
                if (preview.Length > 180) preview = preview[..180] + "…";

                Notify(
                    $"Clipboard received from {peer.DisplayName}",
                    settings.CopyReceivedTextToClipboard
                        ? $"Copied to clipboard\n{preview}"
                        : preview);
            }
        });

        client.FilesReceived += (peer, paths) => Ui(() =>
        {
            PlayReceiveSound();

            if (!settings.Notifications) return;

            var allImages = paths.Count > 0 && paths.All(IsImagePath);
            var title = allImages
                ? (paths.Count == 1
                    ? $"Image received from {peer.DisplayName}"
                    : $"Images received from {peer.DisplayName}")
                : (paths.Count == 1
                    ? $"File received from {peer.DisplayName}"
                    : $"Files received from {peer.DisplayName}");

            var body = paths.Count == 1
                ? $"{Path.GetFileName(paths[0])}\nSaved to {settings.DownloadFolder}"
                : $"{paths.Count} files saved to {settings.DownloadFolder}";

            Notify(title, body);
        });

        client.TransferSent += (peer, description) => Ui(() =>
        {
            if (settings.Notifications)
                Notify($"Sent to {peer.DisplayName}", description);
        });

        client.Error += message => Ui(() =>
        {
            statusItem.Text = "Connection error";
            if (settings.Notifications)
                Notify("PairDrop Native", message);
        });

        _ = client.StartAsync();
    }

    private async void RestartClient()
    {
        var old = client;
        client = null;

        if (old is not null)
            await old.DisposeAsync();

        RefreshPeerMenus(Array.Empty<PeerInfo>());
        statusItem.Text = "Reconnecting...";
        StartClient();
    }

    private void RefreshPeerMenus(IReadOnlyList<PeerInfo> peers)
    {
        peersItem.DropDownItems.Clear();
        sendClipboardItem.DropDownItems.Clear();
        sendFilesItem.DropDownItems.Clear();

        if (peers.Count == 0)
        {
            peersItem.DropDownItems.Add(new ToolStripMenuItem("No devices") { Enabled = false });
            sendClipboardItem.DropDownItems.Add(new ToolStripMenuItem("No devices") { Enabled = false });
            sendFilesItem.DropDownItems.Add(new ToolStripMenuItem("No devices") { Enabled = false });
            return;
        }

        foreach (var peer in peers.OrderBy(p => p.DisplayName))
        {
            var label = string.IsNullOrWhiteSpace(peer.DeviceName)
                ? peer.DisplayName
                : $"{peer.DisplayName} — {peer.DeviceName}";

            peersItem.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });

            var clipboardPeer = peer;
            sendClipboardItem.DropDownItems.Add(label, null, async (_, _) =>
            {
                string text;
                try
                {
                    text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                }
                catch
                {
                    text = "";
                }

                if (string.IsNullOrEmpty(text))
                {
                    Notify("PairDrop Native", "Clipboard does not contain text.");
                    return;
                }

                try
                {
                    if (client is not null)
                        await client.SendTextAsync(clipboardPeer.Id, text);
                }
                catch (Exception ex)
                {
                    Notify("Send failed", ex.Message);
                }
            });

            var filePeer = peer;
            sendFilesItem.DropDownItems.Add(label, null, async (_, _) =>
            {
                using var picker = new OpenFileDialog
                {
                    Multiselect = true,
                    Title = $"Send files to {filePeer.DisplayName}"
                };

                if (picker.ShowDialog() != DialogResult.OK || picker.FileNames.Length == 0)
                    return;

                try
                {
                    if (client is not null)
                        await client.SendFilesAsync(filePeer.Id, picker.FileNames);
                }
                catch (Exception ex)
                {
                    Notify("Send failed", ex.Message);
                }
            });
        }
    }

    private void PlayReceiveSound()
    {
        if (!settings.PlaySoundOnReceive)
            return;

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }

    private static bool IsImagePath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or
            ".bmp" or ".tif" or ".tiff" or ".heic" or ".avif";
    }

    private void Notify(string title, string body)
    {
        if (quitting) return;

        if (body.Length > 240)
            body = body[..240] + "…";

        tray.BalloonTipTitle = title;
        tray.BalloonTipText = body;
        tray.BalloonTipIcon = ToolTipIcon.Info;
        tray.ShowBalloonTip(7000);
    }

    private bool ShowSettings(bool firstRun)
    {
        using var dialog = new SettingsDialog(settings);
        var result = dialog.ShowDialog();

        if (result != DialogResult.OK)
            return false;

        settings = dialog.Settings;
        SaveSettings();
        ApplyStartup(settings.StartWithWindows);
        Directory.CreateDirectory(settings.DownloadFolder);

        return true;
    }

    private void OpenDownloads()
    {
        try
        {
            Directory.CreateDirectory(settings.DownloadFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = settings.DownloadFolder,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OpenPairDropWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = settings.PairDropUrl,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(settingsDir);
            if (!File.Exists(settingsFile))
                return AppSettings.CreateDefault();

            var loaded = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(settingsFile));

            if (loaded is null)
                return AppSettings.CreateDefault();

            if (string.IsNullOrWhiteSpace(loaded.DownloadFolder))
                loaded.DownloadFolder = AppSettings.DefaultDownloadFolder();

            return loaded;
        }
        catch
        {
            return AppSettings.CreateDefault();
        }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(
            settingsFile,
            JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ApplyStartup(bool enabled)
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue("PairDropNative", $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue("PairDropNative", false);
        }
    }

    private void Ui(Action action)
    {
        if (quitting || dispatcher.IsDisposed) return;

        try
        {
            if (dispatcher.InvokeRequired)
                dispatcher.BeginInvoke(action);
            else
                action();
        }
        catch { }
    }

    private async void Quit()
    {
        if (quitting) return;
        quitting = true;

        tray.Visible = false;

        if (client is not null)
            await client.DisposeAsync();

        tray.Dispose();
        dispatcher.Dispose();
        ExitThread();
    }
}


internal sealed class SettingsDialog : Form
{
    private static readonly Color WindowBack = Color.FromArgb(14, 19, 28);
    private static readonly Color HeaderBack = Color.FromArgb(9, 14, 22);
    private static readonly Color CardBack = Color.FromArgb(19, 27, 39);
    private static readonly Color TextColor = Color.FromArgb(242, 246, 252);
    private static readonly Color MutedTextColor = Color.FromArgb(159, 171, 190);
    private static readonly Color AccentColor = Color.FromArgb(25, 126, 230);
    private static readonly Color BorderColor = Color.FromArgb(48, 61, 82);

    private readonly ModernTextBox urlBox;
    private readonly ModernTextBox downloadBox;
    private readonly DarkCheckBox autoAccept;
    private readonly DarkCheckBox copyText;
    private readonly DarkCheckBox notifications;
    private readonly DarkCheckBox receiveSound;
    private readonly DarkCheckBox startup;
    private readonly ModernButton browse;
    private readonly ModernButton save;
    private readonly ModernButton cancel;
    private readonly RoundedPanel receiveCard;
    private readonly Label note;

    public AppSettings Settings { get; private set; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    public SettingsDialog(AppSettings current)
    {
        Settings = current.Clone();

        Text = "PairDrop Native Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = WindowBack;
        ForeColor = TextColor;

        // The previous version mixed WinForms DPI autoscaling with controls that
        // were already drawn manually. On 125/150% Windows scaling that caused
        // the huge window + tiny fixed-width controls seen in the screenshot.
        // Keep this custom UI in one consistent pixel coordinate system instead.
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 16F, FontStyle.Regular, GraphicsUnit.Pixel);

        ClientSize = new Size(980, 620);
        MinimumSize = new Size(996, 659);
        MaximumSize = new Size(996, 659);

        var header = new Panel
        {
            Left = 0,
            Top = 0,
            Width = ClientSize.Width,
            Height = 88,
            BackColor = HeaderBack,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var heading = new Label
        {
            Text = "PairDrop Native",
            Left = 32,
            Top = 14,
            Width = 700,
            Height = 36,
            Font = new Font("Segoe UI Semibold", 30F, FontStyle.Bold, GraphicsUnit.Pixel),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var subHeading = new Label
        {
            Text = "Native tray client for fast file and clipboard sharing.",
            Left = 34,
            Top = 52,
            Width = 760,
            Height = 24,
            Font = new Font("Segoe UI", 15F, FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft
        };

        header.Controls.Add(heading);
        header.Controls.Add(subHeading);

        var urlLabel = CreateSectionLabel("PairDrop URL", 32, 110);

        urlBox = new ModernTextBox
        {
            Left = 32,
            Top = 140,
            Width = 916,
            Height = 48,
            Value = current.PairDropUrl,
            AccentColor = AccentColor
        };

        var downloadLabel = CreateSectionLabel("Download folder", 32, 210);

        downloadBox = new ModernTextBox
        {
            Left = 32,
            Top = 240,
            Width = 776,
            Height = 48,
            Value = current.DownloadFolder,
            AccentColor = AccentColor
        };

        browse = new ModernButton
        {
            Text = "Browse",
            Left = 820,
            Top = 240,
            Width = 128,
            Height = 48,
            Primary = false
        };

        browse.Click += (_, _) =>
        {
            using var folder = new FolderBrowserDialog
            {
                SelectedPath = downloadBox.Value,
                Description = "Choose where PairDrop files are saved"
            };

            if (folder.ShowDialog() == DialogResult.OK)
                downloadBox.Value = folder.SelectedPath;
        };

        var receivingLabel = CreateSectionLabel("Receiving", 32, 314);

        receiveCard = new RoundedPanel
        {
            Left = 32,
            Top = 346,
            Width = 916,
            Height = 158,
            BackColor = CardBack,
            BorderColor = BorderColor,
            CornerRadius = 12
        };

        autoAccept = new DarkCheckBox
        {
            Text = "Automatically accept incoming files",
            Left = 24,
            Top = 22,
            Width = 410,
            Height = 42,
            Checked = current.AutoAccept,
            AccentColor = AccentColor
        };

        notifications = new DarkCheckBox
        {
            Text = "Show Windows notifications",
            Left = 468,
            Top = 22,
            Width = 410,
            Height = 42,
            Checked = current.Notifications,
            AccentColor = AccentColor
        };

        copyText = new DarkCheckBox
        {
            Text = "Automatically copy received text to clipboard",
            Left = 24,
            Top = 82,
            Width = 420,
            Height = 42,
            Checked = current.CopyReceivedTextToClipboard,
            AccentColor = AccentColor
        };

        receiveSound = new DarkCheckBox
        {
            Text = "Play a sound when text / files arrive",
            Left = 468,
            Top = 82,
            Width = 420,
            Height = 42,
            Checked = current.PlaySoundOnReceive,
            AccentColor = AccentColor
        };

        receiveCard.Controls.AddRange(new Control[]
        {
            autoAccept,
            notifications,
            copyText,
            receiveSound
        });

        startup = new DarkCheckBox
        {
            Text = "Start PairDrop Native with Windows",
            Left = 32,
            Top = 526,
            Width = 430,
            Height = 42,
            Checked = current.StartWithWindows,
            AccentColor = AccentColor,
            BackColor = WindowBack
        };

        note = new Label
        {
            Text = "Runs silently in the tray with no embedded browser.",
            Left = 34,
            Top = 570,
            Width = 570,
            Height = 24,
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 15F, FontStyle.Regular, GraphicsUnit.Pixel),
            TextAlign = ContentAlignment.MiddleLeft
        };

        save = new ModernButton
        {
            Text = "Save",
            Left = 716,
            Top = 548,
            Width = 108,
            Height = 48,
            Primary = true,
            DialogResult = DialogResult.OK
        };

        cancel = new ModernButton
        {
            Text = "Cancel",
            Left = 840,
            Top = 548,
            Width = 108,
            Height = 48,
            Primary = false,
            DialogResult = DialogResult.Cancel
        };

        save.Click += (_, _) =>
        {
            var url = urlBox.Value.Trim().TrimEnd('/');

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp
                    && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(
                    "Enter the full PairDrop URL, e.g. https://drop.example.com",
                    "PairDrop Native",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                DialogResult = DialogResult.None;
                return;
            }

            if (string.IsNullOrWhiteSpace(downloadBox.Value))
            {
                MessageBox.Show(
                    "Choose a download folder.",
                    "PairDrop Native",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                DialogResult = DialogResult.None;
                return;
            }

            Settings.PairDropUrl = url;
            Settings.DownloadFolder = downloadBox.Value.Trim();
            Settings.AutoAccept = autoAccept.Checked;
            Settings.CopyReceivedTextToClipboard = copyText.Checked;
            Settings.Notifications = notifications.Checked;
            Settings.PlaySoundOnReceive = receiveSound.Checked;
            Settings.StartWithWindows = startup.Checked;
        };

        Controls.AddRange(new Control[]
        {
            header,
            urlLabel,
            urlBox,
            downloadLabel,
            downloadBox,
            browse,
            receivingLabel,
            receiveCard,
            startup,
            note,
            save,
            cancel
        });

        AcceptButton = save;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            int enabled = 1;

            int result = DwmSetWindowAttribute(
                Handle,
                20, // DWMWA_USE_IMMERSIVE_DARK_MODE
                ref enabled,
                sizeof(int));

            if (result != 0)
            {
                DwmSetWindowAttribute(
                    Handle,
                    19,
                    ref enabled,
                    sizeof(int));
            }

            int cornerPreference = 2; // rounded
            DwmSetWindowAttribute(
                Handle,
                33,
                ref cornerPreference,
                sizeof(int));
        }
        catch
        {
            // Normal title bar on unsupported Windows versions.
        }
    }

    private static Label CreateSectionLabel(string text, int left, int top)
    {
        return new Label
        {
            Text = text,
            Left = left,
            Top = top,
            Width = 820,
            Height = 26,
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold, GraphicsUnit.Pixel),
            TextAlign = ContentAlignment.MiddleLeft
        };
    }
}

internal sealed class RoundedPanel : Panel
{
    public Color BorderColor { get; set; } = Color.FromArgb(48, 61, 82);
    public int CornerRadius { get; set; } = 12;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(
            0,
            0,
            Math.Max(1, Width - 1),
            Math.Max(1, Height - 1));

        using var path = UiDrawing.RoundRect(rect, CornerRadius);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(BorderColor);

        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class ModernTextBox : UserControl
{
    private readonly TextBox inner;
    private bool focused;

    public Color AccentColor { get; set; } = Color.FromArgb(25, 126, 230);

    public string Value
    {
        get => inner.Text;
        set => inner.Text = value ?? "";
    }

    public ModernTextBox()
    {
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(12, 18, 27);
        Padding = new Padding(12, 12, 12, 8);

        inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = Color.FromArgb(242, 246, 252),
            Font = new Font("Segoe UI", 18F, FontStyle.Regular, GraphicsUnit.Pixel),
            Dock = DockStyle.Fill
        };

        inner.Enter += (_, _) =>
        {
            focused = true;
            Invalidate();
        };

        inner.Leave += (_, _) =>
        {
            focused = false;
            Invalidate();
        };

        inner.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);

        Controls.Add(inner);
        Cursor = Cursors.IBeam;
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);

        if (inner is not null)
            inner.BackColor = BackColor;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(
            0,
            0,
            Math.Max(1, Width - 1),
            Math.Max(1, Height - 1));

        using var path = UiDrawing.RoundRect(rect, 9);
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(
            focused ? AccentColor : Color.FromArgb(54, 68, 91),
            focused ? 1.6F : 1F);

        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class ModernButton : Button
{
    private bool hover;
    private bool pressed;

    public bool Primary { get; set; }

    public ModernButton()
    {
        AutoSize = false;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold, GraphicsUnit.Pixel);
        DoubleBuffered = true;

        MouseEnter += (_, _) =>
        {
            hover = true;
            Invalidate();
        };

        MouseLeave += (_, _) =>
        {
            hover = false;
            pressed = false;
            Invalidate();
        };

        MouseDown += (_, _) =>
        {
            pressed = true;
            Invalidate();
        };

        MouseUp += (_, _) =>
        {
            pressed = false;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(14, 19, 28));

        Color background;

        if (Primary)
        {
            background = pressed
                ? Color.FromArgb(15, 100, 198)
                : hover
                    ? Color.FromArgb(38, 141, 247)
                    : Color.FromArgb(25, 126, 230);
        }
        else
        {
            background = pressed
                ? Color.FromArgb(31, 39, 54)
                : hover
                    ? Color.FromArgb(49, 60, 80)
                    : Color.FromArgb(37, 46, 63);
        }

        var rect = new Rectangle(
            0,
            0,
            Math.Max(1, Width - 1),
            Math.Max(1, Height - 1));

        using var path = UiDrawing.RoundRect(rect, 9);
        using var fill = new SolidBrush(background);

        pevent.Graphics.FillPath(fill, path);

        if (!Primary)
        {
            using var border = new Pen(Color.FromArgb(59, 73, 96));
            pevent.Graphics.DrawPath(border, path);
        }

        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine);
    }
}

internal sealed class DarkCheckBox : CheckBox
{
    public Color AccentColor { get; set; } = Color.FromArgb(25, 126, 230);

    public DarkCheckBox()
    {
        AutoSize = false;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        ForeColor = Color.FromArgb(242, 246, 252);
        BackColor = Color.FromArgb(19, 27, 39);
        Font = new Font("Segoe UI", 16F, FontStyle.Regular, GraphicsUnit.Pixel);
        Padding = new Padding(0);
        TextAlign = ContentAlignment.MiddleLeft;
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(BackColor);

        const int boxSize = 22;

        var boxRect = new Rectangle(
            0,
            Math.Max(0, (Height - boxSize) / 2),
            boxSize,
            boxSize);

        using var path = UiDrawing.RoundRect(boxRect, 5);

        if (Checked)
        {
            using var fill = new SolidBrush(AccentColor);
            pevent.Graphics.FillPath(fill, path);

            using var tick = new Pen(Color.White, 2.4F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            var y = boxRect.Top;

            pevent.Graphics.DrawLines(
                tick,
                new[]
                {
                    new Point(boxRect.Left + 5, y + 11),
                    new Point(boxRect.Left + 9, y + 15),
                    new Point(boxRect.Left + 17, y + 7)
                });
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(24, 33, 46));
            using var border = new Pen(Color.FromArgb(72, 88, 112));

            pevent.Graphics.FillPath(fill, path);
            pevent.Graphics.DrawPath(border, path);
        }

        var textRect = new Rectangle(
            34,
            0,
            Math.Max(0, Width - 34),
            Height);

        // No EndEllipsis here. These controls are deliberately wide enough for
        // their full labels, so the user's settings never show "..." again.
        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            textRect,
            ForeColor,
            TextFormatFlags.Left
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding);
    }
}

internal static class UiDrawing
{
    public static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0)
        {
            path.AddRectangle(rect);
            path.CloseFigure();
            return path;
        }

        int diameter = radius * 2;

        if (diameter > rect.Width)
            diameter = rect.Width;

        if (diameter > rect.Height)
            diameter = rect.Height;

        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);

        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);

        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}

internal sealed class AppSettings
{
    public string PairDropUrl { get; set; } = "";
    public string DownloadFolder { get; set; } = DefaultDownloadFolder();
    public bool AutoAccept { get; set; } = true;
    public bool CopyReceivedTextToClipboard { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool PlaySoundOnReceive { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    public string PeerId { get; set; } = "";
    public string PeerIdHash { get; set; } = "";

    public static AppSettings CreateDefault() => new();

    public static string DefaultDownloadFolder() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "PairDrop");

    public AppSettings Clone() => new()
    {
        PairDropUrl = PairDropUrl,
        DownloadFolder = DownloadFolder,
        AutoAccept = AutoAccept,
        CopyReceivedTextToClipboard = CopyReceivedTextToClipboard,
        Notifications = Notifications,
        PlaySoundOnReceive = PlaySoundOnReceive,
        StartWithWindows = StartWithWindows,
        PeerId = PeerId,
        PeerIdHash = PeerIdHash
    };
}

internal sealed record PeerInfo(
    string Id,
    string DisplayName,
    string DeviceName,
    string RoomType,
    string RoomId);

internal sealed class PairDropClient : IAsyncDisposable
{
    private readonly string baseUrl;
    private readonly Func<bool> autoAccept;
    private readonly Func<string> downloadFolder;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly object peerLock = new();

    private readonly Dictionary<string, PeerInfo> peers = new();
    private readonly ConcurrentDictionary<string, IncomingTransfer> incoming = new();
    private readonly ConcurrentDictionary<string, OutgoingTransfer> outgoing = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> outgoingText = new();

    private ClientWebSocket? ws;
    private string peerId;
    private string peerIdHash;
    private bool disposed;
    private bool wsFallbackEnabled = true;

    public event Action<bool>? ConnectionChanged;
    public event Action<string, string, string, string>? IdentityChanged;
    public event Action<IReadOnlyList<PeerInfo>>? PeersChanged;
    public event Action<PeerInfo, string>? TextReceived;
    public event Action<PeerInfo, IReadOnlyList<string>>? FilesReceived;
    public event Action<PeerInfo, string>? TransferSent;
    public event Action<string>? Error;

    public PairDropClient(
        string baseUrl,
        string peerId,
        string peerIdHash,
        Func<bool> autoAccept,
        Func<string> downloadFolder)
    {
        this.baseUrl = baseUrl.Trim().TrimEnd('/');
        this.peerId = peerId;
        this.peerIdHash = peerIdHash;
        this.autoAccept = autoAccept;
        this.downloadFolder = downloadFolder;
    }

    public async Task StartAsync()
    {
        while (!cts.IsCancellationRequested && !disposed)
        {
            try
            {
                await ConnectAndRunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex.Message);
            }

            ConnectionChanged?.Invoke(false);
            ClearPeers();

            if (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken token)
    {
        ws = new ClientWebSocket();

        try
        {
            ws.Options.SetRequestHeader(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36 PairDropNative/0.1");
        }
        catch { }

        var endpoint = BuildEndpoint();

        await ws.ConnectAsync(endpoint, token);
        ConnectionChanged?.Invoke(true);

        var buffer = new byte[256 * 1024];

        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, token);

                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(message.ToArray());
            await HandleMessageAsync(json, token);
        }
    }

    private Uri BuildEndpoint()
    {
        var baseUri = new Uri(baseUrl + "/");

        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws",
            Port = baseUri.IsDefaultPort ? -1 : baseUri.Port
        };

        var path = builder.Path;
        if (!path.EndsWith("/")) path += "/";
        builder.Path = path + "server";

        var query = new List<string>
        {
            "webrtc_supported=false"
        };

        if (!string.IsNullOrWhiteSpace(peerId)
            && !string.IsNullOrWhiteSpace(peerIdHash))
        {
            query.Add("peer_id=" + Uri.EscapeDataString(peerId));
            query.Add("peer_id_hash=" + Uri.EscapeDataString(peerIdHash));
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private async Task HandleMessageAsync(string json, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            return;

        var type = typeEl.GetString() ?? "";

        switch (type)
        {
            case "ping":
                await SendAsync(new { type = "pong" }, token);
                break;

            case "ws-config":
                if (root.TryGetProperty("wsConfig", out var config)
                    && config.TryGetProperty("wsFallback", out var fallback))
                {
                    wsFallbackEnabled = fallback.GetBoolean();
                    if (!wsFallbackEnabled)
                        Error?.Invoke("PairDrop WS_FALLBACK is disabled on the server.");
                }
                break;

            case "display-name":
                await HandleIdentityAsync(root, token);
                break;

            case "peers":
                await HandlePeersAsync(root, token);
                break;

            case "peer-joined":
                HandlePeerJoined(root);
                break;

            case "peer-left":
                HandlePeerLeft(root);
                break;

            case "signal":
                await HandleSignalAsync(root, token);
                break;

            case "request":
                await HandleIncomingRequestAsync(root, token);
                break;

            case "header":
                await HandleIncomingHeaderAsync(root, token);
                break;

            case "ws-chunk":
                await HandleIncomingChunkAsync(root, token);
                break;

            case "partition":
                await HandleIncomingPartitionAsync(root, token);
                break;

            case "text":
                await HandleIncomingTextAsync(root, token);
                break;

            case "display-name-changed":
                HandleDisplayNameChanged(root);
                break;

            case "files-transfer-response":
                HandleOutgoingTransferResponse(root);
                break;

            case "partition-received":
                HandleOutgoingPartitionReceived(root);
                break;

            case "file-transfer-complete":
                HandleOutgoingFileComplete(root);
                break;

            case "message-transfer-complete":
                HandleOutgoingMessageComplete(root);
                break;

            case "progress":
                break;
        }
    }

    private async Task HandleIdentityAsync(JsonElement root, CancellationToken token)
    {
        peerId = GetString(root, "peerId");
        peerIdHash = GetString(root, "peerIdHash");

        var displayName = GetString(root, "displayName");
        var deviceName = GetString(root, "deviceName");

        IdentityChanged?.Invoke(peerId, peerIdHash, displayName, deviceName);

        await SendAsync(new { type = "join-ip-room" }, token);
    }

    private async Task HandlePeersAsync(JsonElement root, CancellationToken token)
    {
        var roomType = GetString(root, "roomType");
        var roomId = GetString(root, "roomId");

        if (!root.TryGetProperty("peers", out var peerArray)
            || peerArray.ValueKind != JsonValueKind.Array)
            return;

        foreach (var peerEl in peerArray.EnumerateArray())
        {
            var peer = ParsePeer(peerEl, roomType, roomId);
            UpsertPeer(peer);

            // Existing peers make us the caller for WSPeer fallback.
            await SendPeerAsync(
                peer,
                new { type = "signal", connected = false },
                token);
        }

        RaisePeersChanged();
    }

    private void HandlePeerJoined(JsonElement root)
    {
        if (!root.TryGetProperty("peer", out var peerEl))
            return;

        var roomType = GetString(root, "roomType");
        var roomId = GetString(root, "roomId");

        UpsertPeer(ParsePeer(peerEl, roomType, roomId));
        RaisePeersChanged();
    }

    private void HandlePeerLeft(JsonElement root)
    {
        var id = GetString(root, "peerId");
        if (string.IsNullOrWhiteSpace(id)) return;

        lock (peerLock)
        {
            peers.Remove(id);
        }

        incoming.TryRemove(id, out _);
        outgoing.TryRemove(id, out _);
        if (outgoingText.TryRemove(id, out var textCompletion))
            textCompletion.TrySetCanceled();
        RaisePeersChanged();
    }

    private async Task HandleSignalAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        if (string.IsNullOrWhiteSpace(senderId)) return;

        var peer = GetPeer(senderId);
        if (peer is null) return;

        var connected = root.TryGetProperty("connected", out var c)
                        && c.ValueKind == JsonValueKind.True;

        if (!connected)
        {
            await SendPeerAsync(
                peer,
                new { type = "signal", connected = true },
                token);
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        var peer = GetPeer(senderId);
        if (peer is null) return;

        var totalSize = root.TryGetProperty("totalSize", out var ts)
            ? ts.GetInt64()
            : 0L;

        var headers = new List<FileHeader>();

        if (root.TryGetProperty("header", out var headerArray)
            && headerArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in headerArray.EnumerateArray())
            {
                headers.Add(new FileHeader(
                    GetString(h, "name"),
                    GetString(h, "mime"),
                    h.TryGetProperty("size", out var sizeEl)
                        ? sizeEl.GetInt64()
                        : 0L));
            }
        }

        if (!autoAccept())
        {
            await SendPeerAsync(
                peer,
                new { type = "files-transfer-response", accepted = false },
                token);
            return;
        }

        Directory.CreateDirectory(downloadFolder());

        var transfer = new IncomingTransfer(peer, headers, totalSize);
        incoming[senderId] = transfer;

        await SendPeerAsync(
            peer,
            new { type = "files-transfer-response", accepted = true },
            token);
    }

    private async Task HandleIncomingHeaderAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        if (!incoming.TryGetValue(senderId, out var transfer))
            return;

        var header = new FileHeader(
            GetString(root, "name"),
            GetString(root, "mime"),
            root.TryGetProperty("size", out var sizeEl)
                ? sizeEl.GetInt64()
                : 0L);

        transfer.BeginFile(header, downloadFolder());

        // A zero-byte file produces no ws-chunk messages, so complete it here.
        if (header.Size == 0)
        {
            await transfer.CompleteCurrentFileAsync();

            await SendPeerAsync(
                transfer.Peer,
                new { type = "progress", progress = transfer.Progress },
                token);

            await SendPeerAsync(
                transfer.Peer,
                new { type = "file-transfer-complete" },
                token);

            if (transfer.TransferComplete)
            {
                incoming.TryRemove(senderId, out _);
                FilesReceived?.Invoke(transfer.Peer, transfer.SavedPaths.ToArray());
            }
        }
    }

    private async Task HandleIncomingChunkAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        if (!incoming.TryGetValue(senderId, out var transfer))
            return;

        var chunk = GetString(root, "chunk");
        if (string.IsNullOrEmpty(chunk))
            return;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(chunk);
        }
        catch
        {
            return;
        }

        await transfer.WriteAsync(bytes, token);

        // PairDrop's browser receiver periodically reports download progress
        // back to the sender. Without these messages, the sender can remain
        // visually stuck in its transfer state even though the bytes arrived.
        if (transfer.ShouldReportProgress)
        {
            await SendPeerAsync(
                transfer.Peer,
                new { type = "progress", progress = transfer.Progress },
                token);

            transfer.MarkProgressReported();
        }

        if (transfer.CurrentFileComplete)
        {
            await transfer.CompleteCurrentFileAsync();

            // Guarantee a final 100% progress update for the last file.
            if (transfer.TransferComplete && transfer.Progress < 1.0)
                transfer.ForceCompleteProgress();

            if (transfer.TransferComplete || transfer.ShouldReportProgress)
            {
                await SendPeerAsync(
                    transfer.Peer,
                    new { type = "progress", progress = transfer.Progress },
                    token);

                transfer.MarkProgressReported();
            }

            await SendPeerAsync(
                transfer.Peer,
                new { type = "file-transfer-complete" },
                token);

            if (transfer.TransferComplete)
            {
                incoming.TryRemove(senderId, out _);
                FilesReceived?.Invoke(transfer.Peer, transfer.SavedPaths.ToArray());
            }
        }
    }

    private async Task HandleIncomingPartitionAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        var peer = GetPeer(senderId);
        if (peer is null) return;

        // Mirror PairDrop's partition acknowledgement, including the offset.
        // Older PairDrop clients ignore the value, newer/alternate clients may
        // use it for transfer bookkeeping.
        object? offset = null;
        if (root.TryGetProperty("offset", out var offsetEl))
            offset = JsonElementToObject(offsetEl);

        await SendPeerAsync(
            peer,
            new { type = "partition-received", offset },
            token);
    }

    private async Task HandleIncomingTextAsync(JsonElement root, CancellationToken token)
    {
        var senderId = SenderId(root);
        var peer = GetPeer(senderId);
        if (peer is null) return;

        var encoded = GetString(root, "text");
        string text;

        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return;
        }

        TextReceived?.Invoke(peer, text);

        await SendPeerAsync(
            peer,
            new { type = "message-transfer-complete" },
            token);
    }

    private void HandleDisplayNameChanged(JsonElement root)
    {
        var senderId = SenderId(root);
        var newName = GetString(root, "displayName");
        if (string.IsNullOrWhiteSpace(senderId)
            || string.IsNullOrWhiteSpace(newName))
            return;

        lock (peerLock)
        {
            if (peers.TryGetValue(senderId, out var peer))
                peers[senderId] = peer with { DisplayName = newName };
        }

        RaisePeersChanged();
    }

    private void HandleOutgoingTransferResponse(JsonElement root)
    {
        var senderId = SenderId(root);
        if (!outgoing.TryGetValue(senderId, out var transfer))
            return;

        var accepted = root.TryGetProperty("accepted", out var a)
                       && a.ValueKind == JsonValueKind.True;

        transfer.Acceptance.TrySetResult(accepted);
    }

    private void HandleOutgoingPartitionReceived(JsonElement root)
    {
        var senderId = SenderId(root);
        if (!outgoing.TryGetValue(senderId, out var transfer))
            return;

        transfer.PartitionAck.TrySetResult(true);
    }

    private void HandleOutgoingFileComplete(JsonElement root)
    {
        var senderId = SenderId(root);
        if (!outgoing.TryGetValue(senderId, out var transfer))
            return;

        transfer.FileComplete.TrySetResult(true);
    }

    private void HandleOutgoingMessageComplete(JsonElement root)
    {
        var senderId = SenderId(root);
        if (outgoingText.TryGetValue(senderId, out var completion))
            completion.TrySetResult(true);
    }

    public async Task SendTextAsync(string targetPeerId, string text)
    {
        var peer = GetPeer(targetPeerId)
                   ?? throw new InvalidOperationException("Device is no longer connected.");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!outgoingText.TryAdd(targetPeerId, completion))
            throw new InvalidOperationException("A text transfer to this device is already in progress.");

        try
        {
            await SendPeerAsync(
                peer,
                new { type = "text", text = encoded },
                cts.Token);

            // PairDrop replies with message-transfer-complete. Waiting for it
            // means we only report the send as finished when the other device
            // has actually processed the message.
            await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                cts.Token);

            TransferSent?.Invoke(peer, "Clipboard text");
        }
        finally
        {
            outgoingText.TryRemove(targetPeerId, out _);
        }
    }

    public async Task SendFilesAsync(string targetPeerId, IReadOnlyList<string> paths)
    {
        var peer = GetPeer(targetPeerId)
                   ?? throw new InvalidOperationException("Device is no longer connected.");

        if (paths.Count == 0) return;

        if (outgoing.ContainsKey(targetPeerId))
            throw new InvalidOperationException("A transfer to this device is already in progress.");

        var headers = paths.Select(path =>
        {
            var info = new FileInfo(path);
            return new FileHeader(
                info.Name,
                MimeFromPath(path),
                info.Length);
        }).ToArray();

        var totalSize = headers.Sum(h => h.Size);
        var transfer = new OutgoingTransfer(peer);

        if (!outgoing.TryAdd(targetPeerId, transfer))
            throw new InvalidOperationException("A transfer to this device is already in progress.");

        try
        {
            await SendPeerAsync(
                peer,
                new
                {
                    type = "request",
                    header = headers.Select(h => new
                    {
                        name = h.Name,
                        mime = h.Mime,
                        size = h.Size
                    }).ToArray(),
                    totalSize,
                    imagesOnly = headers.All(h => h.Mime.StartsWith("image/")),
                    thumbnailDataUrl = ""
                },
                cts.Token);

            var accepted = await transfer.Acceptance.Task.WaitAsync(
                TimeSpan.FromSeconds(90),
                cts.Token);

            if (!accepted)
                throw new InvalidOperationException("The receiving device declined the transfer.");

            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                var header = headers[index];

                await SendPeerAsync(
                    peer,
                    new
                    {
                        type = "header",
                        size = header.Size,
                        name = header.Name,
                        mime = header.Mime
                    },
                    cts.Token);

                transfer.ResetFileComplete();
                await SendFileContentsAsync(peer, transfer, path, cts.Token);

                await transfer.FileComplete.Task.WaitAsync(
                    TimeSpan.FromMinutes(10),
                    cts.Token);
            }

            TransferSent?.Invoke(
                peer,
                paths.Count == 1
                    ? Path.GetFileName(paths[0])
                    : $"{paths.Count} files");
        }
        finally
        {
            outgoing.TryRemove(targetPeerId, out _);
        }
    }

    private async Task SendFileContentsAsync(
        PeerInfo peer,
        OutgoingTransfer transfer,
        string path,
        CancellationToken token)
    {
        const int chunkSize = 64000;
        const int maxPartitionSize = 1_000_000;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            chunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[chunkSize];
        long offset = 0;
        var partitionSize = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (read <= 0) break;

            offset += read;
            partitionSize += read;

            var chunk = Convert.ToBase64String(buffer, 0, read);

            await SendPeerAsync(
                peer,
                new { type = "ws-chunk", chunk },
                token);

            var atEnd = offset >= stream.Length;

            if (!atEnd && partitionSize >= maxPartitionSize)
            {
                transfer.ResetPartitionAck();

                await SendPeerAsync(
                    peer,
                    new { type = "partition", offset },
                    token);

                await transfer.PartitionAck.Task.WaitAsync(
                    TimeSpan.FromSeconds(60),
                    token);

                partitionSize = 0;
            }
        }
    }

    private async Task SendPeerAsync(
        PeerInfo peer,
        object payload,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);

        var dict = new Dictionary<string, object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);

        dict["to"] = peer.Id;
        dict["roomType"] = peer.RoomType;
        dict["roomId"] = peer.RoomId;

        await SendAsync(dict, token);
    }

    private async Task SendAsync(object payload, CancellationToken token)
    {
        var socket = ws;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("PairDrop is not connected.");

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken: token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static object? JsonElementToObject(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var l) => l,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => value.EnumerateArray()
                .Select(JsonElementToObject)
                .ToArray(),
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => null
        };

    private PeerInfo ParsePeer(JsonElement peerEl, string roomType, string roomId)
    {
        var id = GetString(peerEl, "id");

        var displayName = "Device";
        var deviceName = "";

        if (peerEl.TryGetProperty("name", out var name))
        {
            displayName = GetString(name, "displayName");
            deviceName = GetString(name, "deviceName");
        }

        return new PeerInfo(
            id,
            string.IsNullOrWhiteSpace(displayName) ? "Device" : displayName,
            deviceName,
            roomType,
            roomId);
    }

    private void UpsertPeer(PeerInfo peer)
    {
        if (string.IsNullOrWhiteSpace(peer.Id)) return;

        lock (peerLock)
        {
            peers[peer.Id] = peer;
        }
    }

    private PeerInfo? GetPeer(string id)
    {
        lock (peerLock)
        {
            return peers.TryGetValue(id, out var peer)
                ? peer
                : null;
        }
    }

    private void RaisePeersChanged()
    {
        PeerInfo[] snapshot;

        lock (peerLock)
        {
            snapshot = peers.Values.ToArray();
        }

        PeersChanged?.Invoke(snapshot);
    }

    private void ClearPeers()
    {
        lock (peerLock)
        {
            peers.Clear();
        }

        incoming.Clear();
        outgoing.Clear();

        foreach (var completion in outgoingText.Values)
            completion.TrySetCanceled();
        outgoingText.Clear();

        RaisePeersChanged();
    }

    private static string SenderId(JsonElement root)
    {
        if (root.TryGetProperty("sender", out var sender))
            return GetString(sender, "id");

        return "";
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop)
               && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";
    }

    private static string MimeFromPath(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".csv" => "text/csv",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream"
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;

        cts.Cancel();

        var socket = ws;

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await SendAsync(new { type = "disconnect" }, CancellationToken.None);

                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        CancellationToken.None);
                }
            }
            catch { }

            socket.Dispose();
        }

        foreach (var transfer in incoming.Values)
            await transfer.DisposeAsync();

        cts.Dispose();
        sendLock.Dispose();
    }
}

internal sealed record FileHeader(string Name, string Mime, long Size);

internal sealed class IncomingTransfer : IAsyncDisposable
{
    private FileStream? currentStream;
    private FileHeader? currentHeader;
    private long currentBytes;
    private long totalBytesReceived;
    private double lastProgressReported;
    private int completedFiles;

    public PeerInfo Peer { get; }
    public IReadOnlyList<FileHeader> Headers { get; }
    public long TotalSize { get; }
    public List<string> SavedPaths { get; } = new();

    public bool CurrentFileComplete =>
        currentHeader is not null
        && currentBytes >= currentHeader.Size;

    public bool TransferComplete =>
        completedFiles >= Headers.Count;

    public double Progress =>
        TotalSize <= 0
            ? (TransferComplete ? 1.0 : 0.0)
            : Math.Clamp((double)totalBytesReceived / TotalSize, 0.0, 1.0);

    public bool ShouldReportProgress =>
        Progress >= 1.0 || Progress - lastProgressReported >= 0.005;

    public void MarkProgressReported() =>
        lastProgressReported = Progress;

    public void ForceCompleteProgress()
    {
        if (TotalSize > 0)
            totalBytesReceived = Math.Max(totalBytesReceived, TotalSize);
    }

    public IncomingTransfer(
        PeerInfo peer,
        IReadOnlyList<FileHeader> headers,
        long totalSize)
    {
        Peer = peer;
        Headers = headers;
        TotalSize = totalSize;
    }

    public void BeginFile(FileHeader header, string folder)
    {
        currentStream?.Dispose();

        Directory.CreateDirectory(folder);

        var safeName = Path.GetFileName(header.Name);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "PairDrop-file";

        var path = UniquePath(folder, safeName);

        currentHeader = header;
        currentBytes = 0;

        currentStream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        SavedPaths.Add(path);
    }

    public async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        if (currentStream is null)
            return;

        await currentStream.WriteAsync(bytes, token);
        currentBytes += bytes.Length;
        totalBytesReceived += bytes.Length;
    }

    public async Task CompleteCurrentFileAsync()
    {
        if (currentStream is not null)
        {
            await currentStream.FlushAsync();
            await currentStream.DisposeAsync();
            currentStream = null;
        }

        completedFiles++;
        currentHeader = null;
        currentBytes = 0;
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        for (var i = 1; i < 10000; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(
            folder,
            $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    public async ValueTask DisposeAsync()
    {
        if (currentStream is not null)
            await currentStream.DisposeAsync();
    }
}

internal sealed class OutgoingTransfer
{
    public PeerInfo Peer { get; }

    public TaskCompletionSource<bool> Acceptance { get; private set; } =
        NewTcs();

    public TaskCompletionSource<bool> PartitionAck { get; private set; } =
        NewTcs();

    public TaskCompletionSource<bool> FileComplete { get; private set; } =
        NewTcs();

    public OutgoingTransfer(PeerInfo peer)
    {
        Peer = peer;
    }

    public void ResetPartitionAck() =>
        PartitionAck = NewTcs();

    public void ResetFileComplete() =>
        FileComplete = NewTcs();

    private static TaskCompletionSource<bool> NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
