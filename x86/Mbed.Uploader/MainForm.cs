using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Windows.Forms;

namespace Microsoft.MbedUploader
{
    internal partial class MainForm : Form
    {
        FileSystemWatcher watcher;
        private String customcopypath = "";
        private String downloads;
        private String[] filetypes = { ".hex", ".uf2" };
        static private List<KeyValuePair<String, String[]>> robots = new List<KeyValuePair<String, String[]>>()
        {
            new KeyValuePair<String, String[]>(".hex", new string[] {"MINI", "MICROBIT"}),
            new KeyValuePair<String, String[]>(".uf2", new string[] {"EV3"})
        };

        public MainForm()
        {
            InitializeComponent();
            var v = typeof(MainForm).Assembly.GetName().Version;
            this.versionLabel.Text = "v" + v.Major + "." + v.Minor;
        }

        public void ReloadFileWatch(String path)
        {
            customcopypath = path;
            initializeFileWatch();
        }


        private void MainForm_Load(object sender, EventArgs e)
        {
            this.initializeFileWatch();
        }

        private void initializeFileWatch()
        {
            customcopypath = (String)Application.UserAppDataRegistry.GetValue("CustomDirectory", "");

            if (!String.IsNullOrEmpty(customcopypath) && Directory.Exists(customcopypath))
            {
                downloads = customcopypath;
            }
            else
            {
                downloads = KnownFoldersNativeMethods.GetDownloadPath();
            }

            if (String.IsNullOrEmpty(downloads) || !Directory.Exists(downloads))
            {
                this.updateStatus("oops, der `Downloads` Ordner kann nicht gefunden werden. Bitte gibt einen Pfad in den Einstellungen an.");
                return;
            }

            this.watcher = new FileSystemWatcher(downloads);
            this.watcher.Renamed += (sender, e) => this.handleFileEvent(e);
            this.watcher.Created += (sender, e) => this.handleFileEvent(e);
            this.watcher.EnableRaisingEvents = true;

            this.waitingForFileStatus();
        }

        private void waitingForFileStatus()
        {
            this.updateStatus($"Warte auf Datei ...");
            this.trayIcon.ShowBalloonTip(3000, "Bereit...", $"Warte auf Datei ...", ToolTipIcon.None);
            this.label1.Text = downloads;
        }

        delegate void Callback();

        private void updateStatus(String value)
        {
            Callback a = (Callback)(() =>
            {
                this.statusLabel.Text = value;
                this.trayIcon.Text = value;
            });
            this.Invoke(a);
        }

        void handleFileEvent(FileSystemEventArgs e)
        {
            this.handleFile(e.FullPath);
        }

        volatile int copying;
        void handleFile(String fullPath)
        {
            try
            {
                // In case this is data-url download, at least Chrome will not rename file, but instead write to it
                // directly. This mean we may catch it in the act. Let's leave it some time to finish writing.
                Thread.Sleep(500);

                var info = new FileInfo(fullPath);
                Trace.WriteLine("download: " + info.FullName);

                if (!filetypes.Contains(info.Extension)) return;
                Trace.WriteLine("download name: " + info.Name);
                if (info.Name.EndsWith(".uploaded" + info.Extension, StringComparison.OrdinalIgnoreCase)) return;
                if (info.Extension.Equals(".hex") && info.Length > 10000000)
                {
                    this.updateStatus("Die " + info.Extension + "-Datei ist zu groß!");
                    return; // make sure we don't try to copy large files
                }
                    
                // already copying?
                if (Interlocked.Exchange(ref this.copying, 1) == 1)
                    return;

                try
                {
                    var robotList = getRobotList(info.Extension);
                    var driveletters = getDrives(robotList);
                    List<String> drives = new List<String>();
                    
                    foreach (var d in driveletters)
                    {
                        drives.Add(d.RootDirectory.FullName);
                    }
                    if (drives.Count == 0)
                    {
                        this.updateStatus("Keinen Roboter gefunden");
                        this.trayIcon.ShowBalloonTip(3000, "Kopieren abgebrochen...", "Keinen Roboter gefunden", ToolTipIcon.None);
                        return;
                    }

                    var copy = "Kopiere " + info.Extension + "-Datei";
                    this.updateStatus(copy);
                    this.trayIcon.ShowBalloonTip(3000, "Kopiere...", copy, ToolTipIcon.None);
                    
                    // copy to all boards
                    copyFirmware(info, drives);

                    // move away file
                    var temp = Path.ChangeExtension(info.FullName, ".uploaded" + info.Extension);
                    try
                    {
                        File.Copy(info.FullName, temp, true);
                        File.Delete(info.FullName);
                    }
                    catch (IOException) { }
                    catch (NotSupportedException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }

                    // update ui
                    this.updateStatus("uploading done");
                    this.waitingForFileStatus();
                }
                finally
                {
                    Interlocked.Exchange(ref this.copying, 0);
                }
            }
            catch (IOException) { }
            catch (NotSupportedException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        private String[] getRobotList(String extension)
        {
            foreach (var item in robots)
            {
                if (item.Key.StartsWith(extension, StringComparison.Ordinal))
                {
                    return item.Value;
                }
            }
            return null;
        }

        static void copyFirmware(FileInfo file, List<String> drives)
        {
            var waitHandles = new List<WaitHandle>();
            foreach (var drive in drives)
            {

                var ev = new AutoResetEvent(false);
                waitHandles.Add(ev);
                ThreadPool.QueueUserWorkItem((state) =>
                {
                    try
                    {
                        var trg = Path.Combine(drive, "firmware" + file.Extension);

                        var fs1 = new FileStream(file.FullName, FileMode.Open, FileAccess.Read);

                        var fs2 = new FileStream(trg, FileMode.Create);

                        fs1.CopyTo(fs2);

                        fs2.Close(); fs1.Close();

                    }
                    catch (IOException) { }
                    catch (NotSupportedException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (ArgumentException) { }
                    ev.Set();
                }, ev);
            }

            //waits for all the threads (waitHandles) to call the .Set() method
            //and inform that the execution has finished.
            WaitHandle.WaitAll(waitHandles.ToArray());
        }

        static DriveInfo[] getDrives(String[] robotList)
        {
            var drives = DriveInfo.GetDrives();
            var r = new List<DriveInfo>();
            foreach (var di in drives)
            {
                var label = getVolumeLabel(di);
                if (robotList.Contains(label))
                    r.Add(di);
            }
            return r.ToArray();
        }

        static String getVolumeLabel(DriveInfo di)
        {
            try { return di.VolumeLabel; }
            catch (IOException) { }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return "";
        }

        private void trayIcon_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
            this.WindowState = FormWindowState.Normal;
            this.Show();
            this.Activate();
        }

        private void versionLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                Process.Start("https://github.com/OpenRoberta/openroberta-mbed-uploader/releases");
            }
            catch (IOException) { }
        }

        private void SettingsLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var settings = new Settings(customcopypath);
   
            settings.ShowDialog();
            customcopypath = settings.CustomCopyPath;
            Application.UserAppDataRegistry.SetValue("CustomDirectory", customcopypath, RegistryValueKind.String);
        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            try 
            {
                Process.Start("https://jira.iais.fraunhofer.de/wiki/display/ORInfo");
            }
            catch (Exception) { }
        }
    }
}
