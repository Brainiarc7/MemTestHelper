﻿using MahApps.Metro.Controls;
using Microsoft.VisualBasic.Devices;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MemTestHelper2
{
    public partial class MainWindow : MetroWindow
    {
        private readonly int NUM_THREADS, MAX_THREADS;

        private const string CFG_FILENAME = "MemTestHelper.cfg";

        // interval (in ms) for coverage info list
        private const int UPDATE_INTERVAL = 200;

        private MemTest[] memtests;
        private MemTestInfo[] memtestInfo;
        private BackgroundWorker coverageWorker;
        private DateTime startTime;
        private System.Timers.Timer timer;
        private bool isMinimised = true;

        public MainWindow()
        {
            InitializeComponent();

            NUM_THREADS = Convert.ToInt32(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"));
            MAX_THREADS = NUM_THREADS * 4;
            memtests = new MemTest[MAX_THREADS];
            // index 0 stores the total
            memtestInfo = new MemTestInfo[MAX_THREADS + 1];

            InitCboThreads();
            InitLstCoverage();
            InitCboRows();
            CentreXYOffsets();
            UpdateLstCoverage();

            coverageWorker = new BackgroundWorker();
            coverageWorker.WorkerSupportsCancellation = true;
            coverageWorker.DoWork += new DoWorkEventHandler((sender, e) =>
            {
                var worker = sender as BackgroundWorker;
                while (!worker.CancellationPending)
                {
                    UpdateCoverageInfo();
                    Thread.Sleep(UPDATE_INTERVAL);
                }

                e.Cancel = true;
            });
            coverageWorker.RunWorkerCompleted +=
            new RunWorkerCompletedEventHandler((sender, e) =>
            {
                // Wait for all MemTests to stop completely.
                while (IsAnyMemTestStopping())
                    Thread.Sleep(100);

                // TODO: figure out why total coverage is sometimes
                // reporting 0.0 after stopping
                UpdateCoverageInfo(false);
            });

            timer = new System.Timers.Timer(1000);
            timer.Elapsed += new System.Timers.ElapsedEventHandler((sender, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var threads = (int)cboThreads.SelectedItem;
                    var elapsed = e.SignalTime - startTime;

                    lblElapsedTime.Content = String.Format("{0:00}h{1:00}m{2:00}s",
                                                           (int)(elapsed.TotalHours),
                                                           elapsed.Minutes,
                                                           elapsed.Seconds);

                    // This thread only accesses element 0.
                    lock (memtestInfo)
                    {
                        var totalCoverage = memtestInfo[0].Coverage;
                        if (totalCoverage <= 0.0) return;

                        // Round up to next multiple of 100.
                        var nextCoverage = ((int)(totalCoverage / 100) + 1) * 100;
                        var elapsedms = elapsed.TotalMilliseconds;
                        var est = (elapsedms / totalCoverage * nextCoverage) - elapsedms;

                        TimeSpan estimatedTime = TimeSpan.FromMilliseconds(est);
                        lblEstimatedTime.Content = String.Format("{0:00}h{1:00}m{2:00}s to {3}%",
                                                                 (int)(estimatedTime.TotalHours),
                                                                 estimatedTime.Minutes,
                                                                 estimatedTime.Seconds,
                                                                 nextCoverage);

                        var ram = Convert.ToInt32(txtRAM.Text);
                        var speed = (totalCoverage / 100) * ram / (elapsedms / 1000);
                        lblSpeed.Content = $"{speed:f2}MB/s";
                    }
                });
            });
            
        }

        // Event Handling //

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();

            InitCboRows();
            UpdateLstCoverage();
            CentreXYOffsets();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            CloseMemTests();
            SaveConfig();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var threads = (int)cboThreads.SelectedItem;
            switch (WindowState)
            {
                // Minimise MemTest instances.
                case WindowState.Minimized:
                    RunInBackground(() =>
                    {
                        for (var i = 0; i < threads; i++)
                        {
                            if (memtests[i] != null)
                            {
                                memtests[i].Minimised = true;
                                Thread.Sleep(10);
                            }
                        }
                    });
                    break;

                // Restore previous state of MemTest instances.
                case WindowState.Normal:
                    RunInBackground(() =>
                    {
                        /*
                         * isMinimised is true when user clicked the hide button.
                         * This means that the memtest instances should be kept minimised.
                         */
                        if (!isMinimised)
                        {
                            for (var i = 0; i < threads; i++)
                            {
                                if (memtests[i] != null)
                                {
                                    memtests[i].Minimised = false;
                                    Thread.Sleep(10);
                                }
                            }

                            // User may have changed offsets while minimised.
                            LayOutMemTests();

                            Activate();

                            // Hack to bring form to top.
                            //Topmost = true;
                            //Thread.Sleep(10);
                            //Topmost = false;
                        }
                    });
                    break;
            }
        }

        private void btnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(MemTest.EXE_NAME))
            {
                MessageBox.Show(MemTest.EXE_NAME + " not found");
                return;
            }

            if (!ValidateInput()) return;

            txtRAM.IsEnabled = false;
            cboThreads.IsEnabled = false;
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            chkStopAt.IsEnabled = false;
            txtStopAt.IsEnabled = false;
            chkStopAtTotal.IsEnabled = false;
            chkStopOnError.IsEnabled = false;
            chkStartMin.IsEnabled = false;

            // Run in background as StartMemTests() can block.
            RunInBackground(() =>
            {
                StartMemTests();

                if (!coverageWorker.IsBusy)
                    coverageWorker.RunWorkerAsync();

                startTime = DateTime.Now;
                timer.Start();

                Activate();
            });
        }

        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            Parallel.For(0, (int)cboThreads.SelectedItem, i =>
            {
                if (!memtests[i].Finished)
                    memtests[i].Stop();
            });

            coverageWorker.CancelAsync();
            timer.Stop();

            txtRAM.IsEnabled = true;
            cboThreads.IsEnabled = true;
            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            chkStopAt.IsEnabled = true;
            if (chkStopAt.IsEnabled)
            {
                txtStopAt.IsEnabled = true;
                chkStopAtTotal.IsEnabled = true;
            }
            chkStopOnError.IsEnabled = true;
            chkStartMin.IsEnabled = true;

            // Wait for all memtests to fully stop.
            while (IsAnyMemTestStopping())
                Thread.Sleep(100);

            // Update speed.
            var ram = Convert.ToInt32(txtRAM.Text);
            var elapsedTime = TimeSpan.ParseExact((string)lblElapsedTime.Content, @"hh\hmm\mss\s", 
                                                  CultureInfo.InvariantCulture).TotalSeconds;
            // 0 is the total coverage.
            var speed = (memtestInfo[0].Coverage / 100) * ram / elapsedTime;
            lblSpeed.Content = $"{speed:f2}MB/s";

            MessageBox.Show("Please check if there are any errors", "MemTest finished");
        }

        private void btnShow_Click(object sender, RoutedEventArgs e)
        {
            // Run in background as Thread.Sleep can lockup the GUI.
            var threads = (int)cboThreads.SelectedItem;
            RunInBackground(() =>
            {
                for (var i = 0; i < threads; i++)
                {
                    var memtest = memtests[i];
                    if (memtest != null)
                    {
                        memtest.Minimised = false;

                        Thread.Sleep(10);
                    }
                }

                isMinimised = false;

                // User may have changed offsets while minimised.
                LayOutMemTests();

                Activate();
            });
        }

        private void btnHide_Click(object sender, RoutedEventArgs e)
        {
            var threads = (int)cboThreads.SelectedItem;
            RunInBackground(() =>
            {
                for (var i = 0; i < threads; i++)
                {
                    var memtest = memtests[i];
                    if (memtest != null)
                    {
                        memtest.Minimised = true;
                        Thread.Sleep(10);
                    }
                }

                isMinimised = true;
            });
        }

        private void cboThreads_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateLstCoverage();

            cboRows.Items.Clear();
            InitCboRows();
            CentreXYOffsets();
        }

        private void Offset_Changed(object sender, RoutedPropertyChangedEventArgs<double?> e)
        {
            RunInBackground(() => { LayOutMemTests(); });
        }

        private void btnCentre_Click(object sender, RoutedEventArgs e)
        {
            CentreXYOffsets();
        }

        private void cboRows_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CentreXYOffsets();
        }

        private void chkStopAt_Checked(object sender, RoutedEventArgs e)
        {
            txtStopAt.IsEnabled = true;
            chkStopAtTotal.IsEnabled = true;
        }

        private void chkStopAt_Unchecked(object sender, RoutedEventArgs e)
        {
            txtStopAt.IsEnabled = false;
            chkStopAtTotal.IsEnabled = false;
        }

        private void txtDiscord_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            (sender as TextBox).SelectAll();
        }

        // Helper Functions //

        private void InitCboThreads()
        {
            cboThreads.Items.Clear();

            for (var i = 0; i < MAX_THREADS; i++)
                cboThreads.Items.Add(i + 1);

            cboThreads.SelectedItem = NUM_THREADS;
        }

        private void InitCboRows()
        {
            cboRows.Items.Clear();

            var threads = (int)cboThreads.SelectedItem;

            for (var i = 1; i <= threads; i++)
            {
                if (threads % i == 0)
                    cboRows.Items.Add(i);
            }

            cboRows.SelectedItem = threads % 2 == 0 ? 2 : 1;
        }

        private void InitLstCoverage()
        {
            for (var i = 0; i <= (int)cboThreads.SelectedItem; i++)
            {
                // first row is total
                memtestInfo[i] = new MemTestInfo(i == 0 ? "T" : i.ToString(), 0.0, 0);
            }

            lstCoverage.ItemsSource = memtestInfo;
        }

        private void UpdateLstCoverage()
        {
            var threads = (int)cboThreads.SelectedItem;
            var items = (MemTestInfo[])lstCoverage.ItemsSource;
            if (items == null) return;

            var count = items.Count((m) => { return m != null && m.Valid; });
            // items[0] stores the total.
            if (count > threads)
            {
                for (var i = threads + 1; i < count; i++)
                    items[i].Valid = false;
            }
            else
            {
                for (var i = count; i <= threads; i++)
                    items[i] = new MemTestInfo(i.ToString(), 0.0, 0);
            }

            // Only show valid items.
            var view = CollectionViewSource.GetDefaultView(items);
            view.Filter = o =>
            {
                if (o == null) return false;

                var info = o as MemTestInfo;
                return info.Valid;
            };
            view.Refresh();
        }

        // Returns free RAM in MB.
        private ulong GetFreeRAM()
        {
            /*
             * Available RAM = Free + Standby
             * https://superuser.com/a/1032481
             * 
             * Cached = sum of stuff
             * https://www.reddit.com/r/PowerShell/comments/ao59ha/cached_memory_as_it_appears_in_the_performance/efye75r/
             * 
             * Standby = Cached - Modifed
             */
            /*
            float standby = new PerformanceCounter("Memory", "Cache Bytes").NextValue() +
                            //new PerformanceCounter("Memory", "Modified Page List Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Core Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Reserve Bytes").NextValue();
            */

            return new ComputerInfo().AvailablePhysicalMemory / (1024 * 1024);
        }

        // TODO: error checking
        private bool LoadConfig()
        {
            string[] validKeys = { "ram", "threads", "x_offset", "y_offset",
                                   "x_spacing", "y_spacing", "rows", "stop_at",
                                   "stop_at_value", "stop_at_total", "stop_on_error",
                                   "start_min" };

            try
            {
                string[] lines = File.ReadAllLines(CFG_FILENAME);
                Dictionary<string, int> cfg = new Dictionary<string, int>();

                foreach (string l in lines)
                {
                    var s = l.Split('=');
                    if (s.Length != 2) continue;
                    s[0] = s[0].Trim();
                    s[1] = s[1].Trim();

                    if (validKeys.Contains(s[0]))
                    {
                        // skip blank values
                        if (s[1].Length == 0) continue;

                        int v;
                        if (Int32.TryParse(s[1], out v))
                            cfg.Add(s[0], v);
                        else return false;
                    }
                    else return false;
                }

                // input values in controls
                foreach (KeyValuePair<string, int> kv in cfg)
                {
                    switch (kv.Key)
                    {
                        case "ram":
                            txtRAM.Text = kv.Value.ToString();
                            break;
                        case "threads":
                            cboThreads.SelectedItem = kv.Value;
                            break;

                        case "x_offset":
                            udXOffset.Value = kv.Value;
                            break;
                        case "y_offset":
                            udYOffset.Value = kv.Value;
                            break;

                        case "x_spacing":
                            udXSpacing.Value = kv.Value;
                            break;
                        case "y_spacing":
                            udYSpacing.Value = kv.Value;
                            break;

                        case "stop_at":
                            chkStopAt.IsChecked = kv.Value != 0;
                            break;
                        case "stop_at_value":
                            txtStopAt.Text = kv.Value.ToString();
                            break;
                        case "stop_at_total":
                            chkStopAtTotal.IsChecked = kv.Value != 0;
                            break;

                        case "stop_on_error":
                            chkStopOnError.IsChecked = kv.Value != 0;
                            break;

                        case "start_min":
                            chkStartMin.IsChecked = kv.Value != 0;
                            break;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine(CFG_FILENAME + " not found");
                return false;
            }

            return true;
        }

        private bool SaveConfig()
        {
            StreamWriter file = null;
            try
            {
                file = new StreamWriter(CFG_FILENAME);
                var lines = new List<string>();

                lines.Add($"ram = {txtRAM.Text}");
                lines.Add($"threads = {(int)cboThreads.SelectedItem}");

                lines.Add($"x_offset = {udXOffset.Value}");
                lines.Add($"y_offset = {udYOffset.Value}");
                lines.Add($"x_spacing = {udXSpacing.Value}");
                lines.Add($"y_spacing = {udYSpacing.Value}");
                lines.Add($"rows = {cboRows.SelectedItem}");

                lines.Add(string.Format("stop_at = {0}", chkStopAt.IsChecked.Value ? 1 : 0));
                lines.Add($"stop_at_value = {txtStopAt.Text}");
                lines.Add(string.Format("stop_at_total = {0}", chkStopAtTotal.IsChecked.Value ? 1 : 0));
                lines.Add(string.Format("stop_on_error = {0}", chkStopOnError.IsChecked.Value ? 1 : 0));

                lines.Add(string.Format("start_min = {0}", chkStartMin.IsChecked.Value ? 1 : 0));

                foreach (var l in lines)
                    file.WriteLine(l);
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to save " + CFG_FILENAME);
                return false;
            }
            finally
            {
                // BaseStream isn't null when open.
                if (file != null && file.BaseStream != null)
                    file.Close();
            }

            return true;
        }

        private bool ValidateInput()
        {
            var ci = new ComputerInfo();
            UInt64 totalRAM = ci.TotalPhysicalMemory / (1024 * 1024),
                   availableRAM = ci.AvailablePhysicalMemory / (1024 * 1024);

            var ramText = txtRAM.Text;
            // automatically input available ram if empty
            if (ramText.Length == 0)
            {
                ramText = GetFreeRAM().ToString();
                txtRAM.Text = ramText;
            }
            else
            {
                if (!ramText.All(char.IsDigit))
                {
                    ShowErrorMsgBox("Amount of RAM must be an integer");
                    return false;
                }
            }

            int threads = (int)cboThreads.SelectedItem,
                ram = Convert.ToInt32(ramText);
            if (ram < threads)
            {
                ShowErrorMsgBox($"Amount of RAM must be greater than {threads}");
                return false;
            }

            if (ram > MemTest.MAX_RAM * threads)
            {
                ShowErrorMsgBox(
                    $"Amount of RAM must be at most {MemTest.MAX_RAM * threads}\n" +
                    "Try increasing the number of threads\n" +
                    "or reducing amount of RAM"
                );
                return false;
            }

            if ((ulong)ram > totalRAM)
            {
                ShowErrorMsgBox($"Amount of RAM exceeds total RAM ({totalRAM})");
                return false;
            }

            if ((ulong)ram > availableRAM)
            {
                var res = MessageBox.Show(
                    $"Amount of RAM exceeds available RAM ({availableRAM})\n" +
                    "This will cause RAM to be paged to your storage,\n" +
                    "which may make MemTest really slow.\n" +
                    "Continue?",
                    "Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );
                if (res == MessageBoxResult.No)
                    return false;
            }

            // validate stop at % and error count
            if (chkStopAt.IsChecked.Value)
            {
                var stopAtText = txtStopAt.Text;

                if (stopAtText == "")
                {
                    ShowErrorMsgBox("Please enter stop at (%)");
                    return false;
                }

                if (!stopAtText.All(char.IsDigit))
                {
                    ShowErrorMsgBox("Stop at (%) must be an integer");
                    return false;
                }

                var stopAt = Convert.ToInt32(stopAtText);
                if (stopAt <= 0)
                {
                    ShowErrorMsgBox("Stop at (%) must be greater than 0");
                    return false;
                }
            }

            return true;
        }

        private void CentreXYOffsets()
        {
            if (cboRows.SelectedIndex == -1 || cboThreads.SelectedIndex == -1)
                return;

            var workArea = SystemParameters.WorkArea;
            int rows = (int)cboRows.SelectedItem,
                cols = (int)cboThreads.SelectedItem / rows,
                xOffset = ((int)workArea.Width - MemTest.WIDTH * cols) / 2,
                yOffset = ((int)workArea.Height - MemTest.HEIGHT * rows) / 2;

            udXOffset.Value = xOffset;
            udYOffset.Value = yOffset;
        }

        private void StartMemTests()
        {
            CloseAllMemTests();

            var threads = (int)cboThreads.SelectedItem;
            var ram = Convert.ToDouble(txtRAM.Text) / threads;
            var startMin = chkStartMin.IsChecked.Value;
            Parallel.For(0, threads, i =>
            {
                memtests[i] = new MemTest();
                memtests[i].Start(ram, startMin);
            });

            if (!chkStartMin.IsChecked.Value)
                LayOutMemTests();
        }

        private void LayOutMemTests()
        {
            int xOffset = (int)udXOffset.Value,
                yOffset = (int)udYOffset.Value,
                xSpacing = (int)udXSpacing.Value - 5,
                ySpacing = (int)udYSpacing.Value - 3,
                rows = (int)cboRows.SelectedItem,
                cols = (int)cboThreads.SelectedItem / rows;

            Parallel.For(0, (int)cboThreads.SelectedItem, i =>
            {
                var memtest = memtests[i];
                if (memtest == null || !memtest.Started) return;

                int r = i / cols,
                    c = i % cols,
                    x = c * MemTest.WIDTH + c * xSpacing + xOffset,
                    y = r * MemTest.HEIGHT + r * ySpacing + yOffset;

                memtest.Location = new Point(x, y);
            });
        }

        // Only close MemTests started by MemTestHelper.
        private void CloseMemTests()
        {
            Parallel.For(0, (int)cboThreads.SelectedItem, i =>
            {
                try
                {
                    memtests[i].Close();
                }
                catch (Exception)
                {
                    Console.WriteLine($"Failed to close MemTest #{i}");
                }
            });
        }

        /* 
         * Close all MemTests, regardless of if they were
         * started by MemTestHelper.
         */
        private void CloseAllMemTests()
        {
            // remove the .exe
            var name = MemTest.EXE_NAME.Substring(0, MemTest.EXE_NAME.Length - 4);
            var processes = Process.GetProcessesByName(name);
            Parallel.ForEach(processes, p => { p.Kill(); });
        }

        private void UpdateCoverageInfo(bool shouldCheck = true)
        {
            lstCoverage.Invoke(() =>
            {
                var threads = (int)cboThreads.SelectedItem;
                var totalCoverage = 0.0;
                var totalErrors = 0;

                for (var i = 1; i <= threads; i++)
                {
                    var memtest = memtests[i - 1];
                    var mti = memtestInfo[i];
                    if (memtest == null) return;
                    var info = memtest.GetCoverageInfo();
                    if (info == null) return;
                    double coverage = info.Item1;
                    int errors = info.Item2;

                    mti.Coverage = coverage;
                    mti.Errors = errors;

                    if (shouldCheck)
                    {
                        // Check coverage %.
                        if (chkStopAt.IsChecked.Value && !chkStopAtTotal.IsChecked.Value)
                        {
                            var stopAt = Convert.ToInt32(txtStopAt.Text);
                            if (coverage > stopAt)
                            {
                                if (!memtest.Finished)
                                    memtest.Stop();
                            }
                        }

                        // Check error count.
                        if (chkStopOnError.IsChecked.Value)
                        {
                            var item = lstCoverage.ItemContainerGenerator.ContainerFromIndex(i) as ListViewItem;
                            if (errors > 0)
                            {
                                memtest.ClickNagMessageBox("MemTest Error", MemTest.MsgBoxButton.NO);
                                item.Foreground = Brushes.Red;
                                ClickBtnStop();
                            }
                            else item.Foreground = Brushes.White;
                        }
                    }

                    totalCoverage += coverage;
                    totalErrors += errors;
                }

                // Element 0 accessed in time.Elapsed event.
                lock (memtestInfo)
                {
                    // Update the total coverage and errors.
                    memtestInfo[0].Coverage = totalCoverage;
                    memtestInfo[0].Errors = totalErrors;
                }

                if (shouldCheck)
                {
                    // Check total coverage.
                    if (chkStopAt.IsChecked.Value && chkStopAtTotal.IsChecked.Value)
                    {
                        var stopAt = Convert.ToInt32(txtStopAt.Text);
                        if (totalCoverage > stopAt)
                            ClickBtnStop();
                    }

                    if (IsAllFinished())
                        ClickBtnStop();
                }
            });
        }

        /*
         * MemTest can take a while to stop,
         * which causes the total to return 0.
         */
        private bool IsAnyMemTestStopping()
        {
            for (var i = 0; i < (int)cboThreads.SelectedItem; i++)
            {
                if (memtests[i].Stopping)
                    return true;
            }

            return false;
        }

        /* 
         * PerformClick() only works if the button is visible
         * switch to main tab and PerformClick() then switch
         * back to the tab that the user was on.
         */
        private void ClickBtnStop()
        {
            var currTab = tabControl.SelectedItem;
            if (currTab != tabMain)
                tabControl.SelectedItem = tabMain;

            // Click the stop button.
            // https://stackoverflow.com/a/728444
            var peer = new ButtonAutomationPeer(btnStop);
            var provider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
            provider.Invoke();

            tabControl.SelectedItem = currTab;
        }

        private void ShowErrorMsgBox(string msg)
        {
            MessageBox.Show(
                msg,
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }

        private bool IsAllFinished()
        {
            for (var i = 0; i < (int)cboThreads.SelectedItem; i++)
            {
                if (!memtests[i].Finished)
                    return false;
            }

            return true;
        }

        private void RunInBackground(Action method)
        {
            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += new DoWorkEventHandler((sender, e) =>
            {
                Dispatcher.Invoke(method);
            });
            bw.RunWorkerAsync();
        }

        class MemTestInfo : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;

            private string no;
            private double coverage;
            private int errors;
            private bool valid;

            public MemTestInfo(string no, double coverage, int errors)
            {
                this.no = no;
                this.coverage = coverage;
                this.errors = errors;
                valid = true;
            }

            public string No
            {
                get { return no; }
                set { no = value; }
            }
            public double Coverage
            {
                get { return coverage; }
                set { coverage = value; NotifyPropertyChanged(); }
            }
            public int Errors
            {
                get { return errors; }
                set { errors = value; NotifyPropertyChanged(); }
            }
            public bool Valid
            {
                get { return valid; }
                set { valid = value; }
            }

            private void NotifyPropertyChanged([CallerMemberName] string property = "")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
    }
}