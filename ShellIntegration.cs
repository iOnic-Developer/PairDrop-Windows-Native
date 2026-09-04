using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PairDropNative;

/// <summary>
/// Windows shell / IPC integration layered on top of the existing PairDrop
/// client. Keeping this in a separate source file means the native protocol
/// client remains unchanged while Explorer, CLI and hotkey features can evolve
/// independently.
/// </summary>
internal static class ShellBootstrap
{
    private const string MutexName = @"Local\PairDropNative.SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var command = ShellCommand.FromArgs(args);

        using var mutex = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            createdNew: out var isPrimary);

        if (!isPrimary)
        {
            if (command is not null)
            {
                try
                {
                    ShellPipe.SendAsync(command, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch
                {
                    // Explorer launches should fail quietly if the primary
                    // instance is shutting down.
                }
            }

            return;
        }

        var context = new TrayAppContext();

        using var integration = new ShellIntegrationHost(
            context,
            command);

        Application.Run(context);
    }
}

internal enum ShellAction
{
    SendFiles,
    SendClipboard
}

internal sealed class ShellCommand
{
    public ShellAction Action { get; set; }
    public string PeerId { get; set; } = "";
    public string[] Paths { get; set; } = Array.Empty<string>();

    public static ShellCommand? FromArgs(string[] args)
    {
        if (args.Length >= 3
            && (args[0].Equals("--send-files", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("--send-file", StringComparison.OrdinalIgnoreCase)))
        {
            return new ShellCommand
            {
                Action = ShellAction.SendFiles,
                PeerId = args[1],
                Paths = args
                    .Skip(2)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToArray()
            };
        }

        if (args.Length >= 2
            && args[0].Equals("--send-clipboard", StringComparison.OrdinalIgnoreCase))
        {
            return new ShellCommand
            {
                Action = ShellAction.SendClipboard,
                PeerId = args[1]
            };
        }

        return null;
    }
}

internal static class ShellPipe
{
    private const string PipeName = "PairDropNative.CommandPipe";

    public static async Task ListenAsync(
        Action<ShellCommand> onCommand,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                var json = await reader.ReadToEndAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var command =
                    JsonSerializer.Deserialize<ShellCommand>(json);

                if (command is not null)
                    onCommand(command);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public static async Task SendAsync(
        ShellCommand command,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(command));

        Exception? lastError = null;

        // Multi-select Explorer verbs can create several helper processes within
        // milliseconds. Retry until the primary tray process has opened its pipe.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);

                await client.ConnectAsync(250, cancellationToken);
                await client.WriteAsync(payload, cancellationToken);
                await client.FlushAsync(cancellationToken);

                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                lastError = ex;
                await Task.Delay(75, cancellationToken);
            }
        }

        if (lastError is not null)
            throw lastError;
    }
}

internal sealed class ShellIntegrationHost : IDisposable
{
    private readonly TrayAppContext context;
    private readonly Control dispatcher;
    private readonly CancellationTokenSource cancellation = new();
    private readonly System.Windows.Forms.Timer clientMonitor;
    private readonly Dictionary<string, HashSet<string>> pendingFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Forms.Timer> batchTimers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sendGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly FieldInfo? clientField;
    private readonly NotifyIcon? trayIcon;
    private PairDropClient? client;
    private IReadOnlyList<PeerInfo> peers = Array.Empty<PeerInfo>();
    private GlobalClipboardHotkey? hotkey;
    private ContextMenuStrip? hotkeyMenu;
    private bool disposed;

