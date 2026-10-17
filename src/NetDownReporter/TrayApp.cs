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
    // Sin WiFi el ping falla al instante (no hay ruta): chequear seguido no cuesta y acorta el corte.
    static readonly TimeSpan WifiRecheck = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    // Al arrancar o volver de suspensión el WiFi tarda en conectarse: no es un corte.
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    const string AlarmWav = @"C:\Windows\Media\Alarm01.wav";
    // Distinto de la alarma, y corto: suena una sola vez antes de un corte previsto.
    const string WarningWav = @"C:\Windows\Media\Windows Notify Calendar.wav";
    static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(1);
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "NetDownReporter";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string SettingsKey = @"Software\NetDownReporter";
    // WlanScan tarda hasta 4 s; recién después Windows ve la red para conectarse. Se conecta apenas
    // Windows avisa que terminó el escaneo, y esto es el tope por si el aviso no llega.
    static readonly TimeSpan ScanWait = TimeSpan.FromSeconds(4);

    readonly OutageLog log = new(OutageLog.DefaultDir);
    readonly OutageTracker tracker = new();
    readonly WifiReconnector reconnector = new();
    readonly NotifyIcon tray = new();
    readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem nextCutItem = new() { Enabled = false, Visible = false };
    readonly ToolStripMenuItem silenceItem = new("Silenciar alarma") { Enabled = false };
    readonly ToolStripMenuItem autostartItem = new("Iniciar con Windows") { CheckOnClick = true };
    readonly ToolStripMenuItem reconnectItem = new("Reconectar WiFi automáticamente") { CheckOnClick = true };
    readonly Icon okIcon = Dot(Color.FromArgb(46, 160, 67)), downIcon = Dot(Color.FromArgb(218, 54, 51)), idleIcon = Dot(Color.Gray);
    readonly SoundPlayer? alarm = File.Exists(AlarmWav) ? new SoundPlayer(AlarmWav) : null;
    readonly SoundPlayer? warning = File.Exists(WarningWav) ? new SoundPlayer(WarningWav) : null;
    readonly SynchronizationContext ui;

    DateTime sessionStart, graceUntil, lastHeartbeat, profileCheckedAt;
    // Último perfil WiFi con el que hubo conexión: se usa para reconectar.
    string? wifiProfile;
    bool suspended, checking;
    // Cambia en cada suspensión/reanudación: un chequeo que estaba en vuelo se descarta.
    int epoch;
    // Ciclo de cortes del router, si lo hay (ver CutPredictor), y el corte previsto que ya se avisó.
    CutSchedule? schedule;
    DateTime warnedFor;

    public TrayApp()
    {
        ui = SynchronizationContext.Current!;
        log.RecoverFromCrash();
        StartSession();

        silenceItem.Click += (_, _) => StopAlarm();
        autostartItem.Checked = IsAutostart();
        if (autostartItem.Checked) RefreshAutostart();
        autostartItem.Click += (_, _) => SetAutostart(autostartItem.Checked);
        wifiProfile = GetSetting("WifiProfile") as string;
        reconnectItem.Checked = reconnector.Enabled = GetSetting("AutoReconnect") is not 0;
        reconnectItem.Click += (_, _) => SetSetting("AutoReconnect", (reconnector.Enabled = reconnectItem.Checked) ? 1 : 0);
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            statusItem, nextCutItem, new ToolStripSeparator(), silenceItem,
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
        do
        {
            WarnOfCut(DateTime.Now);
            await CheckAsync();
        }
        while (await timer.WaitForNextTickAsync());
    }

    /// <summary>
    /// Lo llama el timer y también cada cambio de red que avisa Windows (WiFi apagado, IP perdida),
    /// así un corte de WiFi se detecta en ~1 s en vez de esperar al próximo tick.
    /// </summary>
    async Task CheckAsync()
    {
        // Un solo loop a la vez, también mientras espera el rechequeo: si no, cada tick y cada aviso
        // de red durante un corte de WiFi sumarían otro loop de 0,5 s.
        if (checking) return;
        checking = true;
        try
        {
            while (!suspended && DateTime.Now >= graceUntil)
            {
                var now = DateTime.Now;
                if (now - lastHeartbeat >= HeartbeatInterval) SaveState(now);

                var started = epoch;
                var failure = await NetworkProbe.CheckAsync();
                if (started != epoch || suspended) return;
                Handle(now, failure);

                // Un fallo suelto de router/ISP se confirma o descarta enseguida, no en 5 s; sin WiFi se
                // chequea seguido para notar al instante que volvió (y lanzar a tiempo cada reintento).
                if (tracker.Pending) await Task.Delay(PendingRecheck);
                else if (tracker.Current is not null && tracker.LastFailure == Cause.Wifi) await Task.Delay(WifiRecheck);
                else return;
            }
        }
        finally { checking = false; }
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
                // Windows ya registró la desconexión: puede confirmar o corregir el ciclo.
                LoadSchedule();
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
        // Primer intento: la red se cortó hace segundos y sigue en la lista que ya tiene Windows, así que
        // se pide la conexión sin esperar a un escaneo. Los reintentos escanean primero.
        if (reconnector.Attempts == 1 && wifiProfile is not null)
        {
            reconnector.Completed(DateTime.Now, Wlan.Connect(wifiProfile));
            if (reconnector.Note() is { } first) tracker.SetNote(first);
            return;
        }

        var started = epoch;
        var error = await Wlan.ScanAsync(ScanWait);
        // Si se suspendió la PC o el corte ya terminó, el intento queda descartado.
        if (started != epoch || tracker.Current?.Start != outageStart) return;

        // Si el escaneo alcanzó para que Windows reconecte, no hace falta pedir la conexión.
        if (tracker.LastFailure == Cause.Wifi)
            error = wifiProfile is null ? error ?? "sin perfil WiFi conocido" : Wlan.Connect(wifiProfile) ?? error;
        reconnector.Completed(DateTime.Now, error);
        if (reconnector.Note() is { } note) tracker.SetNote(note);
    }

    void LoadSchedule()
    {
        var now = DateTime.Now;
        schedule = CutPredictor.Find(WlanEventLog.Read(CutPredictor.Window).Select(d => d.Time), now);
        UpdateNextCut(now);
    }

    /// <summary>Avisa una sola vez, un minuto antes, del próximo corte del ciclo del router.</summary>
    void WarnOfCut(DateTime now)
    {
        UpdateNextCut(now);
        if (schedule is null || suspended || tracker.Current is not null) return;
        var next = schedule.NextAfter(now);
        if (next - now > WarnBefore || next == warnedFor) return;
        warnedFor = next;
        if (warning is not null) warning.Play();
        else SystemSounds.Exclamation.Play();
        tray.ShowBalloonTip(10_000, "Corte previsto en 1 minuto",
            $"El router corta el WiFi cada {schedule.Period.TotalHours:0.#} h: el próximo sería a las {next:HH:mm:ss}.", ToolTipIcon.Warning);
    }

    void UpdateNextCut(DateTime now)
    {
        nextCutItem.Visible = schedule is not null;
        if (schedule is not null) nextCutItem.Text = $"Próximo corte previsto: {schedule.NextAfter(now):HH:mm}";
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
        LoadSchedule();
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
        try { Shell(Report.Write(log, tracker.Current, sessionStart, DateTime.Now, WlanEventLog.Read())); }
        catch (Exception e) { MessageBox.Show(e.Message, "No se pudo generar el reporte", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void Shell(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>
    /// Está en Run y no fue desactivada desde el Administrador de tareas, que lo marca en StartupApproved
    /// (primer byte impar) sin borrar la entrada de Run.
    /// </summary>
    static bool IsAutostart()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(RunValue) is not string) return false;
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(RunValue) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
    }

    static void SetAutostart(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }
        run.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            approved?.DeleteValue(RunValue, throwOnMissingValue: false);
        Unblock();
    }

    /// <summary>
    /// Si el exe registrado ya no existe (se movió o se bajó otra versión), apunta al actual; si es el actual,
    /// le saca la marca de la web por si se registró con una versión anterior.
    /// </summary>
    static void RefreshAutostart()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(RunValue) is not string value) return;
        var path = value.Trim('"');
        if (!File.Exists(path)) SetAutostart(true);
        else if (string.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) Unblock();
    }

    /// <summary>
    /// El exe bajado de internet trae la marca de la web (Zone.Identifier) aunque en SmartScreen se haya elegido
    /// "Ejecutar de todas formas", y al iniciar sesión Windows no lo arranca. Si la app ya está corriendo, el
    /// usuario confía en el exe.
    /// </summary>
    static void Unblock()
    {
        try { File.Delete(Application.ExecutablePath + ":Zone.Identifier"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
        warning?.Dispose();
        base.ExitThreadCore();
    }
}
