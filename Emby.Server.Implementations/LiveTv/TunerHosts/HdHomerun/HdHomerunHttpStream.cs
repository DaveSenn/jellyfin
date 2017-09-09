﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.IO;
using MediaBrowser.Model.IO;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.System;
using System.Globalization;
using MediaBrowser.Controller.IO;

namespace Emby.Server.Implementations.LiveTv.TunerHosts.HdHomerun
{
    public class HdHomerunHttpStream : LiveStream, IDirectStreamProvider
    {
        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IServerApplicationHost _appHost;

        private readonly CancellationTokenSource _liveStreamCancellationTokenSource = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _liveStreamTaskCompletionSource = new TaskCompletionSource<bool>();

        private readonly MulticastStream _multicastStream;

        private readonly string _tempFilePath;
        private bool _enableFileBuffer = false;

        public HdHomerunHttpStream(MediaSourceInfo mediaSource, string originalStreamId, IFileSystem fileSystem, IHttpClient httpClient, ILogger logger, IServerApplicationPaths appPaths, IServerApplicationHost appHost, IEnvironmentInfo environment)
            : base(mediaSource, environment, fileSystem)
        {
            _httpClient = httpClient;
            _logger = logger;
            _appHost = appHost;
            OriginalStreamId = originalStreamId;

            _multicastStream = new MulticastStream(_logger);
            _tempFilePath = Path.Combine(appPaths.TranscodingTempPath, UniqueId + ".ts");
        }

        protected override async Task OpenInternal(CancellationToken openCancellationToken)
        {
            _liveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var mediaSource = OriginalMediaSource;

            var url = mediaSource.Path;

            _logger.Info("Opening HDHR Live stream from {0}", url);

            var taskCompletionSource = new TaskCompletionSource<bool>();

            StartStreaming(url, taskCompletionSource, _liveStreamCancellationTokenSource.Token);

            //OpenedMediaSource.Protocol = MediaProtocol.File;
            //OpenedMediaSource.Path = tempFile;
            //OpenedMediaSource.ReadAtNativeFramerate = true;

            OpenedMediaSource.Path = _appHost.GetLocalApiUrl("127.0.0.1") + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            OpenedMediaSource.Protocol = MediaProtocol.Http;
            //OpenedMediaSource.SupportsDirectPlay = false;
            //OpenedMediaSource.SupportsDirectStream = true;
            //OpenedMediaSource.SupportsTranscoding = true;

            await taskCompletionSource.Task.ConfigureAwait(false);

            //await Task.Delay(5000).ConfigureAwait(false);
        }

        public override Task Close()
        {
            _logger.Info("Closing HDHR live stream");
            _liveStreamCancellationTokenSource.Cancel();

            return _liveStreamTaskCompletionSource.Task;
        }

        private Task StartStreaming(string url, TaskCompletionSource<bool> openTaskCompletionSource, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var isFirstAttempt = true;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using (var response = await _httpClient.SendAsync(new HttpRequestOptions
                        {
                            Url = url,
                            CancellationToken = cancellationToken,
                            BufferContent = false,

                            // Increase a little bit
                            TimeoutMs = 30000

                        }, "GET").ConfigureAwait(false))
                        {
                            _logger.Info("Opened HDHR stream from {0}", url);

                            if (!cancellationToken.IsCancellationRequested)
                            {
                                _logger.Info("Beginning multicastStream.CopyUntilCancelled");

                                if (_enableFileBuffer)
                                {
                                    FileSystem.CreateDirectory(FileSystem.GetDirectoryName(_tempFilePath));
                                    using (var fileStream = FileSystem.GetFileStream(_tempFilePath, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, FileOpenOptions.None))
                                    {
                                        StreamHelper.CopyTo(response.Content, fileStream, 81920, () => Resolve(openTaskCompletionSource), cancellationToken);
                                    }
                                }
                                else
                                {
                                    Resolve(openTaskCompletionSource);

                                    await _multicastStream.CopyUntilCancelled(response.Content, null, cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (isFirstAttempt)
                        {
                            _logger.ErrorException("Error opening live stream:", ex);
                            openTaskCompletionSource.TrySetException(ex);
                            break;
                        }

                        _logger.ErrorException("Error copying live stream, will reopen", ex);
                    }

                    isFirstAttempt = false;
                }

                _liveStreamTaskCompletionSource.TrySetResult(true);
                //await DeleteTempFile(_tempFilePath).ConfigureAwait(false);
            });
        }

        private void Resolve(TaskCompletionSource<bool> openTaskCompletionSource)
        {
            Task.Run(() =>
            {
                openTaskCompletionSource.TrySetResult(true);
            });
        }

        public Task CopyToAsync(Stream stream, CancellationToken cancellationToken)
        {
            return _multicastStream.CopyToAsync(stream, cancellationToken);
        }
    }
}