    public ShellIntegrationHost(
        TrayAppContext context,
        ShellCommand? initialCommand)
    {
        this.context = context;

        dispatcher = new Control();
        dispatcher.CreateControl();

        clientField = typeof(TrayAppContext).GetField(
            "client",
            BindingFlags.Instance | BindingFlags.NonPublic);

        trayIcon = typeof(TrayAppContext)
            .GetField(
                "tray",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(context) as NotifyIcon;

        AddHotkeyHintToTray();

        hotkey = new GlobalClipboardHotkey(
            () => Ui(ShowClipboardPicker));

        if (!hotkey.Register())
        {
            Notify(
                "PairDrop Native",
                "Ctrl+Shift+P could not be registered because another application is already using it.");
        }

        _ = ShellPipe.ListenAsync(
            command => Ui(() => HandleCommand(command)),
            cancellation.Token);

        // TrayAppContext may replace PairDropClient when the user presses
        // Reconnect or changes settings. Monitor the private field and reattach
        // our peer listener whenever that happens.
        clientMonitor = new System.Windows.Forms.Timer
        {
            Interval = 750
        };

        clientMonitor.Tick += (_, _) => AttachCurrentClient();
        clientMonitor.Start();

        AttachCurrentClient();

        if (initialCommand is not null)
            HandleCommand(initialCommand);
    }

    private void AddHotkeyHintToTray()
    {
        try
        {
            var menu = trayIcon?.ContextMenuStrip;
            if (menu is null)
                return;

            var existing = menu.Items
                .OfType<ToolStripItem>()
                .Any(item => item.Text.Contains(
                    "Ctrl+Shift+P",
                    StringComparison.OrdinalIgnoreCase));

            if (existing)
                return;

            var insertAt = Math.Min(5, menu.Items.Count);

            menu.Items.Insert(
                insertAt,
                new ToolStripMenuItem(
                    "Clipboard hotkey: Ctrl+Shift+P")
                {
                    Enabled = false
                });
        }
        catch
        {
        }
    }

    private void AttachCurrentClient()
    {
        if (disposed || clientField is null)
            return;

        var current =
            clientField.GetValue(context) as PairDropClient;

        if (ReferenceEquals(current, client))
            return;

        if (client is not null)
            client.PeersChanged -= OnPeersChanged;

        client = current;
        peers = Array.Empty<PeerInfo>();
        ExplorerSendMenu.Remove();

        if (client is not null)
            client.PeersChanged += OnPeersChanged;
    }

    private void OnPeersChanged(IReadOnlyList<PeerInfo> updatedPeers)
    {
        Ui(() =>
        {
            peers = updatedPeers.ToArray();

            ExplorerSendMenu.Update(peers);

            foreach (var peer in peers)
                TryFlush(peer.Id);
        });
    }

    private void HandleCommand(ShellCommand command)
    {
        if (disposed)
            return;

        switch (command.Action)
        {
            case ShellAction.SendFiles:
                QueueFiles(command.PeerId, command.Paths);
                break;

            case ShellAction.SendClipboard:
                _ = SendClipboardAsync(command.PeerId);
                break;
        }
    }

    private void QueueFiles(
        string peerId,
        IReadOnlyList<string> paths)
    {
        if (string.IsNullOrWhiteSpace(peerId)
            || paths.Count == 0)
            return;

        var valid = paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (valid.Length == 0)
            return;

        if (!pendingFiles.TryGetValue(peerId, out var batch))
        {
            batch = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            pendingFiles[peerId] = batch;
        }

        foreach (var path in valid)
            batch.Add(path);

        // Explorer's legacy context menu calls the command once for each selected
        // file. A short debounce merges those invocations into one transfer.
        if (!batchTimers.TryGetValue(peerId, out var timer))
        {
            timer = new System.Windows.Forms.Timer
            {
                Interval = 500
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                TryFlush(peerId);
            };

            batchTimers[peerId] = timer;
        }

        timer.Stop();
        timer.Start();
    }

    private void TryFlush(string peerId)
    {
        if (!pendingFiles.TryGetValue(peerId, out var batch)
            || batch.Count == 0)
            return;

        var peer = FindPeer(peerId);

        if (peer is null || client is null)
        {
            // Keep the selection queued while PairDrop reconnects.
            return;
        }

        var paths = batch.ToArray();
        pendingFiles.Remove(peerId);

        if (batchTimers.TryGetValue(peerId, out var timer))
            timer.Stop();

        _ = SendFilesSequentiallyAsync(peerId, paths);
    }

    private async Task SendFilesSequentiallyAsync(
        string peerId,
        IReadOnlyList<string> paths)
    {
        var gate = sendGates.GetOrAdd(
            peerId,
            _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync();

        try
        {
            var target = FindPeer(peerId);
            var activeClient = client;

            if (target is null || activeClient is null)
            {
                Ui(() => QueueFiles(peerId, paths));
                return;
            }

            await activeClient.SendFilesAsync(peerId, paths);
        }
        catch (Exception ex)
        {
            Ui(() =>
            {
                var targetStillVisible = FindPeer(peerId) is not null;

                if (!targetStillVisible
                    || ex.Message.Contains(
                        "already in progress",
                        StringComparison.OrdinalIgnoreCase))
                {
                    QueueFiles(peerId, paths);
                }

                Notify("Send failed", ex.Message);
            });
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SendClipboardAsync(string peerId)
    {
        var target = FindPeer(peerId);
        var activeClient = client;

        if (target is null || activeClient is null)
        {
            Notify(
                "PairDrop Native",
                "That device is not currently connected.");

            return;
        }

        var text = ReadClipboardText();

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify(
                "PairDrop Native",
                "Clipboard does not contain text.");

            return;
        }

        try
        {
            await activeClient.SendTextAsync(
                target.Id,
                text);
        }
        catch (Exception ex)
        {
            Notify("Send failed", ex.Message);
        }
    }

    private void ShowClipboardPicker()
    {
        if (peers.Count == 0)
        {
            Notify(
                "PairDrop Native",
                "No PairDrop devices are currently visible.");

            return;
        }

        var text = ReadClipboardText();

        if (string.IsNullOrWhiteSpace(text))
        {
            Notify(
                "PairDrop Native",
                "Copy some text first, then press Ctrl+Shift+P.");

            return;
        }

        hotkeyMenu?.Close();
        hotkeyMenu?.Dispose();

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false
        };

        menu.Items.Add(
            new ToolStripMenuItem("Send clipboard to")
            {
                Enabled = false,
                Font = new Font(
                    SystemFonts.MenuFont,
                    FontStyle.Bold)
            });

        menu.Items.Add(new ToolStripSeparator());

        foreach (var peer in peers.OrderBy(p => p.DisplayName))
        {
            var target = peer;

            menu.Items.Add(
                PeerLabel(peer),
                null,
                async (_, _) =>
                {
                    try
                    {
                        var activeClient = client;

                        if (activeClient is null)
                        {
                            Notify(
                                "PairDrop Native",
                                "PairDrop is not connected.");

                            return;
                        }

                        await activeClient.SendTextAsync(
                            target.Id,
                            text);
                    }
                    catch (Exception ex)
                    {
                        Notify("Send failed", ex.Message);
                    }
                });
        }

        menu.Closed += (_, _) =>
        {
            hotkeyMenu?.Dispose();
            hotkeyMenu = null;
        };

        hotkeyMenu = menu;
        menu.Show(Cursor.Position);
    }

    private PeerInfo? FindPeer(string peerId) =>
        peers.FirstOrDefault(peer =>
            peer.Id.Equals(
                peerId,
                StringComparison.OrdinalIgnoreCase));

    private static string PeerLabel(PeerInfo peer) =>
        string.IsNullOrWhiteSpace(peer.DeviceName)
            ? peer.DisplayName
            : $"{peer.DisplayName} — {peer.DeviceName}";

    private static string ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText()
                ? Clipboard.GetText()
                : "";
        }
        catch
        {
            return "";
        }
    }

