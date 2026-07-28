using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace PermanentHotspotApp
{
    internal class Program
    {
        private static NetworkOperatorTetheringManager? _tetheringManager;
        private static bool _userRequestedStop = false;

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Windows 11 Permanent Hotspot Service ===");

            // 1. Apply Registry Fixes to disable Windows auto-shutoff timeouts
            DisableWindowsHotspotTimeouts();

            // 2. Initialize and start Hotspot
            bool started = await StartHotspotAsync("Persistent_Win11_Hotspot", "SecurePass123!");

            if (!started)
            {
                Console.WriteLine("[-] Failed to start hotspot. Check network adapter capabilities.");
                return;
            }

            Console.WriteLine("[+] Hotspot running successfully!");
            Console.WriteLine("[!] Press CTRL+C or 'Q' to manually terminate.");

            // 3. Start Watchdog Loop (Ensures it stays on continuously)
            var cts = new CancellationTokenSource();
            Task watchdogTask = RunWatchdogAsync("Persistent_Win11_Hotspot", "SecurePass123!", cts.Token);

            // Wait for user input to stop manually
            while (!_userRequestedStop)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
                {
                    _userRequestedStop = true;
                }
                await Task.Delay(500);
            }

            // Clean shutdown when requested by user
            cts.Cancel();
            await StopHotspotAsync();
            Console.WriteLine("[+] Hotspot manually stopped.");
        }

        /// <summary>
        /// Disables Windows Peerless Timeout by altering icssvc settings in HKLM Registry.
        /// </summary>
        private static void DisableWindowsHotspotTimeouts()
        {
            try
            {
                const string keyPath = @"SYSTEM\CurrentControlSet\Services\icssvc\Settings";
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath, true);

                if (key != null)
                {
                    // Setting PeerListTimeout / PeerlessTimeout to 0 or 0xFFFFFFFF prevents idle disconnects
                    key.SetValue("PeerListTimeout", 0, RegistryValueKind.DWord);
                    key.SetValue("PeerlessTimeout", 0, RegistryValueKind.DWord);
                    Console.WriteLine("[+] Registry overrides applied: Peerless timeouts disabled.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Warning: Could not write registry keys (Run as Administrator). Details: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes the WinRT Tethering Manager and configures SSID/Password.
        /// </summary>
       private static async Task<bool> StartHotspotAsync(string ssid, string password)
{
    try
    {
        ConnectionProfile connectionProfile = NetworkInformation.GetInternetConnectionProfile();
        if (connectionProfile == null)
        {
            Console.WriteLine("[-] No active internet connection found to share.");
            return false;
        }

        _tetheringManager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(connectionProfile);

        // Check capability
        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(connectionProfile);
        if (capability != TetheringCapability.Enabled)
        {
            Console.WriteLine($"[-] Tethering not allowed. Reason: {capability}");
            return false;
        }

        // Apply SSID & Password
        var config = new NetworkOperatorTetheringAccessPointConfiguration
        {
            Ssid = ssid,
            Passphrase = password
        };

        await _tetheringManager.ConfigureAccessPointAsync(config);

        // Start Hotspot
        NetworkOperatorTetheringOperationResult result = await _tetheringManager.StartTetheringAsync();
        return result.Status == TetheringOperationStatus.Success;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[-] Error starting hotspot: {ex.Message}");
        return false;
    }
}
        /// <summary>
        /// Continuous background task checking hotspot health every 10 seconds.
        /// Re-enables the hotspot immediately if Windows turns it off.
        /// </summary>
        private static async Task RunWatchdogAsync(string ssid, string password, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);

                if (_userRequestedStop) break;

                if (_tetheringManager != null)
                {
                    var currentState = _tetheringManager.TetheringOperationalState;

                    if (currentState != TetheringOperationalState.On)
                    {
                        Console.WriteLine($"[!] Watchdog detected hotspot dropped state ({currentState}). Restarting...");
                        await StartHotspotAsync(ssid, password);
                    }
                }
            }
        }

        /// <summary>
        /// Manually terminates the hotspot connection.
        /// </summary>
        private static async Task StopHotspotAsync()
        {
            if (_tetheringManager != null)
            {
                await _tetheringManager.StopTetheringAsync();
            }
        }
    }
}
