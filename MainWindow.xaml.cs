using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MusicWidget
{
    public partial class MainWindow : Window
    {
        private readonly SystemMediaHelper _mediaHelper = new SystemMediaHelper();

        private readonly DispatcherTimer _autoHideTimer;
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _mousePollTimer;

        // 定义两种停留时间
        private readonly TimeSpan _longDelay = TimeSpan.FromSeconds(8); // 切歌/操作后：停留10秒
        private readonly TimeSpan _shortDelay = TimeSpan.FromSeconds(2); // 鼠标唤醒后：停留2秒

        private bool _isExpanded = false;
        private bool _isPinned = false;
        private bool _isVisible = false;
        private bool _hasMusic = false;

        // 本地播放状态记录
        private bool _localIsPlaying = false;

        public MainWindow()
        {
            InitializeComponent();

            // 1. 自动隐藏倒计时
            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Interval = _longDelay;
            _autoHideTimer.Tick += (s, e) => TryHideWidget();

            // 2. 进度条刷新
            _progressTimer = new DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromSeconds(1);
            _progressTimer.Tick += ProgressTimer_Tick;

            // 3. 鼠标唤醒检测 【核心优化】
            _mousePollTimer = new DispatcherTimer();
            
            _mousePollTimer.Interval = TimeSpan.FromMilliseconds(101);
            _mousePollTimer.Tick += MousePollTimer_Tick;
            _mousePollTimer.Start();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            this.Left = (screenWidth - this.Width) / 2;
            this.Top = 10;

            _mediaHelper.OnMediaChanged += UpdateUI;
            _mediaHelper.OnPlaybackStateChanged += UpdatePlayButton;

            HideFromAltTab();

            await _mediaHelper.StartAsync();
            _progressTimer.Start();
        }

        // ================== 歌名滚动 (Marquee) ==================

        private void CheckAndStartMarquee()
        {
            TitleTransform.BeginAnimation(TranslateTransform.XProperty, null);
            TitleTransform.X = 0;

            TitleContainer.UpdateLayout();
            TxtTitle.UpdateLayout();

            double textWidth = TxtTitle.DesiredSize.Width;
            double containerWidth = TitleContainer.ActualWidth;

            if (containerWidth <= 0) return;

            if (textWidth > containerWidth)
            {
                double overflow = textWidth - containerWidth + 30;

                DoubleAnimationUsingKeyFrames anim = new DoubleAnimationUsingKeyFrames();
                anim.Duration = TimeSpan.FromSeconds(12);
                anim.RepeatBehavior = RepeatBehavior.Forever;

                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(8)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(10))));

                TitleTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            }
        }

        // ================== 进度条 ==================

        // ================== 进度条更新 (修复重叠问题) ==================

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            var (current, total) = _mediaHelper.GetTimeline();

            if (total.TotalSeconds > 0)
            {
                double val = current.TotalSeconds;
                double max = total.TotalSeconds;

                // 更新大进度条
                PbProgress.Maximum = max;
                PbProgress.Value = val;

                // 更新时间文字 (拆分为左右两个，更清晰)
                TxtCurrentTime.Text = current.ToString(@"mm\:ss");
                TxtTotalTime.Text = total.ToString(@"mm\:ss");

                // 更新迷你进度条
                PbMini.Maximum = max;
                PbMini.Value = val;
            }
            else
            {
                // 获取不到进度时 (如直播或不支持的软件)
                PbProgress.Value = 0;
                PbMini.Value = 0;
                TxtCurrentTime.Text = "--:--";
                TxtTotalTime.Text = "--:--";
            }
        }

        // ================== 鼠标唤醒 (核心逻辑) ==================

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        private void MousePollTimer_Tick(object? sender, EventArgs e)
        {
            if (!_hasMusic) return;

            GetCursorPos(out POINT p);

            double triggerLeft = this.Left;
            double triggerRight = this.Left + this.Width;
            double triggerTop = 0;
            double triggerBottom = 80;

            if (p.X >= triggerLeft && p.X <= triggerRight &&
                p.Y >= triggerTop && p.Y <= triggerBottom)
            {
                if (!_isVisible) ShowWidget();

                // 【关键修复】
                // 如果只是鼠标划过（未展开），使用短延时 (2s)
                // 如果已经展开了，保持长延时 (10s)
                if (!_isExpanded) ResetAutoHide(_shortDelay);
                else ResetAutoHide(_longDelay);
            }
        }

        // ================= 交互 =================

        private void MainBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 3)
            {
                _isPinned = !_isPinned;
                TxtPinned.Visibility = _isPinned ? Visibility.Visible : Visibility.Collapsed;
                if (_isPinned) _autoHideTimer.Stop();
                else ResetAutoHide(_longDelay);
            }
            else if (e.ClickCount == 1)
            {
                ToggleExpand();
                ResetAutoHide(_longDelay); // 点击属于主动操作，设为长延时
            }
        }

        private void ToggleExpand()
        {
            var sbKey = _isExpanded ? "AnimCollapse" : "AnimExpand";
            var sb = (Storyboard)this.Resources[sbKey];
            sb.Begin();
            _isExpanded = !_isExpanded;
        }

        // ================= 控制 =================

        private async void BtnPrev_Click(object sender, RoutedEventArgs e)
        {
            await _mediaHelper.Previous();
            ResetAutoHide(_longDelay);
            e.Handled = true;
        }

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            await _mediaHelper.Next();
            ResetAutoHide(_longDelay);
            e.Handled = true;
        }

        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            await _mediaHelper.TogglePlayPause();
            _localIsPlaying = !_localIsPlaying;
            UpdatePlayButtonIcon(_localIsPlaying);
            ResetAutoHide(_longDelay);
            e.Handled = true;
        }

        // ================= UI 更新 =================

        private void UpdateUI(string title, string artist, byte[]? coverData)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(artist))
                {
                    _hasMusic = false;
                    TryHideWidget(force: true);
                    return;
                }

                _hasMusic = true;
                TxtTitle.Text = title;
                TxtArtist.Text = artist;

                if (coverData != null && coverData.Length > 0)
                {
                    try
                    {
                        using var ms = new MemoryStream(coverData);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = ms;
                        bitmap.DecodePixelWidth = 100;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        ImgCover.Source = bitmap;
                    }
                    catch { ImgCover.Source = null; }
                }
                else ImgCover.Source = null;

                ShowWidget();
                ResetAutoHide(_longDelay); // 切歌是重要信息，设为长延时

                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(CheckAndStartMarquee));
            });

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        private void UpdatePlayButton(bool isPlaying)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _localIsPlaying = isPlaying;
                UpdatePlayButtonIcon(isPlaying);
            });
        }

        private void UpdatePlayButtonIcon(bool isPlaying)
        {
            BtnPlayPause.Content = isPlaying ? "\uE769" : "\uE768";
        }

        // ================= 显隐逻辑 =================

        private void ShowWidget()
        {
            if (!_isVisible)
            {
                var sb = (Storyboard)this.Resources["AnimShow"];
                sb.Begin();
                _isVisible = true;
            }
            // 默认展示时不强制 Reset，由触发源（鼠标或切歌）决定延时时长
        }

        private void TryHideWidget(bool force = false)
        {
            if (!force && (_isPinned || _isExpanded || IsMouseOver))
            {
                if (_autoHideTimer.IsEnabled) ResetAutoHide(_longDelay);
                return;
            }

            if (_isVisible)
            {
                var sb = (Storyboard)this.Resources["AnimHide"];
                sb.Begin();
                _isVisible = false;

                if (_isExpanded)
                {
                    var sbCollapse = (Storyboard)this.Resources["AnimCollapse"];
                    sbCollapse.Begin();
                    _isExpanded = false;
                }
            }
            _autoHideTimer.Stop();
        }

        private void ResetAutoHide(TimeSpan? delay = null)
        {
            if (_isPinned) return;
            _autoHideTimer.Stop();
            if (delay.HasValue) _autoHideTimer.Interval = delay.Value;
            _autoHideTimer.Start();
        }

        // ================== Alt+Tab ==================
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        private void HideFromAltTab()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            _autoHideTimer.Stop();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (!_isPinned)
            {
                
                ResetAutoHide(_isExpanded ? _longDelay : _shortDelay);
            }
            base.OnMouseLeave(e);
        }
    }
}