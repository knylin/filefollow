using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FileFollow
{
    // Update check: asks GitHub for the latest release now and then and offers it from the tray when it is newer.
    // Only the release listing is fetched; nothing is downloaded or installed by itself.
    partial class MainForm
    {
        public const string AppVersion = AppInfo.Version; // see Version.cs
        const string RepoApi = "https://api.github.com/repos/knylin/filefollow/releases/latest";
        const string ReleasesPage = "https://github.com/knylin/filefollow/releases/latest";

        ToolStripMenuItem miUpdate, miAutoUpdate;
        IconButton btnUpdate;
        System.Windows.Forms.Timer updateTimer;
        string newTag, newUrl, balloonUrl;
        bool checking;

        bool AutoUpdate { get { return Cfg("updatecheck") != "0"; } }

        void AddUpdateTrayItems(ContextMenuStrip menu)
        {
            miUpdate = new ToolStripMenuItem(L.T("檢查更新", "Check for updates"));
            miUpdate.Click += delegate
            {
                if (newUrl != null) Open(newUrl);
                else CheckUpdate(true);
            };
            miAutoUpdate = new ToolStripMenuItem(L.T("自動檢查更新", "Check for updates automatically")) { Checked = AutoUpdate };
            miAutoUpdate.Click += delegate
            {
                miAutoUpdate.Checked = !miAutoUpdate.Checked;
                cfg["updatecheck"] = miAutoUpdate.Checked ? "1" : "0";
                SaveSettings();
            };
            menu.Items.Add(miUpdate);
            menu.Items.Add(miAutoUpdate);
        }

        void StartUpdateChecks()
        {
            tray.BalloonTipClicked += delegate { if (balloonUrl != null) Open(balloonUrl); };
            tray.BalloonTipClosed += delegate { balloonUrl = null; };
            updateTimer = new System.Windows.Forms.Timer { Interval = 15000 }; // first look shortly after start, then daily
            updateTimer.Tick += delegate
            {
                updateTimer.Interval = 24 * 60 * 60 * 1000;
                if (AutoUpdate) CheckUpdate(false);
            };
            updateTimer.Start();
        }

        // manual: also reports "up to date" and errors; automatic checks speak only about a version not announced yet
        void CheckUpdate(bool manual)
        {
            if (checking) return;
            checking = true;
            new Thread(() =>
            {
                string tag, url, error;
                FetchLatest(out tag, out url, out error);
                try { BeginInvoke((Action)(() => UpdateResult(tag, url, error, manual))); } catch { }
            }) { IsBackground = true }.Start();
        }

        public static void FetchLatest(out string tag, out string url, out string error)
        {
            tag = url = error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                var req = (HttpWebRequest)WebRequest.Create(RepoApi);
                req.UserAgent = "FileFollow/" + AppVersion;
                req.Accept = "application/vnd.github+json";
                req.Timeout = 15000;
                string json;
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                tag = d.ContainsKey("tag_name") ? d["tag_name"] as string : null;
                url = d.ContainsKey("html_url") ? d["html_url"] as string : null;
            }
            catch (Exception ex) { error = ex.Message; }
        }

        // FILEFOLLOW_FAKEVERSION pretends to be an older build, for trying out the notice
        public static string CurrentVersion
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("FILEFOLLOW_FAKEVERSION");
                return string.IsNullOrEmpty(v) ? AppVersion : v;
            }
        }

        void UpdateResult(string tag, string url, string error, bool manual)
        {
            checking = false;
            if (error != null || tag == null)
            {
                if (manual) Balloon(L.T("無法檢查更新", "Couldn't check for updates"), error ?? L.T("GitHub 沒有回應版本資訊", "GitHub returned no version"), null);
                return;
            }
            if (!Newer(tag, CurrentVersion))
            {
                if (manual) Balloon(L.T("已是最新版本", "You're up to date"), L.T("目前版本 ", "Current version ") + CurrentVersion, null);
                return;
            }
            newTag = tag;
            newUrl = url != null && url.StartsWith("https://github.com/") ? url : ReleasesPage;
            miUpdate.Text = L.T("下載新版本 ", "Download new version ") + tag;
            tips.SetToolTip(btnUpdate, L.T("有新版本 ", "New version ") + tag + L.T("，點一下打開下載頁", " available: click to open the download page"));
            btnUpdate.Visible = true;
            if (manual || Cfg("update_notified") != tag)
            {
                cfg["update_notified"] = tag;
                SaveSettings();
                Balloon(L.T("跟檔夾有新版本 ", "FileFollow update ") + tag,
                    L.T("目前版本 ", "You have ") + CurrentVersion + L.T("。點這裡打開下載頁。", ". Click to open the download page."), newUrl);
            }
        }

        void Balloon(string title, string text, string url)
        {
            balloonUrl = url;
            tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Info);
        }

        // "v1.4.0" vs "1.3.0": compares the dotted numbers
        public static bool Newer(string tag, string current)
        {
            Version a, b;
            return Version.TryParse(tag.TrimStart('v', 'V'), out a) && Version.TryParse(current, out b) && a > b;
        }

        static void Open(string url)
        {
            try { Process.Start(url); } catch { }
        }
    }
}
