using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows;
using TwitchLeecher.Shared.Extensions;

namespace TwitchLeecher.Core.Models
{
    public class TwitchVideo
    {
        #region Constants

        private const string UNTITLED_BROADCAST = "Untitled Broadcast";
        private const string UNKNOWN_GAME = "Unknown";

        #endregion Constants

        #region Constructors

        public TwitchVideo(string channel, string title, string id, string broadcastType, string playlistBase, string game, int views, TimeSpan length,
            DateTime recordedDate, Uri thumbnail, Uri gameThumbnail, Uri url, bool subOnly)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ArgumentNullException(nameof(channel));
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            if (string.IsNullOrWhiteSpace(broadcastType))
            {
                throw new ArgumentNullException(nameof(broadcastType));
            }

            if (string.IsNullOrWhiteSpace(playlistBase))
            {
                throw new ArgumentNullException(nameof(playlistBase));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = UNTITLED_BROADCAST;
            }

            Channel = channel;
            Title = title;
            Id = id;
            BroadcastType = broadcastType;
            PlaylistBase = playlistBase;

            if (string.IsNullOrWhiteSpace(game))
            {
                Game = UNKNOWN_GAME;
            }
            else
            {
                Game = game;
            }

            Views = views;
            Length = length;
            Qualities = DetermineAvailableQualities(playlistBase, broadcastType, id);
            RecordedDate = recordedDate;
            Thumbnail = thumbnail ?? throw new ArgumentNullException(nameof(thumbnail));
            GameThumbnail = gameThumbnail ?? throw new ArgumentNullException(nameof(gameThumbnail));
            Url = url ?? throw new ArgumentNullException(nameof(url));
            SubOnlyVis = subOnly ? Visibility.Visible : Visibility.Hidden;
            SubOnlyStr = subOnly ? "SUB ONLY" : "";
        }

        #endregion Constructors

        #region Properties

        public string Channel { get; }

        public string Title { get; }

        public string Id { get; }

        private string BroadcastType { get; }

        private string PlaylistBase { get; }

        public string Game { get; }

        public TimeSpan Length { get; }

        public string LengthStr
        {
            get
            {
                return Length.ToDaylessString();
            }
        }

        public int Views { get; }

        public List<TwitchVideoQuality> Qualities { get; }

        public string BestQuality
        {
            get
            {
                if (Qualities == null || Qualities.Count == 0)
                {
                    return TwitchVideoQuality.UNKNOWN;
                }

                return Qualities.First().ResFpsString;
            }
        }

        public DateTime RecordedDate { get; }

        public Uri Thumbnail { get; }

        public Uri GameThumbnail { get; }

        public Uri Url { get; }

        public Visibility SubOnlyVis { get; }

        public string SubOnlyStr { get; }

        #endregion Properties

        #region Methods

        private List<TwitchVideoQuality> DetermineAvailableQualities(string playlistBase, string broadcastType, string id)
        {
            List<TwitchVideoQuality> availableQualities = new List<TwitchVideoQuality>
            {
                new TwitchVideoQuality("chunked")
            };

            foreach(string possibleQualityId in new string[] { "1080p60", "1080p30", "720p60", "720p30", "480p30", "360p30", "160p30", "audio_only" })
            {
                string possiblePlaylistUrl = GetPlaylistUrlForQuality(possibleQualityId);

                using (WebClient wc = new WebClient())
                {
                    try
                    {
                        wc.DownloadString(possiblePlaylistUrl);
                        availableQualities.Add(new TwitchVideoQuality(possibleQualityId));
                    }
                    catch (WebException)
                    { }
                }
            }

            return availableQualities;
        }

        public string GetPlaylistUrlForQuality(TwitchVideoQuality quality)
        {
            return GetPlaylistUrlForQuality(quality.QualityId);
        }

        public string GetPlaylistUrlForQuality(string qualityId)
        {
            string playlistUrl = PlaylistBase + "/" + qualityId;

            if (BroadcastType == "HIGHLIGHT")
            {
                playlistUrl += "/highlight-" + Id + ".m3u8";
            }
            else
            {
                playlistUrl += "/index-dvr.m3u8";
            }

            return playlistUrl;
        }

        #endregion Methods

    }
}