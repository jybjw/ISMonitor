using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace InternetShutdownMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly NumericUpDown countdownMinutesInput;
        private readonly NumericUpDown checkIntervalInput;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Label statusValueLabel;
        private readonly Label countdownValueLabel;
        private readonly TextBox logTextBox;
        private readonly System.Windows.Forms.Timer uiTimer;
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly object checkLock = new object();

        private bool monitoring;
        private bool checking;
        private bool countdownActive;
        private int remainingSeconds;
        private DateTime nextCheckAt;

        public MainForm()
        {
            Text = "Internet 断网关机监控";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 430);
            MinimumSize = new Size(560, 430);
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = LoadAppIcon();

            countdownMinutesInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 10,
                Width = 120,
                Location = new Point(160, 24)
            };

            checkIntervalInput = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 300,
                Value = 10,
                Width = 120,
                Location = new Point(160, 64)
            };

            startButton = new Button
            {
                Text = "开始检测",
                Location = new Point(320, 22),
                Size = new Size(100, 32)
            };
            startButton.Click += StartButton_Click;

            stopButton = new Button
            {
                Text = "停止检测",
                Location = new Point(430, 22),
                Size = new Size(100, 32),
                Enabled = false
            };
            stopButton.Click += StopButton_Click;

            statusValueLabel = new Label
            {
                Text = "未开始",
                AutoSize = true,
                Location = new Point(160, 115),
                ForeColor = Color.DimGray
            };

            countdownValueLabel = new Label
            {
                Text = "--:--",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
                Location = new Point(155, 142),
                ForeColor = Color.FromArgb(25, 94, 160)
            };

            logTextBox = new TextBox
            {
                Location = new Point(24, 210),
                Size = new Size(506, 170),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical
            };

            Controls.Add(CreateLabel("关机倒计时（分钟）", 24, 27));
            Controls.Add(countdownMinutesInput);
            Controls.Add(CreateLabel("检测间隔（秒）", 24, 67));
            Controls.Add(checkIntervalInput);
            Controls.Add(startButton);
            Controls.Add(stopButton);
            Controls.Add(CreateLabel("当前状态", 24, 115));
            Controls.Add(statusValueLabel);
            Controls.Add(CreateLabel("剩余倒计时", 24, 156));
            Controls.Add(countdownValueLabel);
            Controls.Add(CreateLabel("运行日志", 24, 188));
            Controls.Add(logTextBox);

            uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            uiTimer.Tick += UiTimer_Tick;

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示窗口", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("开始检测", null, delegate { StartMonitoring(); });
            trayMenu.Items.Add("停止检测", null, delegate { StopMonitoring("已从托盘停止检测。"); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { ExitApplication(); });

            trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Text = "Internet 断网关机监控",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowFromTray(); };

            Resize += MainForm_Resize;
            FormClosing += MainForm_FormClosing;
            UpdateCountdownLabel();
        }

        private static Label CreateLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Location = new Point(x, y)
            };
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                return icon ?? SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void StartButton_Click(object sender, EventArgs e)
        {
            StartMonitoring();
        }

        private void StopButton_Click(object sender, EventArgs e)
        {
            StopMonitoring("已停止检测。");
        }

        private void StartMonitoring()
        {
            if (monitoring)
            {
                return;
            }

            monitoring = true;
            countdownActive = false;
            remainingSeconds = 0;
            nextCheckAt = DateTime.MinValue;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            countdownMinutesInput.Enabled = false;
            checkIntervalInput.Enabled = false;
            SetStatus("检测中", Color.FromArgb(25, 94, 160));
            AddLog("开始检测 Internet 连接。");
            uiTimer.Start();
            BeginNetworkCheck();
        }

        private void StopMonitoring(string reason)
        {
            if (!monitoring && !countdownActive)
            {
                return;
            }

            monitoring = false;
            countdownActive = false;
            remainingSeconds = 0;
            uiTimer.Stop();
            startButton.Enabled = true;
            stopButton.Enabled = false;
            countdownMinutesInput.Enabled = true;
            checkIntervalInput.Enabled = true;
            SetStatus("已停止", Color.DimGray);
            UpdateCountdownLabel();
            AddLog(reason);
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (!monitoring)
            {
                return;
            }

            if (countdownActive)
            {
                remainingSeconds--;
                if (remainingSeconds <= 0)
                {
                    remainingSeconds = 0;
                    UpdateCountdownLabel();
                    AddLog("倒计时结束，正在执行关机。");
                    ExecuteShutdown();
                    StopMonitoring("关机命令已发送。");
                    return;
                }
            }

            UpdateCountdownLabel();

            if (DateTime.Now >= nextCheckAt)
            {
                BeginNetworkCheck();
            }
        }

        private void BeginNetworkCheck()
        {
            lock (checkLock)
            {
                if (checking)
                {
                    return;
                }

                checking = true;
            }

            nextCheckAt = DateTime.Now.AddSeconds((int)checkIntervalInput.Value);

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool connected = InternetProbe.IsInternetAvailable();
                BeginInvoke(new Action(delegate
                {
                    checking = false;
                    HandleNetworkResult(connected);
                }));
            });
        }

        private void HandleNetworkResult(bool connected)
        {
            if (!monitoring)
            {
                return;
            }

            if (connected)
            {
                if (countdownActive)
                {
                    AddLog("Internet 已恢复，取消关机倒计时。");
                }

                countdownActive = false;
                remainingSeconds = 0;
                SetStatus("Internet 正常", Color.ForestGreen);
                UpdateCountdownLabel();
                return;
            }

            if (!countdownActive)
            {
                countdownActive = true;
                remainingSeconds = (int)countdownMinutesInput.Value * 60;
                AddLog("无法连接 Internet，开始关机倒计时。");
            }

            SetStatus("断网倒计时中", Color.Firebrick);
            UpdateCountdownLabel();
        }

        private void ExecuteShutdown()
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/s /t 0",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                MessageBox.Show("执行关机失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AddLog("执行关机失败：" + ex.Message);
            }
        }

        private void UpdateCountdownLabel()
        {
            if (!countdownActive)
            {
                countdownValueLabel.Text = "--:--";
                trayIcon.Text = "Internet 断网关机监控";
                return;
            }

            TimeSpan time = TimeSpan.FromSeconds(remainingSeconds);
            string text = ((int)time.TotalMinutes).ToString("00") + ":" + time.Seconds.ToString("00");
            countdownValueLabel.Text = text;
            trayIcon.Text = "断网关机倒计时 " + text;
        }

        private void SetStatus(string text, Color color)
        {
            statusValueLabel.Text = text;
            statusValueLabel.ForeColor = color;
        }

        private void AddLog(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            logTextBox.AppendText(line + Environment.NewLine);
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                trayIcon.ShowBalloonTip(1500, "仍在运行", "程序已最小化到托盘。", ToolTipIcon.Info);
            }
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult result = MessageBox.Show(
                    "关闭窗口会退出检测程序，是否确定退出？",
                    "确认退出",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                }
            }
        }

        private void ExitApplication()
        {
            trayIcon.Visible = false;
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                uiTimer.Dispose();
                trayIcon.Dispose();
                trayMenu.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal static class InternetProbe
    {
        private static readonly string[] ProbeUrls =
        {
            "http://www.msftconnecttest.com/connecttest.txt",
            "http://clients3.google.com/generate_204",
            "https://www.cloudflare.com/cdn-cgi/trace"
        };

        public static bool IsInternetAvailable()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            foreach (string url in ProbeUrls)
            {
                if (CanReach(url))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanReach(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
                request.UserAgent = "InternetShutdownMonitor/1.0";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    return statusCode >= 200 && statusCode < 400;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
