using System;
using System.IO;
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
            this.Size = new System.Drawing.Size(540, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;

            Label lblSsid = new() { Text = "Hotspot SSID:", Location = new System.Drawing.Point(20, 20), AutoSize = true };
            txtSsid.Location = new System.Drawing.Point(120, 18);
            txtSsid.Size = new System.Drawing.Size(200, 25);
            txtSsid.Text = "Persistent_Win11_Hotspot";

            Label lblPass = new() { Text = "Password:", Location = new System.Drawing.Point(20, 55), AutoSize = true };
            txtPass.Location = new System.Drawing.Point(120, 53);
            txtPass.Size = new System.Drawing.Size(200, 25);
            txtPass.Text = "SecurePass123!";

            btnToggle.Text = "Start Hotspot";
            btnToggle.Location = new System.Drawing.Point(340, 18);
            btnToggle.Size = new System.Drawing.Size(160, 60);
            btnToggle.Click += async (s, e) => await ToggleHotspotAsync();

            Label lblLog = new() { Text = "Activity & Status Log:", Location = new System.Drawing.Point(20, 95), AutoSize = true };
            txtLog.Location = new System.Drawing.Point(20, 115);
            txtLog.Size = new System.Drawing.Size(480, 240);
            txtLog.ReadOnly = true;
            txtLog.BackColor = System.Drawing.Color.FromArgb(240, 240, 240);

            this.Controls.AddRange(new Control[] { lblSsid, txtSsid, lblPass, txtPass, btnToggle, lblLog, txtLog });
            Log("App initialized. Ready to start hotspot.");
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

            // Save log entry to a text file
            try
            {
                File.AppendAllText("hotspot_activity.log", entry + "\n");
            }
            catch { }
        }

        private void DisableWindowsHotspotTimeouts()
        {
            try
            {
                const string keyPath = @"SYSTEM\CurrentControlSet\Services\icssvc\Settings";
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, true);

                if (key != null)
                {
                    key.SetValue("PeerListTimeout", 0, RegistryValueKind.DWord);
                    key.SetValue("PeerlessTimeout", 0, RegistryValueKind.DWord);
                    Log("[+] Registry: Disabling peerless idle timeouts.");
                }
                else
                {
                    Log("[!] Warning: Could not open icssvc registry key.");
                }
            }
            catch (Exception ex)
            {
                Log($"[!] Registry Warning: Run as Administrator! Details: {ex.Message}");
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
                btnToggle.Enabled = true;
                Log("[+] Hotspot manually stopped.");
            }
            else
            {
                btnToggle.Enabled = false;
                bool started = await StartHotspotAsync(txtSsid.Text, txtPass.Text);

                if (started)
                {
                    _isRunning = true;
                    btnToggle.Text = "Stop Hotspot";
                    _watchdogCts = new CancellationTokenSource();
                    _ = RunWatchdogAsync(txtSsid.Text, txtPass.Text, _watchdogCts.Token);
                }

                btnToggle.Enabled = true;
            }
        }

        private async Task<bool> StartHotspotAsync(string ssid, string password)
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

                var config = new NetworkOperatorTetheringAccessPointConfiguration
                {
                    Ssid = ssid,
                    Passphrase = password
                };

                await _tetheringManager.ConfigureAccessPointAsync(config);

                var result = await _tetheringManager.StartTetheringAsync();
                if (result.Status == TetheringOperationStatus.Success)
                {
                    Log($"[+] Hotspot successfully started! SSID: '{ssid}'");
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
                Log($"[-] Exception while starting hotspot: {ex.Message}");
                return false;
            }
        }

        private async Task RunWatchdogAsync(string ssid, string password, CancellationToken token)
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
                            Log($"[!] Watchdog: Hotspot dropped to state '{currentState}'. Re-enabling...");
                            await StartHotspotAsync(ssid, password);
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
