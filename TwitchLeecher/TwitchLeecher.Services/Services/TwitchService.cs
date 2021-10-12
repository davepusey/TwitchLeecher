using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TwitchLeecher.Core.Enums;
using TwitchLeecher.Core.Events;
using TwitchLeecher.Core.Models;
using TwitchLeecher.Services.Interfaces;
using TwitchLeecher.Shared.Events;
using TwitchLeecher.Shared.Extensions;
using TwitchLeecher.Shared.IO;
using TwitchLeecher.Shared.Notification;
using TwitchLeecher.Shared.Reflection;

namespace TwitchLeecher.Services.Services
{
    internal class TwitchService : BindableBase, ITwitchService, IDisposable
    {
        #region Constants

        private const string VIDEO_URL = "https://api.twitch.tv/kraken/videos/{0}";
        private const string GAMES_URL = "https://api.twitch.tv/kraken/games/top";
        private const string USERS_URL = "https://api.twitch.tv/kraken/users";
        private const string CHANNEL_URL = "https://api.twitch.tv/kraken/channels/{0}";
        private const string CHANNEL_VIDEOS_URL = "https://api.twitch.tv/kraken/channels/{0}/videos";
        private const string ACCESS_TOKEN_URL = "https://gql.twitch.tv/gql";
        private const string ALL_PLAYLISTS_URL = "https://usher.ttvnw.net/vod/{0}.m3u8?nauthsig={1}&nauth={2}&allow_source=true&player=twitchweb&allow_spectre=true&allow_audio_only=true";
        private const string UNKNOWN_GAME_URL = "https://static-cdn.jtvnw.net/ttv-boxart/404_boxart.png";

        private const string TEMP_PREFIX = "TL_";

        private const int TIMER_INTERVALL = 2;
        private const int DOWNLOAD_RETRIES = 3;
        private const int DOWNLOAD_RETRY_TIME = 20;

        private const int TWITCH_MAX_LOAD_LIMIT = 100;

        private const string TWITCH_CLIENT_ID_HEADER = "Client-ID";
        private const string TWITCH_CLIENT_ID = "37v97169hnj8kaoq8fs3hzz8v6jezdj";
        private const string TWITCH_CLIENT_ID_WEB = "kimne78kx3ncx6brgo4mv6wki5h1ko";
        private const string TWITCH_V5_ACCEPT_HEADER = "Accept";
        private const string TWITCH_V5_ACCEPT = "application/vnd.twitchtv.v5+json";

        #endregion Constants

        #region Fields

        private bool disposedValue = false;

        private readonly IPreferencesService _preferencesService;
        private readonly IProcessingService _processingService;
        private readonly IEventAggregator _eventAggregator;

        private readonly Timer _downloadTimer;

        private ObservableCollection<TwitchVideo> _videos;
        private ObservableCollection<TwitchVideoDownload> _downloads;

        private ConcurrentDictionary<string, DownloadTask> _downloadTasks;
        private Dictionary<string, Uri> _gameThumbnails;

        private readonly object _changeDownloadLockObject;

        private volatile bool _paused;

        #endregion Fields

        #region Constructors

        public TwitchService(
            IPreferencesService preferencesService,
            IProcessingService processingService,
            IEventAggregator eventAggregator)
        {
            _preferencesService = preferencesService;
            _processingService = processingService;
            _eventAggregator = eventAggregator;

            _videos = new ObservableCollection<TwitchVideo>();
            _videos.CollectionChanged += Videos_CollectionChanged;

            _downloads = new ObservableCollection<TwitchVideoDownload>();
            _downloads.CollectionChanged += Downloads_CollectionChanged;

            _downloadTasks = new ConcurrentDictionary<string, DownloadTask>();

            _changeDownloadLockObject = new object();

            _downloadTimer = new Timer(DownloadTimerCallback, null, 0, TIMER_INTERVALL);

            _eventAggregator.GetEvent<RemoveDownloadEvent>().Subscribe(Remove, ThreadOption.UIThread);
        }

        #endregion Constructors

        #region Properties

        public ObservableCollection<TwitchVideo> Videos
        {
            get
            {
                return _videos;
            }
            private set
            {
                if (_videos != null)
                {
                    _videos.CollectionChanged -= Videos_CollectionChanged;
                }

                SetProperty(ref _videos, value, nameof(Videos));

                if (_videos != null)
                {
                    _videos.CollectionChanged += Videos_CollectionChanged;
                }

                FireVideosCountChanged();
            }
        }

