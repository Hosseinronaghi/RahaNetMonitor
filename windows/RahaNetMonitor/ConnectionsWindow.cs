using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace RahaNetMonitor;

// Windows IPv4 TCP owner table. No byte accounting and no UDP/IPv6 in this release.
internal static class TcpOwners
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
        int addressFamily, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint Pid;
    }

    internal sealed record Connection(int Pid, string Process, string Local, string Remote, string State);
    private static string Address(uint raw) => new IPAddress(BitConverter.GetBytes(raw)).ToString();
    private static int Port(uint raw) => (int)IPAddress.NetworkToHostOrder((short)(raw & 0xffff)) & 0xffff;
    public static List<Connection> Read()
    {
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_ALL = 5;
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (result != 122 && result != 0) throw new System.ComponentModel.Win32Exception((int)result);
        if (size < sizeof(int) || size > 64 * 1024 * 1024)
            throw new InvalidDataException("Unexpected TCP table allocation size");
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(ptr, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
            var count = Marshal.ReadInt32(ptr);
            const int maxRows = 200000;
            if (count < 0 || count > maxRows || (long)count * Marshal.SizeOf<TcpRow>() + sizeof(int) > size)
                throw new InvalidDataException("Invalid TCP table size");
            var rows = new List<Connection>(count);
            var start = IntPtr.Add(ptr, sizeof(int));
            int rowSize = Marshal.SizeOf<TcpRow>();
            // Avoid re-opening the same process handle for each TCP connection.
            var processNames = new Dictionary<int, string>();
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<TcpRow>(IntPtr.Add(start, i * rowSize));
                int pid = (int)r.Pid;
                if (!processNames.TryGetValue(pid, out var process)) {
                    try { using var proc = Process.GetProcessById(pid); process = proc.ProcessName; }
                    catch { process = "(unknown / protected)"; }
                    processNames[pid] = process;
                }
                rows.Add(new Connection((int)r.Pid, process,
                    $"{Address(r.LocalAddress)}:{Port(r.LocalPort)}",
                    $"{Address(r.RemoteAddress)}:{Port(r.RemotePort)}", StateName(r.State)));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    private static string StateName(uint n) => n switch
    {
        1 => "CLOSED", 2 => "LISTEN", 3 => "SYN_SENT", 4 => "SYN_RECEIVED",
        5 => "ESTABLISHED", 6 => "FIN_WAIT_1", 7 => "FIN_WAIT_2", 8 => "CLOSE_WAIT",
        9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB", _ => $"{n}"
    };
}

internal sealed class ConnectionsWindow : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleRight };
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "جستجوی نام برنامه، PID، آدرس یا وضعیت اتصال" };
    private List<TcpOwners.Connection> _rows = new();
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 10000 };
    private readonly CheckBox _watch = new() { Text = "ثبت خودکار هر ۱۰ ثانیه (فقط هنگام باز بودن این پنجره)", AutoSize = true };
    private readonly ConnectionHistory _connectionHistory = new();
    private HashSet<string> _lastConnectionKeys = new(StringComparer.Ordinal);
    public ConnectionsWindow()
    {
        Text = "اتصال‌های TCP برنامه‌ها — IPv4";
        Size = new Size(950, 610); MinimumSize = new Size(700, 440);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
        _grid.Columns.Add("process", "برنامه"); _grid.Columns.Add("pid", "PID");
        _grid.Columns.Add("local", "مبدأ"); _grid.Columns.Add("remote", "مقصد");
        _grid.Columns.Add("state", "وضعیت");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        var refresh = new Button { Text = "به‌روزرسانی", AutoSize = true };
        var export = new Button { Text = "خروجی CSV", AutoSize = true };
        refresh.Click += (_, _) => RefreshConnections();
        export.Click += (_, _) => Export();
        var historical = new Button { Text = "خروجی تاریخچهٔ اتصال‌ها", AutoSize = true };
        historical.Click += (_, _) => ExportConnectionHistory();
        _watch.CheckedChanged += (_, _) => {
            _watchTimer.Enabled = _watch.Checked;
            if (_watch.Checked) { _lastConnectionKeys.Clear(); RefreshConnections(); }
        };
        _watchTimer.Tick += (_, _) => RefreshConnections();
        FormClosed += (_, _) => _watchTimer.Dispose();
        top.Controls.Add(refresh); top.Controls.Add(export); top.Controls.Add(historical); top.Controls.Add(_watch);
        _filter.TextChanged += (_, _) => Render();
        Controls.Add(_grid); Controls.Add(_status); Controls.Add(_filter); Controls.Add(top);
        Shown += (_, _) => RefreshConnections();
    }
    private void RefreshConnections()
    {
        try {
            _rows = TcpOwners.Read();
            if (_watch.Checked) {
                var nowKeys = _rows.Select(ConnectionKey).ToHashSet(StringComparer.Ordinal);
                // Record new/changed rows only, not every persistent TCP connection every 10s.
                _connectionHistory.Append(_rows.Where(r => !_lastConnectionKeys.Contains(ConnectionKey(r))));
                _lastConnectionKeys = nowKeys;
            }
            Render();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "خطا در خواندن ارتباطات"); }
    }
    private static string ConnectionKey(TcpOwners.Connection r) =>
        $"{r.Pid}|{r.Local}|{r.Remote}|{r.State}";
    private void Render()
    {
        var search = _filter.Text.Trim();
        var filtered = _rows.Where(r => string.IsNullOrEmpty(search) ||
            r.Process.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            r.Pid.ToString().Contains(search) ||
            r.Local.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            r.Remote.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            r.State.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        _grid.Rows.Clear();
        foreach (var r in filtered) _grid.Rows.Add(r.Process, r.Pid, r.Local, r.Remote, r.State);
        _status.Text = $"{filtered.Count} اتصال | فقط TCP/IPv4 | اطلاعات لحظه‌ای؛ حجم هر برنامه ثبت نمی‌شود";
    }
    private void ExportConnectionHistory() {
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "raha-connection-snapshots.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _connectionHistory.Export(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "خطا در صدور تاریخچه"); }
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "raha-tcp-connections.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine("process,pid,local,remote,state");
        static string Csv(string s) {
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.TrimStart().Length > 0 && "=+-@".Contains(s.TrimStart()[0])) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        foreach (DataGridViewRow row in _grid.Rows)
            writer.WriteLine(string.Join(",", row.Cells.Cast<DataGridViewCell>().Select(c => Csv(c.Value?.ToString() ?? ""))));
    }
}

