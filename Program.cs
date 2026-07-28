using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace PermanentHotspotApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Log uncaught crashes directly to a file instead of closing silently
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                string crashMsg = $"[{DateTime.Now}] CRASH: {e.ExceptionObject}";
                File.AppendAllText("hotspot_crash.log", crashMsg + "\n");
                MessageBox.Show($"An error occurred:\n{e.ExceptionObject}", "Hotspot Crash Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
        }
    }

    public class MainForm : Form
    {
        private TextBox txtSsid = new();
        private TextBox txtPass = new();
        private ComboBox cmbBand = new();
        private Button btnToggle = new();
        private RichTextBox txtLog = new();
        
        private NetworkOperatorTetheringManager? _tetheringManager;
        private CancellationTokenSource? _watchdogCts;
        private bool _isRunning = false;

        public MainForm()
        {
            InitUI();
            DisableWindowsHotspotTimeouts();
        }

        private void InitUI()
        {
            this.Text = "Windows 11 Permanent Hotspot";
            this.Size = new System.Drawing.Size(540, 470);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            // SSID Input
            Label lblSsid = new() { Text = "Hotspot SSID:", Location = new System.Drawing.Point(20, 20), AutoSize = true };
            txtSsid.Location = new System.Drawing.Point(130, 18);
            txtSsid.Size = new System.Drawing.Size(190, 25);
            txtSsid.Text = "Persistent_Win11_Hotspot";

            // Password Input
            Label lblPass = new() { Text = "Password:", Location = new System.Drawing.Point(20, 55), AutoSize = true };
            txtPass.Location = new System.Drawing.Point(130, 53);
            txtPass.Size = new System.Drawing.Size(190, 25);
            txtPass.Text = "SecurePass123!";

            // Band Selector (Auto / 2.4 GHz / 5 GHz)
            Label lblBand = new() { Text = "Network Band:", Location = new System.Drawing.Point(20, 90), AutoSize = true };
            cmbBand.Location = new System.Drawing.Point(130, 88);
            cmbBand.Size = new System.Drawing.Size(190, 25);
            cmbBand.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbBand.Items.AddRange(new object[] { "Auto (Recommended)", "2.4 GHz (Best Range)", "5 GHz (Best Speed)" });
            cmbBand.SelectedIndex = 1; // Default to 2.4 GHz for max device compatibility

            // Start / Stop Button
            btnToggle.Text = "Start Hotspot";
            btnToggle.Location = new System.Drawing.Point(340, 18);
            btnToggle.Size = new System.Drawing.Size(160, 95);
            btnToggle.Click += async (s, e) => await ToggleHotspotAsync();

            // Log Console Box
            Label lblLog = new() { Text = "Activity & Status Log:", Location = new System.Drawing.Point(20, 130), AutoSize = true };
            txtLog.Location = new System.Drawing.Point(20, 150);
            txtLog.Size = new System.Drawing.Size(480, 260);
            txtLog.ReadOnly = true;
            txtLog.BackColor = System.Drawing.Color.FromArgb(240, 240, 240);

            this.Controls.AddRange(new Control[] { lblSsid, txtSsid, lblPass, txtPass, lblBand, cmbBand, btnToggle, lblLog, txtLog });
            Log("App initialized. Select options and press 'Start Hotspot'.");
        }

        private void Log(string message)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => Log(message)));
                return;
            }

            txtLog.AppendText(entry + "\n");
            txtLog.SelectionStart = txtLog.Text.Length;
            txtLog.ScrollToCaret();

            // Save log entry to a local text file
            try
            {
                File.AppendAllText("hotspot_activity.log", entry + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Overrides Windows Mobile Hotspot Service (icssvc) idle auto-turnoff behavior.
        /// </summary>
        private void DisableWindowsHotspotTimeouts()
        {
            try
            {
                const string keyPath = @"SYSTEM\CurrentControlSet\Services\icssvc\Settings";
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, true);

                if (key != null)
                {
                    // PeerlessTimeoutEnabled = 0 explicitly tells Windows never to turn off the hotspot when no devices are connected
                    key.SetValue("PeerlessTimeoutEnabled", 0, RegistryValueKind.DWord);
                    
                    // Set fallback timeout values to 1440 minutes (24 hours)
                    key.SetValue("PeerlessTimeout", 1440, RegistryValueKind.DWord);
                    key.SetValue("PeerListTimeout", 1440, RegistryValueKind.DWord);
                    
                    Log("[+] Registry: Applied continuous mode (PeerlessTimeoutEnabled = 0).");
                }
                else
                {
                    Log("[!] Registry Warning: Could not open icssvc registry key.");
                }
            }
            catch (Exception ex)
            {
                Log($"[!] Registry Error: Run as Administrator! Details: {ex.Message}");
            }
        }

        private async Task ToggleHotspotAsync()
        {
            if (_isRunning)
            {
                btnToggle.Enabled = false;
                _watchdogCts?.Cancel();

                if (_tetheringManager != null)
                {
                    await _tetheringManager.StopTetheringAsync();
                }

                _isRunning = false;
                btnToggle.Text = "Start Hotspot";
                txtSsid.Enabled = true;
                txtPass.Enabled = true;
                cmbBand.Enabled = true;
                btnToggle.Enabled = true;
                Log("[+] Hotspot manually stopped.");
            }
            else
            {
                btnToggle.Enabled = false;
                txtSsid.Enabled = false;
                txtPass.Enabled = false;
                cmbBand.Enabled = false;

                // Map UI Band choice to WinRT TetheringWiFiBand
                TetheringWiFiBand selectedBand = cmbBand.SelectedIndex switch
                {
                    1 => TetheringWiFiBand.TwoPointFourGigahertz,
                    2 => TetheringWiFiBand.FiveGigahertz,
                    _ => TetheringWiFiBand.Auto
                };

                bool started = await StartHotspotAsync(txtSsid.Text, txtPass.Text, selectedBand);

                if (started)
                {
                    _isRunning = true;
                    btnToggle.Text = "Stop Hotspot";
                    _watchdogCts = new CancellationTokenSource();
                    _ = RunWatchdogAsync(txtSsid.Text, txtPass.Text, selectedBand, _watchdogCts.Token);
                }
                else
                {
                    txtSsid.Enabled = true;
                    txtPass.Enabled = true;
                    cmbBand.Enabled = true;
                }

                btnToggle.Enabled = true;
            }
        }

        private async Task<bool> StartHotspotAsync(string ssid, string password, TetheringWiFiBand band)
        {
            try
            {
                ConnectionProfile connectionProfile = NetworkInformation.GetInternetConnectionProfile();
                if (connectionProfile == null)
                {
                    Log("[-] Error: No active internet connection found to share.");
                    return false;
                }

                _tetheringManager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(connectionProfile);

                var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(connectionProfile);
                if (capability != TetheringCapability.Enabled)
                {
                    Log($"[-] Error: Tethering unavailable. Reason: {capability}");
                    return false;
                }

                // Configure SSID, Password, and Selected Band
                var config = new NetworkOperatorTetheringAccessPointConfiguration
                {
                    Ssid = ssid,
                    Passphrase = password,
                    Band = band
                };

                await _tetheringManager.ConfigureAccessPointAsync(config);

                var result = await _tetheringManager.StartTetheringAsync();
                if (result.Status == TetheringOperationStatus.Success)
                {
                    Log($"[+] Hotspot started successfully! SSID: '{ssid}' | Band: {band}");
                    return true;
                }
                else
                {
                    Log($"[-] Failed to start hotspot. Status: {result.Status}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"[-] Exception starting hotspot: {ex.Message}");
                return false;
            }
        }

        private async Task RunWatchdogAsync(string ssid, string password, TetheringWiFiBand band, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);

                    if (!_isRunning) break;

                    if (_tetheringManager != null)
                    {
                        var currentState = _tetheringManager.TetheringOperationalState;
                        if (currentState != TetheringOperationalState.On)
                        {
                            Log($"[!] Watchdog: Detected state shift to '{currentState}'. Re-enabling...");
                            await StartHotspotAsync(ssid, password, band);
                        }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    Log($"[!] Watchdog exception: {ex.Message}");
                }
            }
        }
    }
}