        public ObservableCollection<TwitchVideoDownload> Downloads
        {
            get
            {
                return _downloads;
            }
            private set
            {
                if (_downloads != null)
                {
                    _downloads.CollectionChanged -= Downloads_CollectionChanged;
                }

                SetProperty(ref _downloads, value, nameof(Downloads));

                if (_downloads != null)
                {
                    _downloads.CollectionChanged += Downloads_CollectionChanged;
                }

                FireDownloadsCountChanged();
            }
        }

        #endregion Properties

        #region Methods

        private WebClient CreatePublicApiWebClient()
        {
            WebClient wc = new WebClient();
            wc.Headers.Add(TWITCH_CLIENT_ID_HEADER, TWITCH_CLIENT_ID);
            wc.Headers.Add(TWITCH_V5_ACCEPT_HEADER, TWITCH_V5_ACCEPT);
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        private WebClient CreatePrivateApiWebClient()
        {
            WebClient wc = new WebClient();
            wc.Headers.Add(TWITCH_CLIENT_ID_HEADER, TWITCH_CLIENT_ID_WEB);
            wc.Encoding = Encoding.UTF8;

            return wc;
        }

        public VodAuthInfo RetrieveVodAuthInfo(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            using (WebClient webClient = CreatePrivateApiWebClient())
            {
                string accessTokenStr = webClient.UploadString(ACCESS_TOKEN_URL, CreateGqlPlaybackAccessToken(id));

                JObject accessTokenJson = JObject.Parse(accessTokenStr);

                JToken vpaToken = accessTokenJson.SelectToken("$.data.videoPlaybackAccessToken", false);

                string token = Uri.EscapeDataString(vpaToken.Value<string>("value"));
                string signature = vpaToken.Value<string>("signature");

                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ApplicationException("VOD access token is null!");
                }

                if (string.IsNullOrWhiteSpace(signature))
                {
                    throw new ApplicationException("VOD signature is null!");
                }

                bool privileged = false;
                bool subOnly = false;

                JObject tokenJson = JObject.Parse(HttpUtility.UrlDecode(token));

                if (tokenJson == null)
                {
                    throw new ApplicationException("Decoded VOD access token is null!");
                }

                privileged = tokenJson.Value<bool>("privileged");

                if (privileged)
                {
                    subOnly = true;
                }
                else
                {
                    JObject chansubJson = tokenJson.Value<JObject>("chansub");

                    if (chansubJson == null)
                    {
                        throw new ApplicationException("Token property 'chansub' is null!");
                    }

                    JArray restrictedQualitiesJson = chansubJson.Value<JArray>("restricted_bitrates");

                    if (restrictedQualitiesJson == null)
                    {
                        throw new ApplicationException("Token property 'chansub -> restricted_bitrates' is null!");
                    }

                    if (restrictedQualitiesJson.Count > 0)
                    {
                        subOnly = true;
                    }
                }

                return new VodAuthInfo(token, signature, privileged, subOnly);
            }
        }

        private string CreateGqlPlaybackAccessToken(string id)
        {
            // {
            //   "operationName": "PlaybackAccessToken",
            //   "variables": {
            //       "isLive": false,
            //       "login": "",
            //       "isVod": true,
            //       "vodID": "870835569",
            //       "playerType": "channel_home_live"
            //   },
            //   "extensions": {
            //     "persistedQuery": {
            //       "version": 1,
            //       "sha256Hash": "0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712"
            //     }
            //   }
            // }

            return "{\"operationName\": \"PlaybackAccessToken\",\"variables\": {\"isLive\": false,\"login\": \"\",\"isVod\": true,\"vodID\": \"" + id + "\",\"playerType\": \"channel_home_live\"},\"extensions\": {\"persistedQuery\": {\"version\": 1,\"sha256Hash\": \"0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712\"}}}";
        }

        public bool ChannelExists(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            return GetChannelIdByName(channel) != null;
        }

        public string GetChannelIdByName(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            return TwitchGQL.RunQuery("channel (name: \"" + channel + "\") {id}").SelectToken("channel.id").Value<string>();
        }

        public void Search(SearchParameters searchParams)
        {
            if (searchParams == null)
            {
                throw new ArgumentNullException(nameof(searchParams));
            }

            switch (searchParams.SearchType)
            {
                case SearchType.Channel:
                    SearchChannel(searchParams.Channel, searchParams.VideoType, searchParams.LoadLimitType, searchParams.LoadFrom.Value, searchParams.LoadTo.Value, searchParams.LoadLastVods);
                    break;

                case SearchType.Urls:
                    SearchUrls(searchParams.Urls);
                    break;

                case SearchType.Ids:
                    SearchIds(searchParams.Ids);
                    break;
            }
        }

