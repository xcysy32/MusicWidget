using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MusicWidget
{
    public class SystemMediaHelper
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        private readonly Timer _titlePollTimer;
        private readonly HttpClient _httpClient = new HttpClient();

        private string _lastSearchQuery = "";
        private bool _isUsingSmtc = false;
        private Process? _netEaseProcess = null;

        public event Action<string, string, byte[]?>? OnMediaChanged;
        public event Action<bool>? OnPlaybackStateChanged;

        public SystemMediaHelper()
        {
            _titlePollTimer = new Timer(1500);
            _titlePollTimer.Elapsed += TitlePollTimer_Elapsed;
            _ = Task.Run(MonitorNetEaseProcessLoop);
        }

        public async Task StartAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (s, e) => UpdateSmtcSession(s.GetCurrentSession());
                UpdateSmtcSession(_manager.GetCurrentSession());
            }
            catch { }
        }

        // ================= 核心修改：导出 OBS 数据 =================

        private void ExportToObs(string title, string artist, byte[]? coverData)
        {
            try
            {
                // 1. 确定输出目录: 程序运行目录/obs/
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string obsDir = Path.Combine(baseDir, "obs");
                if (!Directory.Exists(obsDir)) Directory.CreateDirectory(obsDir);

                string jsonPath = Path.Combine(obsDir, "nowplaying.json");
                string coverPath = Path.Combine(obsDir, "cover.png"); // 相对路径文件名

                // 2. 保存封面图片
                if (coverData != null && coverData.Length > 0)
                {
                    File.WriteAllBytes(coverPath, coverData);
                }
                else
                {
                    // 如果没封面，可以删掉或者留空，这里我们写入空文件或者默认图
                    // 为了简化，如果不删可能会显示上一首的图，所以建议删掉或覆盖
                    if (File.Exists(coverPath)) File.Delete(coverPath);
                }

                // 3. 构建 JSON 数据
                var data = new
                {
                    title = title,
                    artist = artist,
                    // 加个随机数防止 OBS 浏览器缓存图片
                    cover = (coverData != null && coverData.Length > 0) ? $"cover.png?t={DateTime.Now.Ticks}" : "",
                    isPlaying = !string.IsNullOrEmpty(title)
                };

                // 4. 写入 JSON
                string jsonString = JsonSerializer.Serialize(data);
                File.WriteAllText(jsonPath, jsonString);
            }
            catch
            {
                // 文件被占用时忽略，下秒再试
            }
        }

        // 统一的数据发布方法
        private void NotifyMediaChange(string title, string artist, byte[]? coverData)
        {
            // 1. 通知本地 UI
            OnMediaChanged?.Invoke(title, artist, coverData);

            // 2. 导出给 OBS
            ExportToObs(title, artist, coverData);
        }

        // ================= 控制功能 =================

        public async Task TogglePlayPause()
        {
            if (_isUsingSmtc && _currentSession != null) await _currentSession.TryTogglePlayPauseAsync();
            else SendKey(VK_MEDIA_PLAY_PAUSE);
        }

        public async Task Previous()
        {
            if (_isUsingSmtc && _currentSession != null) await _currentSession.TrySkipPreviousAsync();
            else SendKey(VK_MEDIA_PREV_TRACK);
        }

        public async Task Next()
        {
            if (_isUsingSmtc && _currentSession != null) await _currentSession.TrySkipNextAsync();
            else SendKey(VK_MEDIA_NEXT_TRACK);
        }

        public (TimeSpan Current, TimeSpan Total) GetTimeline()
        {
            if (_currentSession != null)
            {
                try
                {
                    var timeline = _currentSession.GetTimelineProperties();
                    var playbackInfo = _currentSession.GetPlaybackInfo();
                    TimeSpan position = timeline.Position;
                    TimeSpan end = timeline.EndTime;

                    if (playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        TimeSpan elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
                        position += elapsed;
                    }
                    if (position > end) position = end;
                    if (position < TimeSpan.Zero) position = TimeSpan.Zero;
                    return (position, end);
                }
                catch { }
            }
            return (TimeSpan.Zero, TimeSpan.Zero);
        }

        // 模拟按键
        private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        private const byte VK_MEDIA_PREV_TRACK = 0xB1;
        private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const int KEYEVENTF_KEYUP = 0x0002;
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);
        private void SendKey(byte key)
        {
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY, 0);
            keybd_event(key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
        }

        // ================= A. SMTC 逻辑 =================
        private void UpdateSmtcSession(GlobalSystemMediaTransportControlsSession? session)
        {
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            }
            _currentSession = session;
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
                CheckSmtcUpdate(_currentSession);
            }
            else
            {
                _isUsingSmtc = false;
                CheckFallbackState();
            }
        }

        private void Session_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            CheckSmtcUpdate(sender);
            var info = sender.GetPlaybackInfo();
            bool isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            OnPlaybackStateChanged?.Invoke(isPlaying);
        }

        private void Session_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => CheckSmtcUpdate(sender);

        private void CheckSmtcUpdate(GlobalSystemMediaTransportControlsSession session)
        {
            var playbackInfo = session.GetPlaybackInfo();
            var status = playbackInfo.PlaybackStatus;

            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                _isUsingSmtc = true;
                _titlePollTimer.Stop();
                _ = TryGetSmtcPropertiesAsync(session);
            }
            else
            {
                _isUsingSmtc = false;
                CheckFallbackState();
            }
        }

        private async Task TryGetSmtcPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var info = await session.TryGetMediaPropertiesAsync();
                if (info == null) return;

                byte[]? thumb = null;
                if (info.Thumbnail != null)
                {
                    using var stream = await info.Thumbnail.OpenReadAsync();
                    if (stream != null)
                    {
                        using var ms = new MemoryStream();
                        await stream.AsStreamForRead().CopyToAsync(ms);
                        thumb = ms.ToArray();
                    }
                }
                // 使用统一通知方法
                NotifyMediaChange(info.Title, info.Artist, thumb);
                _lastSearchQuery = info.Title + info.Artist;
            }
            catch { }
        }

        // ================= B. 进程监控 (网易云) =================

        private async Task MonitorNetEaseProcessLoop()
        {
            while (true)
            {
                if (_netEaseProcess == null)
                {
                    var procs = Process.GetProcessesByName("cloudmusic");
                    if (procs.Length > 0)
                    {
                        _netEaseProcess = procs[0];
                        _netEaseProcess.EnableRaisingEvents = true;
                        _netEaseProcess.Exited += (s, e) =>
                        {
                            _netEaseProcess = null;
                            _titlePollTimer.Stop();
                            CheckFallbackState();
                        };

                        if (!_isUsingSmtc) _titlePollTimer.Start();
                    }
                }
                await Task.Delay(3000);
            }
        }

        private void TitlePollTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (_isUsingSmtc || _netEaseProcess == null) return;
            string title = GetNetEaseWindowTitle(_netEaseProcess.Id);
            ProcessNetEaseTitle(title);
        }

        private string GetNetEaseWindowTitle(int pid)
        {
            string foundTitle = "";
            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowPid);
                if (windowPid == pid)
                {
                    int length = GetWindowTextLength(hWnd);
                    if (length > 0)
                    {
                        StringBuilder sb = new StringBuilder(length + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        string t = sb.ToString();
                        if (t.Contains(" - ") && !t.Contains("网易云音乐") && !t.Contains("桌面歌词") && !t.Contains("迷你模式"))
                        {
                            foundTitle = t;
                            return false;
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return foundTitle;
        }

        private async void ProcessNetEaseTitle(string windowTitle)
        {
            if (string.IsNullOrWhiteSpace(windowTitle)) return;
            string title = windowTitle;
            string artist = "未知歌手";
            int splitIndex = windowTitle.LastIndexOf('-');
            if (splitIndex > 0)
            {
                title = windowTitle.Substring(0, splitIndex).Trim();
                artist = windowTitle.Substring(splitIndex + 1).Trim();
            }
            else return;

            string currentQuery = title + artist;
            if (currentQuery == _lastSearchQuery) return;

            _lastSearchQuery = currentQuery;
            byte[]? cover = await FetchCoverFromITunes(title, artist);

            // 使用统一通知方法
            NotifyMediaChange(title, artist, cover);
        }

        // ================= C. 状态决策 =================
        private void CheckFallbackState()
        {
            if (!_isUsingSmtc)
            {
                if (_netEaseProcess == null || _netEaseProcess.HasExited)
                {
                    ClearUI();
                    _titlePollTimer.Stop();
                }
                else
                {
                    _titlePollTimer.Start();
                    string t = GetNetEaseWindowTitle(_netEaseProcess.Id);
                    ProcessNetEaseTitle(t);
                }
            }
        }

        private void ClearUI()
        {
            _lastSearchQuery = "";
            // 使用统一通知方法 (清空)
            NotifyMediaChange("", "", null);
        }

        private async Task<byte[]?> FetchCoverFromITunes(string title, string artist)
        {
            try
            {
                await Task.Delay(200);
                string term = $"{title} {artist}";
                string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity=song&limit=1";
                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("resultCount", out var count) && count.GetInt32() > 0)
                {
                    var res = root.GetProperty("results")[0];
                    if (res.TryGetProperty("artworkUrl100", out var art))
                    {
                        string artUrl = art.GetString()?.Replace("100x100", "600x600") ?? "";
                        if (!string.IsNullOrEmpty(artUrl)) return await _httpClient.GetByteArrayAsync(artUrl);
                    }
                }
            }
            catch { }
            return null;
        }

        // Win32 API
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}