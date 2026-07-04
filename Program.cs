using System;
using System.Diagnostics;
using System.Drawing;
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
        private const int AuthRetryDelaySeconds = 10;
        private const int AuthPageTimeoutSeconds = 25;
        private const int AuthVerifyDelaySeconds = 6;

        private readonly NumericUpDown countdownMinutesInput;
        private readonly NumericUpDown checkIntervalInput;
        private readonly TextBox authUrlInput;
        private readonly TextBox usernameInput;
        private readonly TextBox passwordInput;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Label statusValueLabel;
        private readonly Label countdownValueLabel;
        private readonly TextBox logTextBox;
        private readonly WebBrowser authBrowser;
        private readonly System.Windows.Forms.Timer uiTimer;
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly object checkLock = new object();

        private bool monitoring;
        private bool checking;
        private bool countdownActive;
        private bool authenticationActive;
        private bool authenticationRetryPending;
        private bool authenticationVerifyPending;
        private bool loginSubmitted;
        private int authenticationAttempt;
        private int remainingSeconds;
        private DateTime nextCheckAt;
        private DateTime authenticationDeadline;
        private DateTime authenticationRetryAt;
        private DateTime authenticationVerifyAt;

        public MainForm()
        {
            Text = "Internet 断网关机监控";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(720, 660);
            MinimumSize = new Size(720, 660);
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = LoadAppIcon();

            countdownMinutesInput = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1440,
                Value = 10,
                Width = 120,
                Location = new Point(165, 24)
            };

            checkIntervalInput = new NumericUpDown
            {
                Minimum = 5,
                Maximum = 300,
                Value = 10,
                Width = 120,
                Location = new Point(165, 64)
            };

            authUrlInput = new TextBox
            {
                Width = 430,
                Location = new Point(165, 104)
            };

            usernameInput = new TextBox
            {
                Width = 180,
                Location = new Point(165, 144)
            };

            passwordInput = new TextBox
            {
                Width = 180,
                Location = new Point(415, 144),
                UseSystemPasswordChar = true
            };

            startButton = new Button
            {
                Text = "开始检测",
                Location = new Point(485, 22),
                Size = new Size(95, 32)
            };
            startButton.Click += StartButton_Click;

            stopButton = new Button
            {
                Text = "停止检测",
                Location = new Point(590, 22),
                Size = new Size(95, 32),
                Enabled = false
            };
            stopButton.Click += StopButton_Click;

            statusValueLabel = new Label
            {
                Text = "未开始",
                AutoSize = true,
                Location = new Point(165, 190),
                ForeColor = Color.DimGray
            };

            countdownValueLabel = new Label
            {
                Text = "--:--",
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold),
                Location = new Point(160, 217),
                ForeColor = Color.FromArgb(25, 94, 160)
            };

            logTextBox = new TextBox
            {
                Location = new Point(24, 310),
                Size = new Size(660, 135),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical
            };

            authBrowser = new WebBrowser
            {
                Location = new Point(24, 480),
                Size = new Size(660, 135),
                ScriptErrorsSuppressed = true
            };
            authBrowser.DocumentCompleted += AuthBrowser_DocumentCompleted;

            Controls.Add(CreateLabel("关机倒计时（分钟）", 24, 27));
            Controls.Add(countdownMinutesInput);
            Controls.Add(CreateLabel("检测间隔（秒）", 24, 67));
            Controls.Add(checkIntervalInput);
            Controls.Add(CreateLabel("认证页面网址", 24, 107));
            Controls.Add(authUrlInput);
            Controls.Add(CreateLabel("登录账号", 24, 147));
            Controls.Add(usernameInput);
            Controls.Add(CreateLabel("登录密码", 340, 147));
            Controls.Add(passwordInput);
            Controls.Add(startButton);
            Controls.Add(stopButton);
            Controls.Add(CreateLabel("当前状态", 24, 190));
            Controls.Add(statusValueLabel);
            Controls.Add(CreateLabel("剩余倒计时", 24, 231));
            Controls.Add(countdownValueLabel);
            Controls.Add(CreateLabel("运行日志", 24, 288));
            Controls.Add(logTextBox);
            Controls.Add(CreateLabel("认证页面", 24, 458));
            Controls.Add(authBrowser);

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
            ResetAuthenticationState();
            remainingSeconds = 0;
            nextCheckAt = DateTime.MinValue;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            SetInputsEnabled(false);
            SetStatus("检测中", Color.FromArgb(25, 94, 160));
            AddLog("开始检测 Internet 连接。");
            uiTimer.Start();
            BeginNetworkCheck();
        }

        private void StopMonitoring(string reason)
        {
            if (!monitoring && !countdownActive && !authenticationActive)
            {
                return;
            }

            monitoring = false;
            countdownActive = false;
            ResetAuthenticationState();
            remainingSeconds = 0;
            uiTimer.Stop();
            startButton.Enabled = true;
            stopButton.Enabled = false;
            SetInputsEnabled(true);
            SetStatus("已停止", Color.DimGray);
            UpdateCountdownLabel();
            AddLog(reason);
        }

        private void SetInputsEnabled(bool enabled)
        {
            countdownMinutesInput.Enabled = enabled;
            checkIntervalInput.Enabled = enabled;
            authUrlInput.Enabled = enabled;
            usernameInput.Enabled = enabled;
            passwordInput.Enabled = enabled;
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

            if (authenticationRetryPending && DateTime.Now >= authenticationRetryAt)
            {
                StartAuthenticationAttempt(authenticationAttempt + 1);
            }

            if (authenticationVerifyPending && DateTime.Now >= authenticationVerifyAt)
            {
                authenticationVerifyPending = false;
                BeginNetworkCheck();
            }

            if (authenticationActive && DateTime.Now >= authenticationDeadline)
            {
                FailAuthenticationAttempt("认证页面打开或登录超时。");
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

                if (authenticationActive || authenticationRetryPending)
                {
                    AddLog("Internet 已恢复，停止认证重试。");
                }

                countdownActive = false;
                remainingSeconds = 0;
                ResetAuthenticationState();
                SetStatus("Internet 正常", Color.ForestGreen);
                UpdateCountdownLabel();
                return;
            }

            if (countdownActive)
            {
                SetStatus("断网倒计时中", Color.Firebrick);
                return;
            }

            if (authenticationActive || authenticationRetryPending)
            {
                return;
            }

            if (HasAuthenticationSettings())
            {
                StartAuthenticationAttempt(1);
                return;
            }

            StartShutdownCountdown("无法连接 Internet，未设置认证页面，开始关机倒计时。");
        }

        private bool HasAuthenticationSettings()
        {
            return authUrlInput.Text.Trim().Length > 0;
        }

        private void StartAuthenticationAttempt(int attempt)
        {
            Uri authUri;
            if (!TryBuildUri(authUrlInput.Text.Trim(), out authUri))
            {
                StartShutdownCountdown("认证页面网址无效，开始关机倒计时。");
                return;
            }

            authenticationActive = true;
            authenticationRetryPending = false;
            authenticationVerifyPending = false;
            loginSubmitted = false;
            authenticationAttempt = attempt;
            authenticationDeadline = DateTime.Now.AddSeconds(AuthPageTimeoutSeconds);

            SetStatus("正在打开认证页面", Color.FromArgb(25, 94, 160));
            AddLog("无法连接 Internet，正在打开认证页面，第 " + attempt + " 次尝试。");

            try
            {
                authBrowser.Navigate(authUri);
            }
            catch (Exception ex)
            {
                FailAuthenticationAttempt("认证页面打开失败：" + ex.Message);
            }
        }

        private static bool TryBuildUri(string input, out Uri uri)
        {
            uri = null;

            if (input.Length == 0)
            {
                return false;
            }

            string candidate = input;
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "http://" + candidate;
            }

            return Uri.TryCreate(candidate, UriKind.Absolute, out uri);
        }

        private void AuthBrowser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {
            if (!authenticationActive || loginSubmitted || authBrowser.Document == null)
            {
                return;
            }

            bool submitted = TrySubmitAuthenticationForm();
            if (!submitted)
            {
                FailAuthenticationAttempt("认证页面已打开，但未找到可填写的账号密码表单。");
                return;
            }

            loginSubmitted = true;
            authenticationVerifyPending = true;
            authenticationDeadline = DateTime.Now.AddSeconds(AuthPageTimeoutSeconds);
            authenticationVerifyAt = DateTime.Now.AddSeconds(AuthVerifyDelaySeconds);
            SetStatus("已提交认证，正在验证", Color.FromArgb(25, 94, 160));
            AddLog("认证信息已提交，等待网络恢复验证。");
        }

        private bool TrySubmitAuthenticationForm()
        {
            HtmlDocument document = authBrowser.Document;
            if (document == null)
            {
                return false;
            }

            HtmlElement passwordElement = FindFirstInput(document, "password");
            HtmlElement usernameElement = FindUsernameInput(document, passwordElement);

            if (usernameElement == null && usernameInput.Text.Trim().Length > 0)
            {
                return false;
            }

            if (passwordElement == null && passwordInput.Text.Length > 0)
            {
                return false;
            }

            if (usernameElement != null)
            {
                usernameElement.SetAttribute("value", usernameInput.Text.Trim());
            }

            if (passwordElement != null)
            {
                passwordElement.SetAttribute("value", passwordInput.Text);
            }

            HtmlElement submitElement = FindSubmitElement(document);
            if (submitElement != null)
            {
                submitElement.InvokeMember("click");
                return true;
            }

            HtmlElement form = usernameElement != null ? usernameElement.Parent : null;
            while (form != null && !String.Equals(form.TagName, "form", StringComparison.OrdinalIgnoreCase))
            {
                form = form.Parent;
            }

            if (form != null)
            {
                form.InvokeMember("submit");
                return true;
            }

            return false;
        }

        private static HtmlElement FindUsernameInput(HtmlDocument document, HtmlElement passwordElement)
        {
            HtmlElementCollection inputs = document.GetElementsByTagName("input");
            foreach (HtmlElement input in inputs)
            {
                string type = input.GetAttribute("type").ToLowerInvariant();
                if (type == "password" || type == "hidden" || type == "submit" || type == "button" || type == "checkbox" || type == "radio")
                {
                    continue;
                }

                string name = (input.GetAttribute("name") + " " + input.GetAttribute("id") + " " + input.GetAttribute("placeholder")).ToLowerInvariant();
                if (name.Contains("user") || name.Contains("account") || name.Contains("login") || name.Contains("phone") || name.Contains("mobile") || name.Contains("email") || name.Contains("name"))
                {
                    return input;
                }
            }

            foreach (HtmlElement input in inputs)
            {
                string type = input.GetAttribute("type").ToLowerInvariant();
                if (type.Length == 0 || type == "text" || type == "email" || type == "tel")
                {
                    return input;
                }
            }

            return null;
        }

        private static HtmlElement FindFirstInput(HtmlDocument document, string inputType)
        {
            HtmlElementCollection inputs = document.GetElementsByTagName("input");
            foreach (HtmlElement input in inputs)
            {
                if (String.Equals(input.GetAttribute("type"), inputType, StringComparison.OrdinalIgnoreCase))
                {
                    return input;
                }
            }

            return null;
        }

        private static HtmlElement FindSubmitElement(HtmlDocument document)
        {
            HtmlElementCollection inputs = document.GetElementsByTagName("input");
            foreach (HtmlElement input in inputs)
            {
                string type = input.GetAttribute("type").ToLowerInvariant();
                if (type == "submit" || type == "button")
                {
                    return input;
                }
            }

            HtmlElementCollection buttons = document.GetElementsByTagName("button");
            foreach (HtmlElement button in buttons)
            {
                return button;
            }

            return null;
        }

        private void FailAuthenticationAttempt(string reason)
        {
            if (!authenticationActive)
            {
                return;
            }

            AddLog(reason);
            authenticationActive = false;
            authenticationVerifyPending = false;

            if (authenticationAttempt < 2)
            {
                authenticationRetryPending = true;
                authenticationRetryAt = DateTime.Now.AddSeconds(AuthRetryDelaySeconds);
                SetStatus("认证失败，10 秒后重试", Color.DarkOrange);
                AddLog("10 秒后重新打开认证页面。");
                return;
            }

            authenticationRetryPending = false;
            StartShutdownCountdown("认证重试仍未成功，开始关机倒计时。");
        }

        private void ResetAuthenticationState()
        {
            authenticationActive = false;
            authenticationRetryPending = false;
            authenticationVerifyPending = false;
            loginSubmitted = false;
            authenticationAttempt = 0;
            authenticationDeadline = DateTime.MinValue;
            authenticationRetryAt = DateTime.MinValue;
            authenticationVerifyAt = DateTime.MinValue;
        }

        private void StartShutdownCountdown(string reason)
        {
            if (countdownActive)
            {
                return;
            }

            countdownActive = true;
            remainingSeconds = (int)countdownMinutesInput.Value * 60;
            ResetAuthenticationState();
            SetStatus("断网倒计时中", Color.Firebrick);
            AddLog(reason);
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
