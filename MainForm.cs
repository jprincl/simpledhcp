using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace SimpleDhcpServer
{
    public class MainForm : Form
    {
        private readonly DhcpServer _server = new();
        // Fully qualified: ImplicitUsings also imports System.Threading, which has its own Timer
        private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 2000 };

        private readonly ComboBox _cmbInterface = new();
        private readonly Button _btnRefreshInterfaces = new();
        private readonly TextBox _txtServerIp = new();
        private readonly TextBox _txtPoolStart = new();
        private readonly TextBox _txtPoolEnd = new();
        private readonly TextBox _txtMask = new();
        private readonly TextBox _txtGateway = new();
        private readonly TextBox _txtDns = new();
        private readonly NumericUpDown _numLeaseHours = new();
        private readonly Button _btnStartStop = new();
        private readonly DataGridView _grid = new();
        private readonly TextBox _log = new();

        public MainForm()
        {
            Text = "Jednoduchý DHCP server";
            Width = 900;
            Height = 680;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);

            BuildUi();
            _txtDns.Text = "8.8.8.8, 1.1.1.1";
            PopulateInterfaces();

            _server.Log += AppendLog;
            _refreshTimer.Tick += (_, _) => RefreshLeaseGrid();
            _refreshTimer.Start();

            FormClosing += (_, _) => { if (_server.IsRunning) _server.Stop(); };
        }

        /// <summary>Represents one IPv4 address on one local network adapter,
        /// as shown in the interface picker.</summary>
        private sealed class InterfaceItem
        {
            public string AdapterName { get; }
            public IPAddress Address { get; }
            public IPAddress Mask { get; }

            public InterfaceItem(string adapterName, IPAddress address, IPAddress mask)
            {
                AdapterName = adapterName;
                Address = address;
                Mask = mask;
            }

            public override string ToString() => $"{AdapterName} — {Address} / {Mask}";
        }

        private void PopulateInterfaces()
        {
            var previouslySelected = (_cmbInterface.SelectedItem as InterfaceItem)?.Address;
            _cmbInterface.Items.Clear();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    _cmbInterface.Items.Add(new InterfaceItem(nic.Name, addr.Address, addr.IPv4Mask));
                }
            }

            if (_cmbInterface.Items.Count == 0) return;

            int idx = 0;
            if (previouslySelected != null)
            {
                for (int i = 0; i < _cmbInterface.Items.Count; i++)
                {
                    if (((InterfaceItem)_cmbInterface.Items[i]!).Address.Equals(previouslySelected))
                    { idx = i; break; }
                }
            }
            _cmbInterface.SelectedIndex = idx;
        }

        private void CmbInterface_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_cmbInterface.SelectedItem is not InterfaceItem item) return;

            _txtServerIp.Text = item.Address.ToString();
            _txtMask.Text = item.Mask.ToString();
            _txtGateway.Text = item.Address.ToString();

            var parts = item.Address.ToString().Split('.');
            if (parts.Length == 4)
            {
                _txtPoolStart.Text = $"{parts[0]}.{parts[1]}.{parts[2]}.100";
                _txtPoolEnd.Text = $"{parts[0]}.{parts[1]}.{parts[2]}.150";
            }
        }

        private void BuildUi()
        {
            var configGroup = new GroupBox { Text = "Konfigurace", Dock = DockStyle.Top, Height = 225 };
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, Padding = new Padding(8) };
            for (int i = 0; i < 4; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            var ifaceLabel = new Label { Text = "Síťová karta:", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            _cmbInterface.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterface.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _cmbInterface.SelectedIndexChanged += CmbInterface_SelectedIndexChanged;
            _btnRefreshInterfaces.Text = "Obnovit seznam";
            _btnRefreshInterfaces.Anchor = AnchorStyles.Left;
            _btnRefreshInterfaces.AutoSize = true;
            _btnRefreshInterfaces.Click += (_, _) => PopulateInterfaces();
            table.Controls.Add(ifaceLabel, 0, 0);
            table.SetColumnSpan(_cmbInterface, 2);
            table.Controls.Add(_cmbInterface, 1, 0);
            table.Controls.Add(_btnRefreshInterfaces, 3, 0);

            AddField(table, 1, 0, "IP serveru:", _txtServerIp);
            AddField(table, 1, 2, "Maska podsítě:", _txtMask);
            AddField(table, 2, 0, "Rozsah OD:", _txtPoolStart);
            AddField(table, 2, 2, "Rozsah DO:", _txtPoolEnd);
            AddField(table, 3, 0, "Brána (Gateway):", _txtGateway);
            AddField(table, 3, 2, "DNS servery (čárkou):", _txtDns);

            var leaseLabel = new Label { Text = "Doba zápůjčky (hod):", Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            _numLeaseHours.Minimum = 1;
            _numLeaseHours.Maximum = 720;
            _numLeaseHours.Value = 12;
            _numLeaseHours.Anchor = AnchorStyles.Left;
            table.Controls.Add(leaseLabel, 0, 4);
            table.Controls.Add(_numLeaseHours, 1, 4);

            _btnStartStop.Text = "Spustit server";
            _btnStartStop.Width = 150;
            _btnStartStop.Height = 32;
            _btnStartStop.Anchor = AnchorStyles.Right;
            _btnStartStop.Click += BtnStartStop_Click;
            table.Controls.Add(_btnStartStop, 3, 4);

            configGroup.Controls.Add(table);

            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.Columns.Add("Ip", "IP adresa");
            _grid.Columns.Add("Mac", "MAC adresa");
            _grid.Columns.Add("Host", "Hostname");
            _grid.Columns.Add("Expires", "Platnost do");
            var gridGroup = new GroupBox { Text = "Aktivní zápůjčky (leases)", Dock = DockStyle.Fill };
            gridGroup.Controls.Add(_grid);

            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = Color.Black;
            _log.ForeColor = Color.LightGreen;
            _log.Font = new Font("Consolas", 9F);
            var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Bottom, Height = 180 };
            logGroup.Controls.Add(_log);

            // Fill-docked control must be added last so Top/Bottom get their space first
            Controls.Add(configGroup);
            Controls.Add(logGroup);
            Controls.Add(gridGroup);
        }

        private static void AddField(TableLayoutPanel table, int row, int col, string label, TextBox box)
        {
            var lbl = new Label { Text = label, Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            box.Width = 160;
            table.Controls.Add(lbl, col, row);
            table.Controls.Add(box, col + 1, row);
        }

        private void BtnStartStop_Click(object? sender, EventArgs e)
        {
            if (!_server.IsRunning)
            {
                if (!TryBuildConfig(out var cfg, out var error))
                {
                    MessageBox.Show(error, "Chyba konfigurace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    _server.Start(cfg!);
                    _btnStartStop.Text = "Zastavit server";
                    SetConfigEnabled(false);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Nepodařilo se spustit server (zkuste aplikaci spustit jako správce a zkontrolujte, že port UDP/67 není obsazený jiným DHCP serverem):\n{ex.Message}",
                        "Chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                _server.Stop();
                _btnStartStop.Text = "Spustit server";
                SetConfigEnabled(true);
            }
        }

        private void SetConfigEnabled(bool enabled)
        {
            foreach (var box in new[] { _txtServerIp, _txtPoolStart, _txtPoolEnd, _txtMask, _txtGateway, _txtDns })
                box.Enabled = enabled;
            _numLeaseHours.Enabled = enabled;
            _cmbInterface.Enabled = enabled;
            _btnRefreshInterfaces.Enabled = enabled;
        }

        private bool TryBuildConfig(out DhcpServerConfig? cfg, out string error)
        {
            cfg = null;
            if (!IPAddress.TryParse(_txtServerIp.Text.Trim(), out var serverIp))
            { error = "Neplatná IP adresa serveru."; return false; }
            if (!IPAddress.TryParse(_txtPoolStart.Text.Trim(), out var poolStart))
            { error = "Neplatná počáteční IP rozsahu."; return false; }
            if (!IPAddress.TryParse(_txtPoolEnd.Text.Trim(), out var poolEnd))
            { error = "Neplatná koncová IP rozsahu."; return false; }
            if (!IPAddress.TryParse(_txtMask.Text.Trim(), out var mask))
            { error = "Neplatná maska podsítě."; return false; }

            IPAddress gateway = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(_txtGateway.Text) && !IPAddress.TryParse(_txtGateway.Text.Trim(), out gateway))
            { error = "Neplatná adresa brány."; return false; }

            var dnsList = _txtDns.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => IPAddress.TryParse(s, out var ip) ? ip : null)
                .Where(ip => ip != null)
                .Select(ip => ip!)
                .ToArray();

            cfg = new DhcpServerConfig
            {
                ServerIp = serverIp,
                PoolStart = poolStart,
                PoolEnd = poolEnd,
                SubnetMask = mask,
                Gateway = gateway,
                DnsServers = dnsList,
                LeaseTime = TimeSpan.FromHours((double)_numLeaseHours.Value)
            };
            error = "";
            return true;
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), message); return; }
            _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }

        private void RefreshLeaseGrid()
        {
            if (!_server.IsRunning) return;
            _server.Leases.CleanExpired();
            _grid.Rows.Clear();
            foreach (var l in _server.Leases.AllLeases)
                _grid.Rows.Add(l.Ip.ToString(), l.MacString, l.HostName, l.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
        }
    }
}