    private void Notify(string title, string body)
    {
        if (trayIcon is null)
            return;

        try
        {
            if (body.Length > 240)
                body = body[..240] + "…";

            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = body;
            trayIcon.BalloonTipIcon = ToolTipIcon.Info;
            trayIcon.ShowBalloonTip(7000);
        }
        catch
        {
        }
    }

    private void Ui(Action action)
    {
        if (disposed || dispatcher.IsDisposed)
            return;

        try
        {
            if (dispatcher.InvokeRequired)
                dispatcher.BeginInvoke(action);
            else
                action();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        cancellation.Cancel();
        clientMonitor.Stop();
        clientMonitor.Dispose();

        if (client is not null)
            client.PeersChanged -= OnPeersChanged;

        hotkeyMenu?.Close();
        hotkeyMenu?.Dispose();

        hotkey?.Dispose();
        hotkey = null;

        ExplorerSendMenu.Remove();

        foreach (var timer in batchTimers.Values)
            timer.Dispose();

        batchTimers.Clear();
        pendingFiles.Clear();

        foreach (var gate in sendGates.Values)
            gate.Dispose();

        cancellation.Dispose();
        dispatcher.Dispose();
    }
}

internal sealed class GlobalClipboardHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5044;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkP = 0x50;

    private readonly Action callback;
    private bool registered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);

    public GlobalClipboardHotkey(Action callback)
    {
        this.callback = callback;

        CreateHandle(new CreateParams
        {
            Caption = "PairDrop Native Clipboard Hotkey"
        });
    }

    public bool Register()
    {
        if (registered)
            return true;

        registered = RegisterHotKey(
            Handle,
            HotkeyId,
            ModControl | ModShift | ModNoRepeat,
            VkP);

        return registered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey
            && m.WParam.ToInt32() == HotkeyId)
        {
            callback();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            registered = false;
        }

        DestroyHandle();
    }
}

