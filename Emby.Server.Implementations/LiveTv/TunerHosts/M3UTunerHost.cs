#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Emby.Server.Implementations.LiveTv.TunerHosts
{
    public class M3UTunerHost : BaseTunerHost, ITunerHost, IConfigurableTunerHost
    {
        private static readonly string[] _disallowedSharedStreamExtensions =
        {
            ".mkv",
            ".mp4",
            ".m3u8",
            ".mpd"
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _appHost;
        private readonly INetworkManager _networkManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IStreamHelper _streamHelper;

        public M3UTunerHost(
            IServerConfigurationManager config,
            IMediaSourceManager mediaSourceManager,
            ILogger<M3UTunerHost> logger,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            IServerApplicationHost appHost,
            INetworkManager networkManager,
            IStreamHelper streamHelper,
            IMemoryCache memoryCache)
            : base(config, logger, fileSystem, memoryCache)
        {
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            _networkManager = networkManager;
            _mediaSourceManager = mediaSourceManager;
            _streamHelper = streamHelper;
        }

        public override string Type => "m3u";

        public virtual string Name => "M3U Tuner";

        private string GetFullChannelIdPrefix(TunerHostInfo info)
        {
            return ChannelIdPrefix + info.Url.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }

        protected override async Task<List<ChannelInfo>> GetChannelsInternal(TunerHostInfo tuner, CancellationToken cancellationToken)
        {
            var channelIdPrefix = GetFullChannelIdPrefix(tuner);

            return await new M3uParser(Logger, _httpClientFactory)
                .Parse(tuner, channelIdPrefix, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<List<LiveTvTunerInfo>> GetTunerInfos(CancellationToken cancellationToken)
        {
            var list = GetTunerHosts()
            .Select(i => new LiveTvTunerInfo()
            {
                Name = Name,
                SourceType = Type,
                Status = LiveTvTunerStatus.Available,
                Id = i.Url.GetMD5().ToString("N", CultureInfo.InvariantCulture),
                Url = i.Url
            })
            .ToList();

            return Task.FromResult(list);
        }

        protected override async Task<ILiveStream> GetChannelStream(TunerHostInfo tunerHost, ChannelInfo channel, string streamId, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            var tunerCount = tunerHost.TunerCount;

            if (tunerCount > 0)
            {
                var tunerHostId = tunerHost.Id;
                var liveStreams = currentLiveStreams.Where(i => string.Equals(i.TunerHostId, tunerHostId, StringComparison.OrdinalIgnoreCase));

                if (liveStreams.Count() >= tunerCount)
                {
                    throw new LiveTvConflictException("M3U simultaneous stream limit has been reached.");
                }
            }

            var sources = await GetChannelStreamMediaSources(tunerHost, channel, cancellationToken).ConfigureAwait(false);

            var mediaSource = sources[0];

            if (mediaSource.Protocol == MediaProtocol.Http && !mediaSource.RequiresLooping)
            {
                var extension = Path.GetExtension(mediaSource.Path) ?? string.Empty;

                if (!_disallowedSharedStreamExtensions.Contains(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return new SharedHttpStream(mediaSource, tunerHost, streamId, FileSystem, _httpClientFactory, Logger, Config, _appHost, _streamHelper);
                }
            }

            return new LiveStream(mediaSource, tunerHost, FileSystem, Logger, Config, _streamHelper);
        }

        public async Task Validate(TunerHostInfo info)
        {
            using (await new M3uParser(Logger, _httpClientFactory).GetListingsStream(info, CancellationToken.None).ConfigureAwait(false))
            {
            }
        }

        protected override Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(TunerHostInfo tuner, ChannelInfo channel, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<MediaSourceInfo> { CreateMediaSourceInfo(tuner, channel) });
        }

        protected virtual MediaSourceInfo CreateMediaSourceInfo(TunerHostInfo info, ChannelInfo channel)
        {
            var path = channel.Path;

            var supportsDirectPlay = !info.EnableStreamLooping && info.TunerCount == 0;
            var supportsDirectStream = !info.EnableStreamLooping;

            var protocol = _mediaSourceManager.GetPathProtocol(path);

            var isRemote = true;
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                isRemote = !_networkManager.IsInLocalNetwork(uri.Host);
            }

            var httpHeaders = new Dictionary<string, string>();

            if (protocol == MediaProtocol.Http)
            {
                // Use user-defined user-agent. If there isn't one, make it look like a browser.
                httpHeaders[HeaderNames.UserAgent] = string.IsNullOrWhiteSpace(info.UserAgent) ?
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/64.0.3282.85 Safari/537.36" :
                    info.UserAgent;
            }

            var mediaSource = new MediaSourceInfo
            {
                Path = path,
                Protocol = protocol,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        // Set the index to -1 because we don't know the exact index of the video stream within the container
                        Index = -1,
                        IsInterlaced = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        // Set the index to -1 because we don't know the exact index of the audio stream within the container
                        Index = -1
                    }
                },
                RequiresOpening = true,
                RequiresClosing = true,
                RequiresLooping = info.EnableStreamLooping,

                ReadAtNativeFramerate = false,

                Id = channel.Path.GetMD5().ToString("N", CultureInfo.InvariantCulture),
                IsInfiniteStream = true,
                IsRemote = isRemote,

                IgnoreDts = true,
                SupportsDirectPlay = supportsDirectPlay,
                SupportsDirectStream = supportsDirectStream,

                RequiredHttpHeaders = httpHeaders
            };

            mediaSource.InferTotalBitrate();

            return mediaSource;
        }

        public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        {
            return Task.FromResult(new List<TunerHostInfo>());
        }
    }
}