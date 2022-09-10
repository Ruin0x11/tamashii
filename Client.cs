using MetaBrainz.MusicBrainz;
using MetaBrainz.MusicBrainz.Interfaces.Entities;
using Soulseek;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace tamashii
{
    internal class SearchSession
    {
        public SearchSession(SearchQuery query, IArtist artist, IRelease release, IRecording recording, CancellationTokenSource cancellationTokenSource)
        {
            Query = query;
            Artist = artist;
            Release = release;
            Recording = recording;
            CancellationTokenSource = cancellationTokenSource;
        }

        public SearchQuery Query { get; }
        public IArtist Artist { get; }
        public IRelease Release { get; }
        public IRecording Recording { get; }
        public CancellationTokenSource CancellationTokenSource { get; }
        public SearchResult? Result { get; set; } = null;
    }

    internal class SearchResult
    {
        public SearchResult(string username, Soulseek.File found)
        {
            Username = username;
            File = found;
        }

        public string Username { get; }
        public Soulseek.File File { get; }
    }

    internal class Client
    {
        private readonly SoulseekClient _slskClient;
        private readonly Query _musicBrainzClient;

        private SearchSession? _currentSearch = null;

        public Client()
        {
            _slskClient = new SoulseekClient();
            _musicBrainzClient = new Query("tamashii", Assembly.GetExecutingAssembly().GetName().Version, "nonbirithm@fastmail.com");
        }

        public async Task SetupAsync()
        {
            _currentSearch = null;
            await _slskClient.ConnectAsync("username", "password");
        }

        public Task TearDownAsync()
        {
            _slskClient.Disconnect();
            _currentSearch = null;
            return Task.CompletedTask;
        }

        private void ResponseReceived((Search Search, SearchResponse Response) pair)
        {
            if (_currentSearch == null)
            {
                Console.WriteLine("No search");
                return;
            }

            var (search, response) = pair;

            if (!response.HasFreeUploadSlot)
                return;

            Console.WriteLine($"- {response.Username} ({response.FileCount} files)");

            foreach (var file in response.Files)
            {
                Console.WriteLine($"   | {file.Filename} ({file.BitDepth}/{file.Extension}, {file.Size})");
            }

            var found = response.Files.Where(f => f.Filename.EndsWith(".mp3")).FirstOrDefault();
            if (found == null)
                return;

            _currentSearch.CancellationTokenSource.Cancel();
            _currentSearch.Result = new SearchResult(response.Username, found);
        }

        private void StateChanged((SearchStates PreviousState, Search Search) pair)
        {
            if (_currentSearch == null)
            {
                Console.WriteLine("No search");
                return;
            }

            var (previousState, search) = pair;

            switch (search.State)
            {
                case SearchStates.None:
                case SearchStates.Requested:
                case SearchStates.InProgress:
                default:
                    break;
                case SearchStates.Completed:
                case SearchStates.Cancelled:
                case SearchStates.TimedOut:
                case SearchStates.ResponseLimitReached:
                case SearchStates.FileLimitReached:
                case SearchStates.Errored:
                    Console.WriteLine($"Search finished with result: {search.State}");
                    _currentSearch = null;
                    break;
            }
        }

        public async Task PlayAsync(Guid recordingMBID, CancellationToken cancelToken)
        {
            if (_currentSearch != null)
            {
                throw new InvalidOperationException($"Search is currently ongoing: {_currentSearch.Query}");
            }

            var recording = await _musicBrainzClient.LookupRecordingAsync(recordingMBID, Include.Releases | Include.Artists);
            var release = recording.Releases!.First();
            var artist = recording.ArtistCredit!.First().Artist!;

            var cancelSource = new CancellationTokenSource();
            var query = SearchQuery.FromText($"{artist.Name} {recording.Title}");
            var opts = new SearchOptions(responseLimit: 100, stateChanged: StateChanged, responseReceived: ResponseReceived);

            Console.WriteLine($"Searching: \"{query.SearchText}\"");
            _currentSearch = new SearchSession(query, artist, release, recording, cancelSource);

            try
            {
                var search = await _slskClient.SearchAsync(query, options: opts, cancellationToken: cancelSource.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                _currentSearch = null;
                throw;
            }

            var result = _currentSearch.Result;
            _currentSearch = null;

            if (result == null)
            {
                Console.WriteLine("No results found.");
                return;
            }

            Console.WriteLine($"Download: {result.Username} {result.File.Filename}");
            Console.WriteLine($"File: {result.File.SampleRate} {result.File.BitRate} {result.File.BitRate} {result.File.IsVariableBitRate}");

            var buffer = new byte[100000];

            var player = new Player();
            var stream = new SlidingStream();

            void TStateChanged((TransferStates PreviousState, Transfer Transfer) pair)
            {
                var (prevState, transfer) = pair;
                Console.WriteLine($"STATE CHANGE: {prevState} -> {transfer.State}");
            }

            void ProgressUpdated((long PreviousBytesTransferred, Transfer Transfer) pair)
            {
                var (bytes, transfer) = pair;
                // Console.WriteLine($"XFER: {bytes}, {transfer.AverageSpeed}");
            }

            var size = result.File.Size;

            // var stream2 = new FileStream("C:\\Users\\Yuno\\Music\\test.mp3", FileMode.Create, FileAccess.ReadWrite);

            var transferOpts = new TransferOptions(stateChanged: TStateChanged, progressUpdated: ProgressUpdated);
            var transfer = _slskClient.DownloadAsync(username: result.Username, remoteFilename: result.File.Filename, size: size, outputStreamFactory: () => stream, options: transferOpts, cancellationToken: cancelToken);

            Console.WriteLine("TRANSFER");
            // await transfer;
            
            // await stream2.DisposeAsync();

            Console.WriteLine("PLAY");
            //var play = player.DoPlay(new FileStream("C:\\Users\\Yuno\\Music\\test.mp3", FileMode.Open, FileAccess.ReadWrite), size);
            var play = player.DoPlay(stream, size, cancelToken);
            await play;
        }
    }
}