        private void SearchChannel(string channel, VideoType videoType, LoadLimitType loadLimit, DateTime loadFrom, DateTime loadTo, int loadLastVods)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            ObservableCollection<TwitchVideo> videos = new ObservableCollection<TwitchVideo>();

            string broadcastTypeParam;

            if (videoType == VideoType.Broadcast)
            {
                broadcastTypeParam = "archive";
            }
            else if (videoType == VideoType.Highlight)
            {
                broadcastTypeParam = "highlight";
            }
            else if (videoType == VideoType.Upload)
            {
                broadcastTypeParam = "upload";
            }
            else
            {
                throw new ApplicationException("Unsupported video type '" + videoType.ToString() + "'");
            }

            DateTime fromDate = DateTime.Now;
            DateTime toDate = DateTime.Now;

            if (loadLimit == LoadLimitType.Timespan)
            {
                fromDate = loadFrom;
                toDate = loadTo;
            }

            JObject variables = new JObject
            {
                { "channelOwnerLogin", channel },
                { "broadcastType", broadcastTypeParam.ToUpper() },
                { "videoSort", "TIME" }
            };

            List<JObject> videosJson = TwitchGQL.RunPaginatedPersistedQuery("FilterableVideoTower_Videos", variables, "a937f1d22e269e39a03b509f65a7490f9fc247d7f83d6ac1421523e3b68042cb");

            foreach (JObject videoJson in videosJson)
            {
                TwitchVideo video = GetTwitchVideoFromId(videoJson.Value<int>("id"));

                if (loadLimit == LoadLimitType.LastVods)
                {
                    videos.Add(video);

                    if (videos.Count >= loadLastVods)
                    {
                        break;
                    }
                }
                else
                {
                    DateTime recordedDate = video.RecordedDate;

                    if (recordedDate.Date >= fromDate.Date && recordedDate.Date <= toDate.Date)
                    {
                        videos.Add(video);
                    }

                    if (recordedDate.Date < fromDate.Date)
                    {
                        break;
                    }
                }
            }

            Videos = videos;
        }

        private void SearchUrls(string urls)
        {
            if (string.IsNullOrWhiteSpace(urls))
            {
                throw new ArgumentNullException(nameof(urls));
            }

            ObservableCollection<TwitchVideo> videos = new ObservableCollection<TwitchVideo>();

            string[] urlArr = urls.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            if (urlArr.Length > 0)
            {
                HashSet<int> addedIds = new HashSet<int>();

                foreach (string url in urlArr)
                {
                    int? id = GetVideoIdFromUrl(url);

                    if (id.HasValue && !addedIds.Contains(id.Value))
                    {
                        TwitchVideo video = GetTwitchVideoFromId(id.Value);

                        if (video != null)
                        {
                            videos.Add(video);
                            addedIds.Add(id.Value);
                        }
                    }
                }
            }

            Videos = videos;
        }

        private void SearchIds(string ids)
        {
            if (string.IsNullOrWhiteSpace(ids))
            {
                throw new ArgumentNullException(nameof(ids));
            }

            ObservableCollection<TwitchVideo> videos = new ObservableCollection<TwitchVideo>();

            string[] idsArr = ids.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

            if (idsArr.Length > 0)
            {
                HashSet<int> addedIds = new HashSet<int>();

                foreach (string id in idsArr)
                {
                    if (int.TryParse(id, out int idInt) && !addedIds.Contains(idInt))
                    {
                        TwitchVideo video = GetTwitchVideoFromId(idInt);

                        if (video != null)
                        {
                            videos.Add(video);
                            addedIds.Add(idInt);
                        }
                    }
                }
            }

            Videos = videos;
        }