internal static class ExplorerSendMenu
{
    private const string RootPath =
        @"Software\Classes\*\shell\PairDropNative";

    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);

    public static void Update(IReadOnlyList<PeerInfo> peers)
    {
        try
        {
            if (peers.Count == 0)
            {
                Remove();
                return;
            }

            var exe =
                Environment.ProcessPath
                ?? Application.ExecutablePath;

            using var root =
                Registry.CurrentUser.CreateSubKey(RootPath);

            if (root is null)
                return;

            root.SetValue(
                "MUIVerb",
                "Send with PairDrop Native",
                RegistryValueKind.String);

            root.SetValue(
                "Icon",
                exe,
                RegistryValueKind.String);

            root.SetValue(
                "SubCommands",
                "",
                RegistryValueKind.String);

            root.SetValue(
                "MultiSelectModel",
                "Player",
                RegistryValueKind.String);

            root.DeleteSubKeyTree(
                "shell",
                throwOnMissingSubKey: false);

            using var shell = root.CreateSubKey("shell");

            if (shell is null)
                return;

            var index = 0;

            foreach (var peer in peers.OrderBy(p => p.DisplayName))
            {
                var keyName =
                    $"{index:000}_{peer.Id.Replace("-", "")}";

                using var peerKey =
                    shell.CreateSubKey(keyName);

                if (peerKey is null)
                    continue;

                peerKey.SetValue(
                    "MUIVerb",
                    PeerLabel(peer),
                    RegistryValueKind.String);

                peerKey.SetValue(
                    "Icon",
                    exe,
                    RegistryValueKind.String);

                peerKey.SetValue(
                    "MultiSelectModel",
                    "Player",
                    RegistryValueKind.String);

                using var command =
                    peerKey.CreateSubKey("command");

                // Explorer invokes this once per selected file. The named-pipe
                // batcher in ShellIntegrationHost combines those calls.
                command?.SetValue(
                    "",
                    $"\"{exe}\" --send-files \"{peer.Id}\" \"%1\"",
                    RegistryValueKind.String);

                index++;
            }

            RefreshExplorer();
        }
        catch
        {
            // Registry policies should not prevent the tray app from running.
        }
    }

    public static void Remove()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                RootPath,
                throwOnMissingSubKey: false);

            RefreshExplorer();
        }
        catch
        {
        }
    }

    private static string PeerLabel(PeerInfo peer) =>
        string.IsNullOrWhiteSpace(peer.DeviceName)
            ? peer.DisplayName
            : $"{peer.DisplayName} — {peer.DeviceName}";

    private static void RefreshExplorer()
    {
        try
        {
            SHChangeNotify(
                ShcneAssocChanged,
                ShcnfIdList,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        catch
        {
        }
    }
}