// Connection presence snapshots, NOT byte-counted network flows.
internal sealed class ConnectionHistory
{
    private readonly string _folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RahaNetMonitor", "connections");
    public void Append(IEnumerable<TcpOwners.Connection> connections)
    {
        var changed = connections.ToList();
        Directory.CreateDirectory(_folder);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var file = Path.Combine(_folder, $"connections-{DateTime.UtcNow:yyyy-MM-dd}.csv");
        if (changed.Count > 0) {
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            static string C(string value) {
                value = value.Replace("\r", " ").Replace("\n", " ");
                if (value.TrimStart().Length > 0 && "=+-@".Contains(value.TrimStart()[0])) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            foreach (var c in changed)
                writer.WriteLine($"{now},{c.Pid},{C(c.Process)},{C(c.Local)},{C(c.Remote)},{C(c.State)}");
        }
        foreach (var old in Directory.EnumerateFiles(_folder, "connections-*.csv"))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-7))
                try { File.Delete(old); } catch (IOException) { /* retry on a later capture */ }
    }
    public void Export(string target)
    {
        using var output = new StreamWriter(target, false, new System.Text.UTF8Encoding(true));
        output.WriteLine("utc_timestamp,pid,process,local,remote,state");
        if (!Directory.Exists(_folder)) return;
        foreach (var file in Directory.EnumerateFiles(_folder, "connections-*.csv").OrderBy(f => f))
            foreach (var line in File.ReadLines(file)) output.WriteLine(line);
    }
}
