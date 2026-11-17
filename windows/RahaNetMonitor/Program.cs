using System.Collections.Concurrent;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace RahaNetMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Dashboard());
    }
}

internal sealed record AdapterSample(string Id, string Name, long Rx, long Tx);
internal sealed record AdapterDelta(string Name, long RxBytes, long TxBytes);
internal sealed record Sample(DateTimeOffset At, long RxBytesPerSec, long TxBytesPerSec,
    long TotalRxDelta, long TotalTxDelta, int ActiveAdapters, List<AdapterDelta> AdapterDeltas);

internal sealed class NetCollector
{
    private readonly Dictionary<string, AdapterSample> _previous = new();
    private DateTimeOffset? _last;

    public Sample Collect()
    {
        var now = DateTimeOffset.UtcNow;
        var current = NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up &&
                        a.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        a.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(a => { var s = a.GetIPv4Statistics(); return new AdapterSample(a.Id, a.Name, s.BytesReceived, s.BytesSent); })
            .ToList();
        var duration = _last.HasValue ? (now - _last.Value).TotalSeconds : 0;
        long rx = 0, tx = 0;
        var deltas = new List<AdapterDelta>();
        foreach (var a in current)
        {
            if (_previous.TryGetValue(a.Id, out var prev))
            {
                // Discontinuities caused by driver reset / counter wrap are not treated as traffic.
                long dr = a.Rx >= prev.Rx ? a.Rx - prev.Rx : 0;
                long dt = a.Tx >= prev.Tx ? a.Tx - prev.Tx : 0;
                rx += dr; tx += dt;
                deltas.Add(new AdapterDelta(a.Name, dr, dt));
            }
        }
        _previous.Clear();
        foreach (var a in current) _previous[a.Id] = a;
        _last = now;
        return new Sample(now, duration > 0 ? (long)(rx / duration) : 0,
            duration > 0 ? (long)(tx / duration) : 0, rx, tx, current.Count, deltas);
    }
}

internal sealed class JsonlHistory
{
    public string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RahaNetMonitor");
    public JsonlHistory() => Directory.CreateDirectory(Folder);
    public IEnumerable<Sample> Read(DateTime from, DateTime until)
    {
        for (var day = from.Date; day <= until.Date; day = day.AddDays(1))
        {
            // Legacy files were written using the local date; explicitly scan the
            // local-day files, including UTC rollover samples.
            var filename = Path.Combine(Folder, $"traffic-{day:yyyy-MM-dd}.jsonl");
            if (!File.Exists(filename)) continue;
            foreach (var line in File.ReadLines(filename))
            {
                Sample? sample = null;
                try { sample = JsonSerializer.Deserialize<Sample>(line); }
                catch (JsonException) { /* ignore incomplete trailing lines */ }
                if (sample != null && sample.At.ToLocalTime().DateTime >= from && sample.At.ToLocalTime().DateTime <= until) yield return sample;
            }
        }
    }
    public void Append(Sample sample)
    {
        var filename = Path.Combine(Folder, $"traffic-{sample.At.ToLocalTime():yyyy-MM-dd}.jsonl");
        File.AppendAllText(filename, JsonSerializer.Serialize(sample) + Environment.NewLine, new UTF8Encoding(false));
    }
}

internal sealed class TrafficGraph : Control
{
    private readonly Queue<(double Rx, double Tx)> _history = new();
    public TrafficGraph() { DoubleBuffered = true; BackColor = Color.FromArgb(24, 29, 41); }
    public void Add(long rx, long tx)
    {
        _history.Enqueue((rx, tx));
        while (_history.Count > 90) _history.Dequeue();
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var gridPen = new Pen(Color.FromArgb(48, 57, 73));
        for (int i = 1; i < 5; i++) { float y = Height * i / 5f; g.DrawLine(gridPen, 0, y, Width, y); }
        var a = _history.ToArray(); if (a.Length < 2) return;
        double max = Math.Max(1, a.Max(t => Math.Max(t.Rx, t.Tx)) * 1.15);
        Draw(a.Select(t => t.Rx).ToArray(), Color.FromArgb(65, 200, 158));
        Draw(a.Select(t => t.Tx).ToArray(), Color.FromArgb(94, 166, 241));
        void Draw(double[] v, Color color)
        {
            using var p = new Pen(color, 2.5f);
            var points = v.Select((n, i) => new PointF((float)i / Math.Max(1, v.Length - 1) * (Width - 10) + 5,
                Math.Clamp((float)(Height - 8 - n / max * (Height - 16)), 5, Height - 5))).ToArray();
            if (points.Length >= 2) g.DrawLines(p, points);
        }
    }
}

