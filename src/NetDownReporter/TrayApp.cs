using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using Microsoft.Win32;

namespace NetDownReporter;

/// <summary>
/// App de bandeja (no servicio de Windows: un servicio corre en la sesión 0 y no puede mostrar
/// notificaciones ni reproducir sonido en el escritorio del usuario). Todo corre en el hilo de UI:
/// el loop de chequeo usa await, así que no hace falta sincronización.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PendingRecheck = TimeSpan.FromSeconds(1);
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    // Al arrancar o volver de suspensión el WiFi tarda en conectarse: no es un corte.
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    const string AlarmWav = @"C:\Windows\Media\Alarm01.wav";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "NetDownReporter";
    const string SettingsKey = @"Software\NetDownReporter";
    // WlanScan tarda hasta 4 s; recién después Windows ve la red para conectarse.
    static readonly TimeSpan ScanWait = TimeSpan.FromSeconds(4);

    readonly OutageLog log = new(OutageLog.DefaultDir);
    readonly OutageTracker tracker = new();
    readonly WifiReconnector reconnector = new();
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem silenceItem = new("Silenciar alarma") { Enabled = false };
    readonly ToolStripMenuItem autostartItem = new("Iniciar con Windows") { CheckOnClick = true };
    readonly ToolStripMenuItem reconnectItem = new("Reconectar WiFi automáticamente") { CheckOnClick = true };
    readonly Icon okIcon = Dot(Color.FromArgb(46, 160, 67)), downIcon = Dot(Color.FromArgb(218, 54, 51)), idleIcon = Dot(Color.Gray);
    readonly SoundPlayer? alarm = File.Exists(AlarmWav) ? new SoundPlayer(AlarmWav) : null;
    readonly SynchronizationContext ui;

    DateTime sessionStart, graceUntil, lastHeartbeat, profileCheckedAt;
    // Último perfil WiFi con el que hubo conexión: se usa para reconectar.
    string? wifiProfile;
    bool suspended, checking;
    // Cambia en cada suspensión/reanudación: un chequeo que estaba en vuelo se descarta.
    int epoch;

    public TrayApp()
    {
        ui = SynchronizationContext.Current!;
        log.RecoverFromCrash();
        StartSession();

        silenceItem.Click += (_, _) => StopAlarm();
        autostartItem.Checked = IsAutostart();
        autostartItem.Click += (_, _) => SetAutostart(autostartItem.Checked);
        wifiProfile = GetSetting("WifiProfile") as string;
        reconnectItem.Checked = reconnector.Enabled = GetSetting("AutoReconnect") is not 0;
        reconnectItem.Click += (_, _) => SetSetting("AutoReconnect", (reconnector.Enabled = reconnectItem.Checked) ? 1 : 0);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, new ToolStripSeparator(), silenceItem,
            new ToolStripMenuItem("Abrir reporte", null, (_, _) => OpenReport()),
            new ToolStripMenuItem("Abrir carpeta de datos", null, (_, _) => Shell(log.Dir)),
            reconnectItem, autostartItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Salir", null, (_, _) => ExitThread()),
        });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => OpenReport();
        tray.BalloonTipClicked += (_, _) => StopAlarm();
        SetStatus(idleIcon, "Iniciando…");
        tray.Visible = true;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnded += OnSessionEnded;
        // Llega en un hilo del pool: OnNetworkChanged lo pasa al hilo de UI.
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _ = RunAsync();
    }

    async Task RunAsync()
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do await CheckAsync();
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>
    /// Lo llama el timer y también cada cambio de red que avisa Windows (WiFi apagado, IP perdida),
    /// así un corte de WiFi se detecta en ~1 s en vez de esperar al próximo tick.
    /// </summary>
    async Task CheckAsync()
    {
        while (!checking && !suspended && DateTime.Now >= graceUntil)
        {
            var now = DateTime.Now;
            if (now - lastHeartbeat >= HeartbeatInterval) SaveState(now);

            var started = epoch;
            checking = true;
            Cause? failure;
            try { failure = await NetworkProbe.CheckAsync(); }
            finally { checking = false; }
            if (started != epoch || suspended) return;
            Handle(now, failure);

            // Un fallo suelto de router/ISP se confirma o descarta enseguida, no en 5 s.
            if (!tracker.Pending) return;
            await Task.Delay(PendingRecheck);
        }
    }

    void OnNetworkChanged(object? sender, EventArgs e) => ui.Post(_ => _ = CheckAsync(), null);

    void Handle(DateTime at, Cause? failure)
    {
        switch (tracker.Update(at, failure))
        {
            case ({ } o, true):
                SaveState(at);
                StartAlarm();
                tray.ShowBalloonTip(10_000, "Se cortó internet",
                    o.Cause == Cause.Wifi && reconnector.Enabled ? Labels.Reconnecting : Labels.Explain(o.Cause), ToolTipIcon.Error);
                _ = AttachWifiDetailAsync();
                break;
            case ({ } o, false):
                if (reconnector.Note(ended: true) is { } note) o = o with { Note = note };
                log.Append(o);
                SaveState(at);
                StopAlarm();
                tray.ShowBalloonTip(5_000, "Volvió internet",
                    $"El corte duró {Labels.Duration(o.Duration)} ({Labels.Short(o.Cause)}).", ToolTipIcon.Info);
                break;
        }

        if (reconnector.Update(at, tracker.Current, tracker.LastFailure)) _ = ReconnectAsync(tracker.Current!.Start);
        if (failure is null && at - profileCheckedAt >= HeartbeatInterval) RememberProfile(at);

        if (tracker.Current is { } open)
            SetStatus(downIcon, $"Sin internet desde las {open.Start:HH:mm} ({Labels.Short(tracker.LastFailure ?? open.Cause)})");
        else
            SetStatus(okIcon, "Conectado");
    }

    async Task AttachWifiDetailAsync()
    {
        var detail = await NetworkProbe.DescribeWifiAsync();
        tracker.SetDetail(detail);
    }

    async Task ReconnectAsync(DateTime outageStart)
    {
        var started = epoch;
        var error = Wlan.Scan();
        await Task.Delay(ScanWait);
        // Si se suspendió la PC o el corte ya terminó, el intento queda descartado.
        if (started != epoch || tracker.Current?.Start != outageStart) return;

        // Si el escaneo alcanzó para que Windows reconecte, no hace falta pedir la conexión.
        if (tracker.LastFailure == Cause.Wifi)
            error = wifiProfile is null ? error ?? "sin perfil WiFi conocido" : Wlan.Connect(wifiProfile) ?? error;
        reconnector.Completed(DateTime.Now, error);
        if (reconnector.Note() is { } note) tracker.SetNote(note);
    }

    void RememberProfile(DateTime at)
    {
        profileCheckedAt = at;
        if (Wlan.CurrentProfile() is not { } profile || profile == wifiProfile) return;
        wifiProfile = profile;
        SetSetting("WifiProfile", profile);
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // SystemEvents dispara en su propio hilo.
        ui.Send(_ =>
        {
            if (e.Mode == PowerModes.Suspend) Pause("PC suspendida");
            else if (e.Mode == PowerModes.Resume && suspended)
            {
                suspended = false;
                epoch++;
                StartSession();
            }
        }, null);
    }

    void OnSessionEnded(object? sender, SessionEndedEventArgs e) => ui.Send(_ => Pause("PC apagada o sesión cerrada"), null);

    void Pause(string note)
    {
        if (suspended) return;
        suspended = true;
        epoch++;
        EndSession(note);
        SetStatus(idleIcon, "Suspendido");
    }

    void StartSession()
    {
        sessionStart = DateTime.Now;
        graceUntil = sessionStart + Grace;
        SaveState(sessionStart);
    }

    void EndSession(string note)
    {
        var now = DateTime.Now;
        if (tracker.Interrupt(now, note) is { } o) log.Append(o);
        reconnector.Reset();
        StopAlarm();
        log.AppendSession(sessionStart, now);
        log.ClearState();
    }

    void SaveState(DateTime now)
    {
        lastHeartbeat = now;
        log.SaveState(new OutageLog.State(sessionStart, now, tracker.Current));
    }

    void StartAlarm()
    {
        alarm?.PlayLooping();
        if (alarm is null) SystemSounds.Hand.Play();
        silenceItem.Enabled = true;
    }

    void StopAlarm()
    {
        alarm?.Stop();
        silenceItem.Enabled = false;
    }

    void SetStatus(Icon icon, string text)
    {
        tray.Icon = icon;
        statusItem.Text = text;
        tray.Text = "NetDown Reporter: " + text;
    }

    void OpenReport()
    {
        try { Shell(Report.Write(log, tracker.Current, sessionStart, DateTime.Now)); }
        catch (Exception e) { MessageBox.Show(e.Message, "No se pudo generar el reporte", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    static bool IsAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
    }

    static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    static object? GetSetting(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name);
    }

    static void SetSetting(string name, object value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value);
    }

    static Icon Dot(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 3, 26, 26);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnded -= OnSessionEnded;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        if (!suspended) EndSession("monitor cerrado");
        tray.Visible = false;
        tray.Dispose();
        alarm?.Dispose();
        base.ExitThreadCore();
    }
}
