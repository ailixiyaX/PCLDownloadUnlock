using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using PCLDownloadUnlock.Core;

namespace PCLDownloadUnlock
{
    public partial class MainWindow : Window, ILog
    {
        private Analysis _analysis;
        private bool _busy;
        private bool _initializing = true;
        private string _lastLogSignature;
        private readonly DispatcherTimer _debounce;

        public MainWindow()
        {
            InitializeComponent();

            VersionText.Text = AppInfo.Version;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _debounce.Tick += async (s, e) =>
            {
                _debounce.Stop();
                await RefreshAsync();
            };

            AppSettings.Load();
            LogLine("就绪。选择 PCL 主程序后即可检测补丁状态。");

            var detected = AutoDetect();
            if (detected != null) TargetBox.Text = detected;
            else ShowEmpty();

            Loaded += async (s, e) =>
            {
                await RefreshAsync();
                _initializing = false;
            };
        }

        // ---------------- 交互 ----------------

        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 PCL 主程序",
                Filter = "PCL 主程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            var current = CurrentPath(silent: true);
            if (current != null) dlg.InitialDirectory = Path.GetDirectoryName(current);

            if (dlg.ShowDialog(this) == true)
            {
                TargetBox.Text = dlg.FileName;
            }
        }

        private void OnTargetChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_initializing) return;

            var path = TargetBox.Text?.Trim().Trim('"');
            if (_analysis != null && string.Equals(path, _analysis.Path, StringComparison.OrdinalIgnoreCase)) return;

            _debounce.Stop();
            _debounce.Start();
        }

        private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var path = CurrentPath();
            if (path == null) return;

            var confirm = MessageBox.Show(this,
                "将给以下文件打补丁：\n\n" + path + "\n\n" +
                "· 原文件会自动备份为同目录下的 *.pclfix.bak，可随时还原。\n" +
                "· 只改写两个方法的 IL 代码，不改变文件大小。\n" +
                "· 请先完全关闭 PCL（包括托盘图标）。\n\n是否继续？",
                "确认打补丁", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            SetBusy(true);
            try
            {
                var report = await Task.Run(() => PatchEngine.Apply(path, this));

                if (report.Patched.Count == 0)
                {
                    LogLine("补丁已存在，文件未修改。");
                }
                else
                {
                    LogLine("完成：已解除下载 / 安装限制。");
                }
            }
            catch (PatchException ex)
            {
                LogLine(ex.Message, LogLevel.Error);
                MessageBox.Show(this, ex.Message, "打补丁失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                LogLine("打补丁失败：" + ex.Message, LogLevel.Error);
                MessageBox.Show(this, ex.Message, "打补丁失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }

            await RefreshAsync();

            if (_analysis != null && _analysis.AllPatched)
            {
                MessageBox.Show(this, "补丁已生效。\n\n现在启动 PCL，下载 / 安装功能应该可以正常使用。",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void OnRevert(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var path = CurrentPath();
            if (path == null) return;

            var bak = PatchEngine.BackupPath(path);
            if (MessageBox.Show(this,
                    "将从备份还原原始文件：\n\n" + bak + "\n\n还原后下载限制会恢复。是否继续？",
                    "确认还原", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            SetBusy(true);
            try
            {
                var ok = await Task.Run(() => PatchEngine.Revert(path, this));
                if (ok) LogLine("已还原为原始文件。");
            }
            catch (Exception ex)
            {
                LogLine("还原失败：" + ex.Message, LogLevel.Error);
                MessageBox.Show(this, ex.Message, "还原失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }

            await RefreshAsync();
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            var path = CurrentPath(silent: true);
            if (path == null) return;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LogLine("打开目录失败：" + ex.Message, LogLevel.Warn);
            }
        }

        private void OnClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

        private void OnNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // 忽略
            }
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            var exe = files[0];
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                LogLine("请拖入 .exe 文件。", LogLevel.Warn);
                return;
            }

            TargetBox.Text = exe;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5) _ = RefreshAsync();
        }

        // ---------------- 状态渲染 ----------------

        private async Task RefreshAsync()
        {
            var path = CurrentPath(silent: true);
            if (path == null)
            {
                ShowEmpty();
                return;
            }

            SetBusy(true);
            try
            {
                var analysis = await Task.Run(() => PatchEngine.Analyze(path));
                _analysis = analysis;
                Render(analysis);
                AppSettings.Save(path);
            }
            catch (Exception ex)
            {
                LogLine("检测失败：" + ex.Message, LogLevel.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void Render(Analysis a)
        {
            VersionValue.Text = a.Version;
            SizeValue.Text = FormatSize(a.Size);
            ShaValue.Text = string.IsNullOrEmpty(a.Sha256) ? "—" : a.Sha256;
            BackupValue.Text = a.HasBackup ? "已存在" : "无";

            var signature = a.Path + "|" + a.Status + "|" +
                            string.Join(",", a.Sites.ConvertAll(site => site.PatchApplied ? "1" : "0")) + "|" +
                            a.IsLocked;
            var firstTime = !string.Equals(signature, _lastLogSignature, StringComparison.Ordinal);
            _lastLogSignature = signature;

            if (firstTime)
            {
                if (a.IsBundle) LogLine("单文件包：" + a.BundleInfo);

                foreach (var site in a.Sites)
                {
                    LogLine("补丁点 " + site.Name.PadRight(18) + " RVA=0x" + site.Rva.ToString("X") +
                            (site.PatchApplied ? "  [已打补丁]" : "  [未打补丁]"));
                }

                foreach (var w in a.Warnings) LogLine(w, LogLevel.Warn);
            }

            switch (a.Status)
            {
                case PatchStatus.Patched:
                    SetBadge("已打补丁", a.IsLocked ? "限制已解除 · PCL 正在运行" : "下载 / 安装限制已解除", "#E8F7EF", "#15803D");
                    break;
                case PatchStatus.NotPatched:
                    SetBadge("未打补丁", a.IsLocked ? "PCL 正在运行，请先退出" : "点击「应用补丁」解除限制", "#FFF5E6", "#B45309");
                    break;
                case PatchStatus.Incompatible:
                    SetBadge("不适用", "未找到区域限制逻辑", "#EEF1F6", "#4B5563");
                    break;
                case PatchStatus.NotBundle:
                    SetBadge("无效文件", "不是 .NET 单文件包", "#FDECEC", "#B91C1C");
                    break;
                default:
                    SetBadge("未知", a.Failure ?? "无法读取文件", "#EEF1F6", "#4B5563");
                    break;
            }

            if (!string.IsNullOrEmpty(a.Failure)) LogLine(a.Failure, LogLevel.Warn);

            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var a = _analysis;
            if (a == null)
            {
                ApplyButton.IsEnabled = false;
                RevertButton.IsEnabled = false;
                OpenFolderButton.IsEnabled = false;
                return;
            }

            ApplyButton.IsEnabled = !_busy && a.IsBundle && a.Sites.Count > 0 && !a.AllPatched && !a.IsLocked;
            RevertButton.IsEnabled = !_busy && a.HasBackup && !a.IsLocked;
            OpenFolderButton.IsEnabled = !_busy && File.Exists(a.Path);
        }

        private void ShowEmpty()
        {
            _analysis = null;
            VersionValue.Text = "—";
            SizeValue.Text = "—";
            ShaValue.Text = "—";
            BackupValue.Text = "—";
            SetBadge("未检测", "请选择目标文件", "#F1F3F7", "#6B7280");
            ApplyButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
        }

        private void SetBadge(string text, string sub, string background, string foreground)
        {
            StatusBadgeText.Text = text;
            StatusBadgeSub.Text = sub;
            StatusBadge.Background = (Brush)new BrushConverter().ConvertFromString(background);
            StatusBadgeText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Cursor = busy ? Cursors.Wait : null;

            UpdateButtons();
        }

        // ---------------- 工具 ----------------

        private string CurrentPath(bool silent = false)
        {
            var path = TargetBox.Text?.Trim().Trim('"');
            if (string.IsNullOrEmpty(path))
            {
                if (!silent) LogLine("请先选择 PCL 主程序（PCL2_CE_x64.exe）。", LogLevel.Warn);
                return null;
            }

            if (!File.Exists(path))
            {
                if (!silent) LogLine("文件不存在：" + path, LogLevel.Warn);
                return null;
            }

            return path;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "—";
            var mb = bytes / 1024.0 / 1024.0;
            return mb.ToString("0.0") + " MB";
        }

        private static string AutoDetect()
        {
            if (!string.IsNullOrWhiteSpace(AppSettings.LastPath) && File.Exists(AppSettings.LastPath))
                return AppSettings.LastPath;

            var local = Path.Combine(AppContext.BaseDirectory, "PCL2_CE_x64.exe");
            if (File.Exists(local)) return local;

            var desktops = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };

            foreach (var dir in desktops)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                    {
                        var target = ResolveShortcut(lnk);
                        if (target == null || !File.Exists(target)) continue;
                        if (Path.GetFileName(target).StartsWith("PCL", StringComparison.OrdinalIgnoreCase)) return target;
                    }
                }
                catch
                {
                    // 忽略
                }
            }

            var cwd = Path.Combine(Environment.CurrentDirectory, "PCL2_CE_x64.exe");
            return File.Exists(cwd) ? cwd : null;
        }

        private static string ResolveShortcut(string lnk)
        {
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return null;

                dynamic shell = Activator.CreateInstance(type);
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = shortcut.TargetPath;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch
            {
                return null;
            }
        }

        // ---------------- ILog ----------------

        private enum LogLevel
        {
            Info,
            Warn,
            Error
        }

        private void LogLine(string message, LogLevel level = LogLevel.Info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => LogLine(message, level)));
                return;
            }

            var prefix = level switch
            {
                LogLevel.Warn => "! ",
                LogLevel.Error => "× ",
                _ => "· "
            };

            LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + prefix + message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }

        void ILog.Info(string message) => LogLine(message);
        void ILog.Warn(string message) => LogLine(message, LogLevel.Warn);
        void ILog.Error(string message) => LogLine(message, LogLevel.Error);
    }
}
