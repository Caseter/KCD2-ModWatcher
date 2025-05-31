using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace KCD2_ModWatcher
{
    public partial class MainWindow : Window
    {
        private NotifyIcon trayIcon = null!;
        private string? selectedModFolder = null;
        private string lastHash = "";

        private bool _hasRepackedOnce = false;
        private bool _skipFirstKill = true;

        public MainWindow()
        {
            InitializeComponent();
            LoadConfig();

            // If LoadConfig populated a valid mod folder, compute its initial hash:
            if (!string.IsNullOrWhiteSpace(selectedModFolder) && Directory.Exists(selectedModFolder))
            {
                try
                {
                    lastHash = ComputeFolderHash(selectedModFolder);
                }
                catch
                {
                    // If hashing fails, leave lastHash = "", so the first real repack will see a difference.
                    lastHash = "";
                }
            }

            // Set up the tray icon by extracting the icon embedded into the running EXE:
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            Icon trayIconImage;
            if (!string.IsNullOrEmpty(exePath))
            {
                trayIconImage = System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application;
            }
            else
            {
                trayIconImage = System.Drawing.SystemIcons.Application;
            }

            trayIcon = new NotifyIcon
            {
                Icon = trayIconImage,
                Visible = true,
                Text = "KCD2 Mod Watcher",
                ContextMenuStrip = new ContextMenuStrip()
            };
            trayIcon.ContextMenuStrip.Items.Add("Exit", null, (s, e) => Application.Current.Shutdown());
            trayIcon.MouseClick += TrayIcon_MouseClick;

            StartPollingForKCD2();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                Hide(); // minimize to tray
            }
        }

        private void TrayIcon_MouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        }

        private void LogDebug(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => LogDebug(message));
                return;
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            DebugOutput.Text += $"[{timestamp}] {message}\n";
            DebugOutput.ScrollToEnd();
        }

        private async void StartPollingForKCD2()
        {
            bool gamePreviouslyRunning = false;
            bool warnedKillFailure = false;

            while (true)
            {
                var proc = Process.GetProcessesByName("KingdomCome").FirstOrDefault();

                if (proc != null && !gamePreviouslyRunning)
                {
                    // We have just detected the game “KingdomCome” starting.

                    // 1) If this is the very first detection since watcher launch, skip killing/repacking:
                    if (_skipFirstKill)
                    {
                        _skipFirstKill = false;
                        gamePreviouslyRunning = true;
                        LogDebug("Skipping first repack.");
                    }
                    else
                    {
                        // Now we're in a state where we want to actually kill + repack if needed:
                        gamePreviouslyRunning = true;

                        try
                        {
                            proc.Kill();
                            LogDebug("Killed KCD2 process to allow repack.");
                        }
                        catch (Exception ex)
                        {
                            if (!warnedKillFailure)
                            {
                                LogDebug($"Failed to kill KCD2: {ex.Message}");
                                warnedKillFailure = true;
                                MessageBox.Show(
                                    "KCD2 is running, but the watcher couldn't terminate it automatically.\n\n" +
                                    "You may need to close it manually before mod repack will run.\n\n" +
                                    $"Reason: {ex.Message}",
                                    "KCD2 Watcher Warning",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            }
                            // Skip repack if kill fails:
                            await Task.Delay(5000);
                            continue;
                        }

                        // 2) After killing, check whether to force-repack (first time) or do a hash‐check:
                        if (!_hasRepackedOnce)
                        {
                            // It’s the FIRST repack for this watcher session → always run repack:
                            _hasRepackedOnce = true;
                            LogDebug("Performing first forced repack (no hash check).");
                            await ForceRepackAndLaunch();
                        }
                        else
                        {
                            // Not the first repack → only repack if the folder contents changed:
                            await RunModCheckAndRepack();
                        }
                    }
                }

                if (proc == null)
                {
                    // Game is not running; reset flags so the next launch can trigger again:
                    gamePreviouslyRunning = false;
                    warnedKillFailure = false;
                }

                await Task.Delay(5000); // poll every 5 seconds
            }
        }

        /// <summary>
        /// Always run a repack (regardless of hash), then launch the game.
        /// </summary>
        private async Task ForceRepackAndLaunch()
        {
            if (string.IsNullOrWhiteSpace(selectedModFolder) || !Directory.Exists(selectedModFolder))
            {
                MessageBox.Show("No valid mod folder selected.", "KCD2 PAK Watcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Compute and store the new baseline hash (so the next time, RunModCheckAndRepack can compare correctly):
            try
            {
                lastHash = ComputeFolderHash(selectedModFolder);
            }
            catch (Exception ex)
            {
                LogDebug($"Error computing folder hash before forced repack: {ex.Message}");
                lastHash = "";
            }

            LogDebug("Running forced mod repack...");
            await Task.Run(() => RunExternalRepack(selectedModFolder!));
            LogDebug("Forced repack complete. Launching game.");
            StartGame();
        }

        /// <summary>
        /// Compares current folder hash to lastHash. If different, runs repack and updates lastHash; otherwise does nothing.
        /// </summary>
        private async Task RunModCheckAndRepack()
        {
            if (string.IsNullOrWhiteSpace(selectedModFolder) || !Directory.Exists(selectedModFolder))
            {
                MessageBox.Show("No valid mod folder selected.", "KCD2 PAK Watcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string currentHash;
            try
            {
                currentHash = ComputeFolderHash(selectedModFolder);
            }
            catch (Exception ex)
            {
                LogDebug($"Error computing folder hash: {ex.Message}");
                return;
            }

            if (currentHash != lastHash)
            {
                lastHash = currentHash; // update the baseline
                LogDebug("Detected mod‐folder change → running mod repack...");

                await Task.Run(() => RunExternalRepack(selectedModFolder!));

                LogDebug("Repack complete. Launching game.");
                StartGame();
            }
            else
            {
                LogDebug("No changes detected; skipping repack. Launching game.");
                StartGame();
            }
        }

        private void RunExternalRepack(string folderToPack)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pakToolRoot = Path.Combine(appData, "KCD2-PAK");
            string pakToolPath = Path.Combine(pakToolRoot, "current", "KCD2-PAK.exe");

            // Ensure .nopause is in the root (not /current)
            string noPausePath = Path.Combine(pakToolRoot, ".nopause");
            if (!File.Exists(noPausePath))
            {
                try
                {
                    File.Create(noPausePath).Dispose();
                    Dispatcher.Invoke(() => LogDebug("Created .nopause file."));
                }
                catch (Exception exNoPause)
                {
                    Dispatcher.Invoke(() => LogDebug($".nopause creation failed: {exNoPause.Message}"));
                }
            }

            if (!File.Exists(pakToolPath))
            {
                Dispatcher.Invoke(() =>
                    MessageBox.Show("PAK tool not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                return;
            }

            try
            {
                var proc = new Process();
                proc.StartInfo.FileName = pakToolPath;
                proc.StartInfo.Arguments = $"\"{folderToPack}\"";
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                proc.Start();

                if (!proc.WaitForExit(30000)) // wait max 30 seconds
                {
                    try { proc.Kill(); } catch { }
                    Dispatcher.Invoke(() => LogDebug("Repack process timed out and was killed."));
                }
            }
            catch (Exception exProc)
            {
                Dispatcher.Invoke(() => LogDebug($"Repack failed: {exProc.Message}"));
            }
        }

        private void StartGame()
        {
            LogDebug("Launching KCD2 via Steam...");

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://rungameid/1771300",
                    UseShellExecute = true
                });

                LogDebug("Steam launch triggered.");
            }
            catch (Exception ex)
            {
                LogDebug($"Steam launch failed: {ex.Message}");
                MessageBox.Show("Failed to launch KCD2 via Steam: " + ex.Message, "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ComputeFolderHash(string folder)
        {
            var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
            using var sha = SHA256.Create();
            using var stream = new MemoryStream();

            foreach (var file in files.OrderBy(f => f))
            {
                var data = File.ReadAllBytes(file);
                stream.Write(data, 0, data.Length);
            }

            stream.Position = 0;
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }

        private string ConfigFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KCD2ModWatcher", "config.json");

        private void LoadConfig()
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (config != null && config.TryGetValue("modFolder", out var folder))
                {
                    selectedModFolder = folder;
                    if (ModFolderTextBox != null)
                        ModFolderTextBox.Text = folder;
                }
            }
        }

        private void SaveConfig()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
            var config = new { modFolder = selectedModFolder };
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(config));
        }

        // === UI events ===

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select your KCD2 mod folder (must contain mod.manifest)",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                selectedModFolder = dialog.SelectedPath;
                ModFolderTextBox.Text = selectedModFolder;
                LogDebug($"Mod folder set to: {selectedModFolder}");
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists(ModFolderTextBox.Text))
            {
                selectedModFolder = ModFolderTextBox.Text;
                SaveConfig();
                MessageBox.Show("Mod path saved.", "KCD2 Mod Watcher", MessageBoxButton.OK, MessageBoxImage.Information);

                // Recompute lastHash immediately upon saving a new folder,
                // so we don’t trigger an unnecessary repack on next check if nothing changed.
                try
                {
                    lastHash = ComputeFolderHash(selectedModFolder);
                    LogDebug("Initial folder hash stored after saving config.");
                }
                catch (Exception ex)
                {
                    LogDebug($"Error computing initial hash: {ex.Message}");
                    lastHash = "";
                }

                // Reset flags so that on the next game launch we do a forced repack.
                _hasRepackedOnce = false;
                _skipFirstKill = true;
            }
            else
            {
                MessageBox.Show("That folder doesn't exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}