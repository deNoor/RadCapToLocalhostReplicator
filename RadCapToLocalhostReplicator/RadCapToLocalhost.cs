using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RadCapToLocalhostReplicator
{
    internal class RadCapToLocalhost : IDisposable
    {
        private const string IcyMetadataKey = "Icy-Metadata";
        private const string IcyMetaintKey = "Icy-Metaint";
        private const int IcyCastLengthValue = 16; // Defined by the IceCast protocol.

        private static readonly Regex _songTitlePattern = new(
            "StreamTitle='(?'Title'.+?)'",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(50));

        private CancellationTokenSource? _currentlyActiveConnection;

        public RadCapToLocalhost(Options options, Action<string?>? infoOutput, Action<string?>? debugOutput)
        {
            InfoOutput = infoOutput ?? InfoOutput;
            DebugOutput = debugOutput ?? DebugOutput;
            HttpListener.Prefixes.Add(options.LocalUrl!);
            CurrentSongFilePath = options.SongNameFilePath!;
            Station = new Uri(options.RadCapStationUrl!);
            EnsureDirectory();
            AppDomain.CurrentDomain.ProcessExit += async (_, _) => await ResetFileAsync();
        }

        public bool Disposed { get; private set; }

        private HttpClient HttpClient { get; } = new();
        private HttpListener HttpListener { get; } = new();
        private Action<string?> InfoOutput { get; } = SkipOutput;
        private Action<string?> DebugOutput { get; } = SkipOutput;
        private string CurrentSongFilePath { get; }
        private Uri Station { get; }

        public async Task RunAsync()
        {
            CheckDisposed();
            HttpListener.Start();
            ReportOnStart();
            while (true)
            {
                var context = await HttpListener.GetContextAsync();
                _ = Task.Run(() => ProcessRequestAsync(context));
            }
        }

        private static string AddCurrentTime() => $"{DateTimeOffset.Now.TimeOfDay:hh':'mm':'ss}";

        private static void SkipOutput(string? message)
        {
        }

        private static void SetupRadioRequestHeaders(
            HttpRequestHeaders radioRequestHeaders,
            NameValueCollection localhostRequestHeaders)
        {
            // Keep original request headers.
            foreach (string? key in localhostRequestHeaders)
            {
                if (key is not null)
                {
                    var value = localhostRequestHeaders[key];
                    radioRequestHeaders.Add(key, value);
                }
            }
            // Ask for metadata.
            if (!radioRequestHeaders.TryGetValues(IcyMetadataKey, out var _))
            {
                radioRequestHeaders.Add(IcyMetadataKey, "1");
            }
            if (!radioRequestHeaders.Accept.Any())
            {
                radioRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
            }
        }

        private void ReportOnStart()
        {
            InfoOutput(string.Empty);
            InfoOutput($"Radio: {Station}");
            InfoOutput($"Update song name from: {Path.GetFullPath(CurrentSongFilePath)}");
            InfoOutput($"Grab media stream from: {HttpListener.Prefixes.SingleOrDefault()}");
            InfoOutput(string.Empty);
            InfoOutput("Waiting for connection...");
            InfoOutput(string.Empty);
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            CheckDisposed();
            using var cts = new CancellationTokenSource();
            var token = cts.Token;

            var currentConnection = cts;
            var requestDisplayedData = $"{context.Request.RemoteEndPoint} {context.Request.UserAgent}";
            try
            {
                // Only single retransmission connection is allowed. Close the existing one.
                var previousConnection = Interlocked.Exchange(ref _currentlyActiveConnection, currentConnection);
                previousConnection?.Cancel();

                InfoOutput($"Got connection from {requestDisplayedData}");

                // Target the remote radio.
                var radioRequest = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = Station,
                };

                SetupRadioRequestHeaders(radioRequest.Headers, context.Request.Headers);

                // Get the remote radio.
                var radioResponse = await HttpClient.SendAsync(
                    radioRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);

                // Transmit all headers.
                foreach (var (key, value) in radioResponse.Headers)
                {
                    foreach (var v in value)
                    {
                        context.Response.Headers.Add(key, v);
                    }
                }

                // Replicate content with metadata interception.
                string? icyMetaintHeaderValue;
                var icyMetaintValue = 0;
                var keepMetadataInStream = context.Request.Headers[IcyMetadataKey] is not null;

                bool MetadataIsAvailable()
                    => radioResponse.Headers.TryGetValues(
                            IcyMetaintKey,
                            out var icyMetaintHeaderValues)
                        && (icyMetaintHeaderValue = icyMetaintHeaderValues.SingleOrDefault()) is not null
                        && int.TryParse(icyMetaintHeaderValue, out icyMetaintValue);

                if (MetadataIsAvailable())
                {
                    var oldSongName = string.Empty;

                    var dataBuffer = new byte[icyMetaintValue];

                    var metadataLengthBuffer = new byte[1];
                    var metadataBuffer = new byte[byte.MaxValue * IcyCastLengthValue];

                    var radioContentStream = await radioResponse.Content.ReadAsStreamAsync(token);

                    while (await MusicIsPlayingAsync()
                        && await GotMetadataLengthAsync()
                        && await CanReadSongTitleAsync())
                    {
                        // chill. The radio is up and running.
                    }

                    async ValueTask<bool> MusicIsPlayingAsync()
                        => await FillBufferAsync(dataBuffer, dataBuffer.Length, true);

                    async ValueTask<bool> GotMetadataLengthAsync()
                        => await FillBufferAsync(
                            metadataLengthBuffer,
                            metadataLengthBuffer.Length,
                            keepMetadataInStream);

                    async ValueTask<bool> CanReadSongTitleAsync()
                    {
                        var metadataLength = metadataLengthBuffer[0] * IcyCastLengthValue;
                        if (metadataLength > 0)
                        {
                            if (!await FillBufferAsync(metadataBuffer, metadataLength, keepMetadataInStream))
                            {
                                return false;
                            }

                            var metadata = Encoding.UTF8.GetString(
                                new ReadOnlySpan<byte>(metadataBuffer, 0, metadataLength));
                            var currentSong = _songTitlePattern.Match(metadata).Groups["Title"].Value;
                            InfoOutput($"{AddCurrentTime()} {currentSong}");
                            await UpdateSongTitleInFile();

                            async ValueTask UpdateSongTitleInFile()
                            {
                                if (currentSong != oldSongName)
                                {
                                    try
                                    {
                                        await File.WriteAllTextAsync(CurrentSongFilePath, currentSong, token);
                                        oldSongName = currentSong;
                                    }
                                    catch (Exception e)
                                    {
                                        InfoOutput($"Failed to update file.{Environment.NewLine}{e}");
                                    }
                                }
                            }
                        }

                        return true;
                    }

                    async ValueTask<bool> FillBufferAsync(byte[] buffer, int count, bool writeToOutput)
                    {
                        var offset = 0;
                        while (offset < count)
                        {
                            var bytesRead =
                                await radioContentStream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
                            if (bytesRead < 1)
                            {
                                InfoOutput("End of radio stream.");
                                return false;
                            }
                            if (writeToOutput && !token.IsCancellationRequested)
                            {
                                await context.Response.OutputStream.WriteAsync(
                                    buffer.AsMemory(offset, bytesRead),
                                    token);
                            }
                            offset += bytesRead;
                        }

                        return true;
                    }
                }
                else
                {
                    // Pure replication without interception or modification if metadata is not available.
                    await (await radioResponse.Content.ReadAsStreamAsync(token)).CopyToAsync(
                        context.Response.OutputStream,
                        token);
                }
            }
            catch (HttpListenerException e) when (e.ErrorCode == 1229)
            {
                // The connection is closed on user's end.
                // Is reported as 'An operation was attempted on a nonexistent network connection'.
                // Occurs when writing to the output stream if the user closes the connection.
                // Couldn't find a better way to handle this from managed code.
                // See also the related question https://stackoverflow.com/q/1329932
                DebugOutput($"{requestDisplayedData} connection is cancelled by the user.");
                await ResetFileAsync();
            }
            catch (TaskCanceledException)
            {
                // Happens because we cancel the previous connection to allow only one active.
                DebugOutput($"{requestDisplayedData} connection is cancelled by the server.");
            }
            finally
            {
                Interlocked.CompareExchange(ref _currentlyActiveConnection, null, currentConnection);
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                context.Response.Close();
                InfoOutput($"Terminated connection from {requestDisplayedData}{Environment.NewLine}");
            }
        }

        private void EnsureDirectory()
        {
            var directory = Path.GetDirectoryName(CurrentSongFilePath);
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private async Task ResetFileAsync()
        {
            if (File.Exists(CurrentSongFilePath))
            {
                await File.WriteAllTextAsync(CurrentSongFilePath, string.Empty);
            }
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Disposed)
            {
                return;
            }

            if (disposing)
            {
                ((IDisposable) HttpListener).Dispose();
                HttpClient.Dispose();
            }

            Disposed = true;
        }

        private void CheckDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(RadCapToLocalhost));
            }
        }

        #endregion
    }
}