        private int? GetVideoIdFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri validUrl))
            {
                return null;
            }

            string[] segments = validUrl.Segments;

            if (segments.Length < 2)
            {
                return null;
            }

            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals("video/", StringComparison.OrdinalIgnoreCase) || segments[i].Equals("videos/", StringComparison.OrdinalIgnoreCase))
                {
                    if (segments.Length > (i + 1))
                    {
                        string idStr = segments[i + 1];

                        if (!string.IsNullOrWhiteSpace(idStr))
                        {
                            idStr = idStr.Trim(new char[] { '/' });

                            if (int.TryParse(idStr, out int idInt) && idInt > 0)
                            {
                                return idInt;
                            }
                        }
                    }

                    break;
                }
            }

            return null;
        }

        private TwitchVideo GetTwitchVideoFromId(int id)
        {
            string query = "video(id:" + id + ") {" +
                            "owner{displayName}, title, id, " +
                            "game{name,boxArtURL(width:136,height:190)}, " +
                            "viewCount, lengthSeconds, " +
                            "thumbnailURLs(width:640,height:360), " +
                            "publishedAt, createdAt, animatedPreviewURL, broadcastType}";

            JObject videoJson = TwitchGQL.RunQuery(query);

            if (videoJson != null)
            {
                return ParseVideo(videoJson);
            }
            else
            {
                return null;
            }
        }

        private bool IsVideoSubOnly(int id)
        {
            JObject variables = new JObject
            {
                { "isLive", false },
                { "login", "" },
                { "isVod", true },
                { "vodID", id.ToString() },
                { "playerType", "" }
            };

            JObject patJson = TwitchGQL.RunPersistedQuery("PlaybackAccessToken", variables, "0828119ded1c13477966434e15800ff57ddacf13ba1911c129dc2200705b0712");
            JObject tokenJson = JObject.Parse(patJson.SelectToken("data.videoPlaybackAccessToken.value").Value<string>());
            JArray bitratesJson = tokenJson.SelectToken("chansub.restricted_bitrates").Value<JArray>();

            return bitratesJson.Count > 0;
        }

        public void Enqueue(DownloadParameters downloadParams)
        {
            if (_paused)
            {
                return;
            }

            lock (_changeDownloadLockObject)
            {
                _downloads.Add(new TwitchVideoDownload(downloadParams));
            }
        }

        private void DownloadTimerCallback(object state)
        {
            if (_paused)
            {
                return;
            }

            StartQueuedDownloadIfExists();
        }

        private void StartQueuedDownloadIfExists()
        {
            if (_paused)
            {
                return;
            }

            if (Monitor.TryEnter(_changeDownloadLockObject))
            {
                try
                {
                    if (!_downloads.Where(d => d.DownloadState == DownloadState.Downloading).Any())
                    {
                        TwitchVideoDownload download = _downloads.Where(d => d.DownloadState == DownloadState.Queued).FirstOrDefault();

                        if (download == null)
                        {
                            return;
                        }

                        DownloadParameters downloadParams = download.DownloadParams;

                        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                        CancellationToken cancellationToken = cancellationTokenSource.Token;

                        string downloadId = download.Id;
                        string vodId = downloadParams.Video.Id;
                        string tempDir = Path.Combine(_preferencesService.CurrentPreferences.DownloadTempFolder, TEMP_PREFIX + downloadId);
                        string ffmpegFile = _processingService.FFMPEGExe;
                        string concatFile = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(downloadParams.FullPath) + ".ts");
                        string outputFile = downloadParams.FullPath;

                        bool disableConversion = downloadParams.DisableConversion;
                        bool cropStart = downloadParams.CropStart;
                        bool cropEnd = downloadParams.CropEnd;

                        TimeSpan cropStartTime = downloadParams.CropStartTime;
                        TimeSpan cropEndTime = downloadParams.CropEndTime;

                        TwitchVideoQuality quality = downloadParams.Quality;

                        VodAuthInfo vodAuthInfo = downloadParams.VodAuthInfo;

                        Action<DownloadState> setDownloadState = download.SetDownloadState;
                        Action<string> log = download.AppendLog;
                        Action<string> setStatus = download.SetStatus;
                        Action<double> setProgress = download.SetProgress;
                        Action<bool> setIsIndeterminate = download.SetIsIndeterminate;

                        Task downloadVideoTask = new Task(() =>
                        {
                            setStatus("Initializing");

                            log("Download task has been started!");

                            WriteDownloadInfo(log, downloadParams, ffmpegFile, tempDir);

                            CheckTempDirectory(log, tempDir);

                            cancellationToken.ThrowIfCancellationRequested();

                            string playlistUrl = RetrievePlaylistUrlForQuality(log, quality, downloadParams.Video);

                            cancellationToken.ThrowIfCancellationRequested();

                            VodPlaylist vodPlaylist = RetrieveVodPlaylist(log, tempDir, playlistUrl);

                            cancellationToken.ThrowIfCancellationRequested();

                            CropInfo cropInfo = CropVodPlaylist(vodPlaylist, cropStart, cropEnd, cropStartTime, cropEndTime);

                            cancellationToken.ThrowIfCancellationRequested();

                            DownloadParts(log, setStatus, setProgress, vodPlaylist, cancellationToken);

                            cancellationToken.ThrowIfCancellationRequested();

                            _processingService.ConcatParts(log, setStatus, setProgress, vodPlaylist, disableConversion ? outputFile : concatFile);

                            if (!disableConversion)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                _processingService.ConvertVideo(log, setStatus, setProgress, setIsIndeterminate, concatFile, outputFile, cropInfo);
                            }
                        }, cancellationToken);

                        Task continueTask = downloadVideoTask.ContinueWith(task =>
                        {
                            log(Environment.NewLine + Environment.NewLine + "Starting temporary download folder cleanup!");
                            CleanUp(tempDir, log);

                            setProgress(100);
                            setIsIndeterminate(false);

                            bool success = false;

                            if (task.IsFaulted)
                            {
                                setDownloadState(DownloadState.Error);
                                log(Environment.NewLine + Environment.NewLine + "Download task ended with an error!");

                                if (task.Exception != null)
                                {
                                    log(Environment.NewLine + Environment.NewLine + task.Exception.ToString());
                                }
                            }
                            else if (task.IsCanceled)
                            {
                                setDownloadState(DownloadState.Canceled);
                                log(Environment.NewLine + Environment.NewLine + "Download task was canceled!");
                            }
                            else
                            {
                                success = true;
                                setDownloadState(DownloadState.Done);
                                log(Environment.NewLine + Environment.NewLine + "Download task ended successfully!");
                            }

                            if (!_downloadTasks.TryRemove(downloadId, out DownloadTask downloadTask))
                            {
                                throw new ApplicationException("Could not remove download task with ID '" + downloadId + "' from download task collection!");
                            }

                            if (success && _preferencesService.CurrentPreferences.DownloadRemoveCompleted)
                            {
                                _eventAggregator.GetEvent<RemoveDownloadEvent>().Publish(downloadId);
                            }
                        });

                        if (_downloadTasks.TryAdd(downloadId, new DownloadTask(downloadVideoTask, continueTask, cancellationTokenSource)))
                        {
                            downloadVideoTask.Start();
                            setDownloadState(DownloadState.Downloading);
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_changeDownloadLockObject);
                }
            }
        }

        private void WriteDownloadInfo(Action<string> log, DownloadParameters downloadParams, string ffmpegFile, string tempDir)
        {
            log(Environment.NewLine + Environment.NewLine + "TWITCH LEECHER INFO");
            log(Environment.NewLine + "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Version: " + AssemblyUtil.Get.GetAssemblyVersion().Trim());

            log(Environment.NewLine + Environment.NewLine + "VOD INFO");
            log(Environment.NewLine + "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "VOD ID: " + downloadParams.Video.Id);
            log(Environment.NewLine + "Selected Quality: " + downloadParams.Quality.DisplayString);
            log(Environment.NewLine + "Download Url: " + downloadParams.Video.Url);
            log(Environment.NewLine + "Crop Start: " + (downloadParams.CropStart ? "Yes (" + downloadParams.CropStartTime.ToDaylessString() + ")" : "No"));
            log(Environment.NewLine + "Crop End: " + (downloadParams.CropEnd ? "Yes (" + downloadParams.CropEndTime.ToDaylessString() + ")" : "No"));

            log(Environment.NewLine + Environment.NewLine + "OUTPUT INFO");
            log(Environment.NewLine + "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Disable Conversion: " + (downloadParams.DisableConversion ? "Yes" : "No"));
            log(Environment.NewLine + "Output File: " + downloadParams.FullPath);
            log(Environment.NewLine + "FFMPEG Path: " + ffmpegFile);
            log(Environment.NewLine + "Temporary Download Folder: " + tempDir);

            VodAuthInfo vodAuthInfo = downloadParams.VodAuthInfo;

            log(Environment.NewLine + Environment.NewLine + "ACCESS INFO");
            log(Environment.NewLine + "--------------------------------------------------------------------------------------------");
            log(Environment.NewLine + "Token: " + vodAuthInfo.Token);
            log(Environment.NewLine + "Signature: " + vodAuthInfo.Signature);
            log(Environment.NewLine + "Sub-Only: " + (vodAuthInfo.SubOnly ? "Yes" : "No"));
            log(Environment.NewLine + "Privileged: " + (vodAuthInfo.Privileged ? "Yes" : "No"));
        }

        private void CheckTempDirectory(Action<string> log, string tempDir)
        {
            if (!Directory.Exists(tempDir))
            {
                log(Environment.NewLine + Environment.NewLine + "Creating temporary download directory '" + tempDir + "'...");
                FileSystem.CreateDirectory(tempDir);
                log(" done!");
            }

            if (Directory.EnumerateFileSystemEntries(tempDir).Any())
            {
                throw new ApplicationException("Temporary download directory '" + tempDir + "' is not empty!");
            }
        }

        private string RetrievePlaylistUrlForQuality(Action<string> log, TwitchVideoQuality quality, TwitchVideo video)
        {
            log(Environment.NewLine + Environment.NewLine + "Retrieving m3u8 playlist url for selected quality " + quality.DisplayString);

            string playlistUrl = video.GetPlaylistUrlForQuality(quality);

            log(Environment.NewLine + Environment.NewLine + "Playlist url for selected quality " + quality.DisplayString + " is " + playlistUrl);

            return playlistUrl;
        }

        private VodPlaylist RetrieveVodPlaylist(Action<string> log, string tempDir, string playlistUrl)
        {
            using (WebClient webClient = new WebClient())
            {
                log(Environment.NewLine + Environment.NewLine + "Retrieving playlist...");
                string playlistStr = webClient.DownloadString(playlistUrl);
                log(" done!");

                if (string.IsNullOrWhiteSpace(playlistStr))
                {
                    throw new ApplicationException("The playlist is empty!");
                }

                string urlPrefix = playlistUrl.Substring(0, playlistUrl.LastIndexOf("/") + 1);

                log(Environment.NewLine + "Parsing playlist...");
                VodPlaylist vodPlaylist = VodPlaylist.Parse(tempDir, playlistStr, urlPrefix);
                log(" done!");

                log(Environment.NewLine + "Number of video chunks: " + vodPlaylist.Count());

                return vodPlaylist;
            }
        }

        private CropInfo CropVodPlaylist(VodPlaylist vodPlaylist, bool cropStart, bool cropEnd, TimeSpan cropStartTime, TimeSpan cropEndTime)
        {
            double start = cropStartTime.TotalMilliseconds;
            double end = cropEndTime.TotalMilliseconds;
            double length = cropEndTime.TotalMilliseconds;

            if (cropStart)
            {
                length -= start;
            }

            start = Math.Round(start / 1000, 3);
            end = Math.Round(end / 1000, 3);
            length = Math.Round(length / 1000, 3);

            List<VodPlaylistPart> deleteStart = new List<VodPlaylistPart>();
            List<VodPlaylistPart> deleteEnd = new List<VodPlaylistPart>();

            if (cropStart)
            {
                double lengthSum = 0;

                foreach (VodPlaylistPart part in vodPlaylist)
                {
                    double partLength = part.Length;

                    if (lengthSum + partLength < start)
                    {
                        lengthSum += partLength;
                        deleteStart.Add(part);
                    }
                    else
                    {
                        start = Math.Round(start - lengthSum, 3);
                        break;
                    }
                }
            }

            if (cropEnd)
            {
                double lengthSum = 0;

                foreach (VodPlaylistPart part in vodPlaylist)
                {
                    if (lengthSum >= end)
                    {
                        deleteEnd.Add(part);
                    }

                    lengthSum += part.Length;
                }
            }

            deleteStart.ForEach(part =>
            {
                vodPlaylist.Remove(part);
            });

            deleteEnd.ForEach(part =>
            {
                vodPlaylist.Remove(part);
            });

            return new CropInfo(cropStart, cropEnd, cropStart ? start : 0, length);
        }

        private void DownloadParts(Action<string> log, Action<string> setStatus, Action<double> setProgress,
            VodPlaylist vodPlaylist, CancellationToken cancellationToken)
        {
            int partsCount = vodPlaylist.Count;
            int maxConnectionCount = ServicePointManager.DefaultConnectionLimit;

            log(Environment.NewLine + Environment.NewLine + "Starting parallel video chunk download");
            log(Environment.NewLine + "Number of video chunks to download: " + partsCount);
            log(Environment.NewLine + "Maximum connection count: " + maxConnectionCount);

            setStatus("Downloading");

            log(Environment.NewLine + Environment.NewLine + "Parallel video chunk download is running...");

            long completedPartDownloads = 0;

            Parallel.ForEach(vodPlaylist, new ParallelOptions() { MaxDegreeOfParallelism = maxConnectionCount - 1 }, (part, loopState) =>
            {
                int retryCounter = 0;

                bool success = false;

                do
                {
                    try
                    {
                        using (WebClient downloadClient = new WebClient())
                        {
                            byte[] bytes = downloadClient.DownloadData(part.RemoteFile);

                            Interlocked.Increment(ref completedPartDownloads);

                            FileSystem.DeleteFile(part.LocalFile);

                            File.WriteAllBytes(part.LocalFile, bytes);

                            long completed = Interlocked.Read(ref completedPartDownloads);

                            setProgress((double)completed / partsCount * 100);

                            success = true;
                        }
                    }
                    catch (WebException ex)
                    {
                        if (retryCounter < DOWNLOAD_RETRIES)
                        {
                            retryCounter++;
                            log(Environment.NewLine + Environment.NewLine + "Downloading file '" + part.RemoteFile + "' failed! Trying again in " + DOWNLOAD_RETRY_TIME + "s");
                            log(Environment.NewLine + ex.ToString());
                            Thread.Sleep(DOWNLOAD_RETRY_TIME * 1000);
                        }
                        else
                        {
                            throw new ApplicationException("Could not download file '" + part.RemoteFile + "' after " + DOWNLOAD_RETRIES + " retries!");
                        }
                    }
                }
                while (!success);

                if (cancellationToken.IsCancellationRequested)
                {
                    loopState.Stop();
                }
            });

            setProgress(100);

            log(Environment.NewLine + Environment.NewLine + "Download of all video chunks complete!");
        }

        private void CleanUp(string directory, Action<string> log)
        {
            try
            {
                log(Environment.NewLine + "Deleting directory '" + directory + "'...");
                FileSystem.DeleteDirectory(directory);
                log(" done!");
            }
            catch
            {
            }
        }

        public void Cancel(string id)
        {
            lock (_changeDownloadLockObject)
            {
                if (_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    downloadTask.CancellationTokenSource.Cancel();
                }
            }
        }

        public void Retry(string id)
        {
            if (_paused)
            {
                return;
            }

            lock (_changeDownloadLockObject)
            {
                if (!_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    TwitchVideoDownload download = _downloads.Where(d => d.Id == id).FirstOrDefault();

                    if (download != null && (download.DownloadState == DownloadState.Canceled || download.DownloadState == DownloadState.Error))
                    {
                        download.ResetLog();
                        download.SetProgress(0);
                        download.SetDownloadState(DownloadState.Queued);
                        download.SetStatus("Initializing");
                    }
                }
            }
        }

        public void Remove(string id)
        {
            lock (_changeDownloadLockObject)
            {
                if (!_downloadTasks.TryGetValue(id, out DownloadTask downloadTask))
                {
                    TwitchVideoDownload download = _downloads.Where(d => d.Id == id).FirstOrDefault();

                    if (download != null)
                    {
                        _downloads.Remove(download);
                    }
                }
            }
        }

        public TwitchVideo ParseVideo(JObject videoJson)
        {
            string channel = videoJson.SelectToken("video.owner.displayName").Value<string>();
            string title = videoJson.SelectToken("video.title").Value<string>();
            int id = videoJson.SelectToken("video.id").Value<int>();
            string broadcastType = videoJson.SelectToken("video.broadcastType").Value<string>();
            string game = videoJson.SelectToken("video.game.name")?.Value<string>();
            int views = videoJson.SelectToken("video.viewCount").Value<int>();
            TimeSpan length = new TimeSpan(0, 0, videoJson.SelectToken("video.lengthSeconds").Value<int>());
            Uri url = new Uri("https://www.twitch.tv/videos/" + id);
            Uri thumbnail = new Uri(videoJson.SelectToken("video.thumbnailURLs[0]").Value<string>());
            Uri gameThumbnail = videoJson.SelectToken("video.game.boxArtURL") == null ? new Uri(UNKNOWN_GAME_URL) : new Uri(videoJson.SelectToken("video.game.boxArtURL").Value<string>());
            bool subOnly = IsVideoSubOnly(id);

            string playlistBase = videoJson.SelectToken("video.animatedPreviewURL").Value<string>();
            playlistBase = playlistBase.Substring(0, playlistBase.LastIndexOf("/storyboards/"));

            string dateStr = videoJson.SelectToken("video.publishedAt").Value<string>();

            if (string.IsNullOrWhiteSpace(dateStr))
            {
                dateStr = videoJson.SelectToken("video.createdAt").Value<string>();
            }

            DateTime recordedDate = DateTime.Parse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

            return new TwitchVideo(channel, title, id.ToString(), broadcastType, playlistBase, game, views, length, recordedDate, thumbnail, gameThumbnail, url, subOnly);
        }

        public Uri GetGameThumbnail(string game)
        {
            Uri unknownGameUri = new Uri(UNKNOWN_GAME_URL);

            if (string.IsNullOrWhiteSpace(game))
            {
                return unknownGameUri;
            }

            int hashIndex = game.IndexOf(" #");

            if (hashIndex >= 0)
            {
                game = game.Substring(0, game.Length - (game.Length - hashIndex));
            }

            string gameLower = game.ToLowerInvariant();

            if (_gameThumbnails == null)
            {
                InitGameThumbnails();
            }

            if (_gameThumbnails.TryGetValue(gameLower, out Uri thumb))
            {
                return thumb;
            }

            return unknownGameUri;
        }

        public void InitGameThumbnails()
        {
            _gameThumbnails = new Dictionary<string, Uri>();

            try
            {
                int offset = 0;
                int total = 0;

                do
                {
                    using (WebClient webClient = CreatePublicApiWebClient())
                    {
                        webClient.QueryString.Add("limit", TWITCH_MAX_LOAD_LIMIT.ToString());
                        webClient.QueryString.Add("offset", offset.ToString());

                        string result = webClient.DownloadString(GAMES_URL);

                        JObject gamesResponseJson = JObject.Parse(result);

                        if (total == 0)
                        {
                            total = gamesResponseJson.Value<int>("_total");
                        }

                        foreach (JObject gamesJson in gamesResponseJson.Value<JArray>("top"))
                        {
                            JObject gameJson = gamesJson.Value<JObject>("game");

                            string name = gameJson.Value<string>("name").ToLowerInvariant();
                            Uri gameThumb = new Uri(gameJson.Value<JObject>("box").Value<string>("medium"));

                            if (!_gameThumbnails.ContainsKey(name))
                            {
                                _gameThumbnails.Add(name, gameThumb);
                            }
                        }
                    }

                    offset += TWITCH_MAX_LOAD_LIMIT;
                } while (offset < total);
            }
            catch
            {
                // Thumbnail loading should not affect the rest of the application
            }
        }

        public void Pause()
        {
            _paused = true;
            _downloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        public void Resume()
        {
            _paused = false;
            _downloadTimer.Change(0, TIMER_INTERVALL);
        }

        public bool CanShutdown()
        {
            Monitor.Enter(_changeDownloadLockObject);

            try
            {
                return !_downloads.Where(d => d.DownloadState == DownloadState.Downloading || d.DownloadState == DownloadState.Queued).Any();
            }
            finally
            {
                Monitor.Exit(_changeDownloadLockObject);
            }
        }

        public void Shutdown()
        {
            Pause();

            foreach (DownloadTask downloadTask in _downloadTasks.Values)
            {
                downloadTask.CancellationTokenSource.Cancel();
            }

            List<Task> tasks = _downloadTasks.Values.Select(v => v.Task).ToList();
            tasks.AddRange(_downloadTasks.Values.Select(v => v.ContinueTask).ToList());

            try
            {
                Task.WaitAll(tasks.ToArray());
            }
            catch (Exception)
            {
                // Don't care about aborted tasks
            }

            List<string> toRemove = _downloads.Select(d => d.Id).ToList();

            foreach (string id in toRemove)
            {
                Remove(id);
            }
        }

        public bool IsFileNameUsed(string fullPath)
        {
            IEnumerable<TwitchVideoDownload> downloads = _downloads.Where(d => d.DownloadState == DownloadState.Downloading || d.DownloadState == DownloadState.Queued);

            foreach (TwitchVideoDownload download in downloads)
            {
                if (download.DownloadParams.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void FireVideosCountChanged()
        {
            _eventAggregator.GetEvent<VideosCountChangedEvent>().Publish(_videos != null ? _videos.Count : 0);
        }

        private void FireDownloadsCountChanged()
        {
            _eventAggregator.GetEvent<DownloadsCountChangedEvent>().Publish(_downloads != null ? _downloads.Count : 0);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _downloadTimer.Dispose();
                }

                _videos = null;
                _downloads = null;
                _downloadTasks = null;

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        #endregion Methods

        #region EventHandlers

        private void Videos_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            FireVideosCountChanged();
        }

        private void Downloads_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            FireDownloadsCountChanged();
        }

        #endregion EventHandlers
    }
}