internal sealed class Dashboard : Form
{
    private readonly NetCollector _collector = new();
    private readonly JsonlHistory _history = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _down = NewLabel("در حال اندازه‌گیری…", 22, Color.FromArgb(65, 200, 158));
    private readonly Label _up = NewLabel("در حال اندازه‌گیری…", 22, Color.FromArgb(94, 166, 241));
    private readonly Label _total = NewLabel("۰ B", 12, Color.WhiteSmoke);
    private readonly Label _status = NewLabel("", 10, Color.Silver);
    private readonly TrafficGraph _graph = new() { Dock = DockStyle.Fill, Margin = new Padding(8) };
    private readonly DataGridView _adapters = new() { Dock = DockStyle.Fill, ReadOnly = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.FromArgb(24, 29, 41),
        ForeColor = Color.White, GridColor = Color.FromArgb(50, 57, 72), BorderStyle = BorderStyle.None,
        AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private long _sessionRx, _sessionTx;
    private DateTimeOffset _lastHistoryWrite = DateTimeOffset.MinValue;
    private long _pendingRx, _pendingTx;
    private DateTime _pendingLocalDay = DateTime.MinValue;
    private readonly DateTimePicker _historyDay = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    private readonly Button _historyButton = new() { Text = "نمایش گزارش روز", AutoSize = true };
    private readonly Button _exportButton = new() { Text = "خروجی CSV", AutoSize = true };
    private readonly Button _connectionsButton = new() { Text = "اتصال‌های TCP برنامه‌ها", AutoSize = true };
    public Dashboard()
    {
        Text = "Raha Net Monitor — نسخهٔ نامزد انتشار ۰.۶.۰";
        MinimumSize = new Size(760, 640); Size = new Size(1010, 780);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(17, 21, 30);
        Font = new Font("Segoe UI", 10); RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 7,
            ColumnCount = 1, BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 125));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        Controls.Add(root);
        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        header.Controls.Add(NewLabel("رها | پایش اینترنت", 23, Color.White));
        root.Controls.Add(header, 0, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 / 3f));
        cards.Controls.Add(Card("سرعت دانلود", _down), 0, 0);
        cards.Controls.Add(Card("سرعت آپلود", _up), 1, 0);
        cards.Controls.Add(Card("مصرف از زمان اجرای برنامه", _total), 2, 0);
        root.Controls.Add(cards, 0, 1);
        root.Controls.Add(NewLabel("نمودار زنده | سبز: دانلود  •  آبی: آپلود", 12, Color.WhiteSmoke), 0, 2);
        root.Controls.Add(_graph, 0, 3);
        root.Controls.Add(NewLabel("کارت‌های شبکهٔ فعال (حجم تجمعی گزارش‌شده توسط سیستم)", 12, Color.WhiteSmoke), 0, 4);
        _adapters.ColumnCount = 3;
        _adapters.Columns[0].Name = "کارت شبکه"; _adapters.Columns[1].Name = "دریافت در بازه"; _adapters.Columns[2].Name = "ارسال در بازه";
        _adapters.EnableHeadersVisualStyles = false;
        _adapters.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 45, 62);
        _adapters.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _adapters.DefaultCellStyle.BackColor = Color.FromArgb(24, 29, 41);
        _adapters.DefaultCellStyle.ForeColor = Color.White;
        _adapters.DefaultCellStyle.SelectionBackColor = Color.FromArgb(45, 69, 91);
        root.Controls.Add(_adapters, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.Add(_historyDay); actions.Controls.Add(_historyButton); actions.Controls.Add(_exportButton); actions.Controls.Add(_connectionsButton);
        _connectionsButton.Click += (_, _) => new ConnectionsWindow().Show(this);
        _historyButton.Click += (_, _) => ShowHistory();
        _exportButton.Click += (_, _) => ExportHistory();
        root.Controls.Add(actions, 0, 6);
        var footer = new StatusStrip { BackColor = Color.FromArgb(24, 29, 41), ForeColor = Color.Silver };
        var message = new ToolStripStatusLabel("حجم مصرف در سطح کارت شبکه است؛ جدول TCP فقط پردازش و مقصد را نشان می‌دهد، نه حجم مصرف هر برنامه.");
        footer.Items.Add(message);
        var open = new ToolStripStatusLabel("بازکردن پوشهٔ تاریخچه") { IsLink = true, LinkColor = Color.LightSkyBlue };
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _history.Folder, UseShellExecute = true });
        footer.Items.Add(open); Controls.Add(footer);
        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) => { Tick(); _timer.Start(); };
        FormClosed += (_, _) => {
            _timer.Stop();
            try { FlushPendingHistory(); }
            catch (IOException) { /* best effort flush */ }
            _timer.Dispose();
        };
    }
    private void FlushPendingHistory()
    {
        if (_pendingRx == 0 && _pendingTx == 0) return;
        var now = DateTimeOffset.UtcNow;
        // At midnight, assign pending bytes to the previous local day.
        if (_pendingLocalDay != DateTime.MinValue && now.ToLocalTime().Date != _pendingLocalDay)
        {
            var endOfDay = _pendingLocalDay.AddDays(1).AddTicks(-1);
            now = new DateTimeOffset(endOfDay, TimeZoneInfo.Local.GetUtcOffset(endOfDay));
        }
        _history.Append(new Sample(now, 0, 0, _pendingRx, _pendingTx, 0, new()));
        _pendingRx = 0;
        _pendingTx = 0;
    }
    private void Tick()
    {
        try
        {
            var s = _collector.Collect(); _sessionRx += s.TotalRxDelta; _sessionTx += s.TotalTxDelta;
            _down.Text = Format(s.RxBytesPerSec) + "/s"; _up.Text = Format(s.TxBytesPerSec) + "/s";
            _total.Text = $"↓ {Format(_sessionRx)}    ↑ {Format(_sessionTx)}";
            _graph.Add(s.RxBytesPerSec, s.TxBytesPerSec);
            _adapters.Rows.Clear();
            foreach (var a in s.AdapterDeltas)
                _adapters.Rows.Add(a.Name, Format(a.RxBytes), Format(a.TxBytes));
            // Batch disk writes to one sample per ~10s while retaining byte totals.
            // Never attribute the previous day's pending traffic to the next day.
            var localDay = s.At.ToLocalTime().Date;
            if (_pendingLocalDay != DateTime.MinValue && localDay != _pendingLocalDay)
                FlushPendingHistory();
            _pendingLocalDay = localDay;
            _pendingRx += s.TotalRxDelta; _pendingTx += s.TotalTxDelta;
            if (s.At - _lastHistoryWrite >= TimeSpan.FromSeconds(10))
            {
                var aggregate = s with { TotalRxDelta = _pendingRx, TotalTxDelta = _pendingTx };
                if (_pendingRx != 0 || _pendingTx != 0) _history.Append(aggregate);
                _pendingRx = 0; _pendingTx = 0; _lastHistoryWrite = s.At;
            }
        }
        catch (Exception ex) { _down.Text = "خطا در خواندن شبکه"; Text = "Raha Net Monitor — " + ex.Message; }
    }
    private List<Sample> SelectedDay() => _history.Read(_historyDay.Value.Date, _historyDay.Value.Date.AddDays(1).AddTicks(-1)).ToList();
    private void ShowHistory()
    {
        try
        {
            var rows = SelectedDay();
            long rx = rows.Sum(x => x.TotalRxDelta), tx = rows.Sum(x => x.TotalTxDelta);
            var peakRx = rows.Count == 0 ? 0 : rows.Max(x => x.RxBytesPerSec);
            var peakTx = rows.Count == 0 ? 0 : rows.Max(x => x.TxBytesPerSec);
            MessageBox.Show(this, $"تعداد نمونه‌ها: {rows.Count}\nدریافت: {Format(rx)}\nارسال: {Format(tx)}\nبیشترین سرعت دریافت: {Format(peakRx)}/s\nبیشترین سرعت ارسال: {Format(peakTx)}/s\n\nاین آمار شامل ترافیک شبکهٔ محلی هم هست.", "گزارش " + _historyDay.Value.ToShortDateString());
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "خطا در خواندن تاریخچه"); }
    }
    private void ExportHistory()
    {
        try
        {
            var rows = SelectedDay();
            using var dialog = new SaveFileDialog { Filter = "CSV files|*.csv", FileName = $"raha-traffic-{_historyDay.Value:yyyy-MM-dd}.csv" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            using var sw = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
            sw.WriteLine("utc_timestamp,download_bytes_per_sec,upload_bytes_per_sec,download_delta_bytes,upload_delta_bytes,active_adapters");
            foreach (var row in rows)
                sw.WriteLine($"{row.At:O},{row.RxBytesPerSec},{row.TxBytesPerSec},{row.TotalRxDelta},{row.TotalTxDelta},{row.ActiveAdapters}");
            MessageBox.Show(this, $"{rows.Count} ردیف صادر شد.", "خروجی CSV");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "خطا در صدور فایل"); }
    }
    private static Panel Card(string title, Control value)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 29, 41), Margin = new Padding(6), Padding = new Padding(14) };
        var caption = NewLabel(title, 11, Color.Silver); caption.Dock = DockStyle.Top; caption.Height = 34;
        value.Dock = DockStyle.Fill; panel.Controls.Add(value); panel.Controls.Add(caption); return panel;
    }
    private static Label NewLabel(string s, float size, Color color) => new() { Text = s, Font = new Font("Segoe UI", size, FontStyle.Regular), ForeColor = color, TextAlign = ContentAlignment.MiddleRight, AutoSize = false, Dock = DockStyle.Fill };
    private static string Format(long b)
    {
        double n = b; string[] units = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
}
