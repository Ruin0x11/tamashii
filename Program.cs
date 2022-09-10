using MetaBrainz.MusicBrainz;
using System.Reflection;
using System.Threading;

namespace tamashii
{
    internal class Program
    {
        private Client _client = new();

        static async Task Main(string[] args)
        {
            var program = new Program();

            await program.SetupAsync();

            while (true)
            {
                await program.QueryAsync();
            }
        }

        private async Task SetupAsync()
        {
            await _client.SetupAsync();
        }

        private async Task TearDownAsync()
        {
            await _client.TearDownAsync();
        }

        public async Task QueryAsync()
        {
            string? artist = null;
            while (string.IsNullOrWhiteSpace(artist))
            {
                Console.Write("Artist: ");
                artist = Console.ReadLine();
            }

            string? track = null;
            while (string.IsNullOrWhiteSpace(track))
            {
                Console.Write("Track: ");
                track = Console.ReadLine();
            }

            var query = new Query("tamashii", Assembly.GetExecutingAssembly().GetName().Version, "nonbirithm@fastmail.com");

            var results = await query.FindRecordingsAsync($"\"{track}\" AND artist:\"{artist}\"");
            var list = results.Results.OrderBy(r => r.Item.FirstReleaseDate ?? PartialDate.Empty).Take(10).ToList();
            if (list.Count == 0)
            {
                Console.WriteLine("No results.");
                return;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var result = list[i];
                Console.WriteLine($"{i + 1}) {result.Item.ArtistCredit?.FirstOrDefault()?.Name} - {result.Item.Title} ({result.Item.FirstReleaseDate})");
            }

            int? selected = null;
            while (selected == null)
            {
                Console.Write("Which? ");
                var selectedStr = Console.ReadLine();
                if (selectedStr != null)
                {
                    if (int.TryParse(selectedStr.Trim(), out var selectedI) && selectedI > 0 && selectedI <= list.Count)
                        selected = selectedI - 1;
                }
                else
                    return;
            }

            var cancelSrc = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (s, e) => cancelSrc.Cancel();
            Console.CancelKeyPress += cancelHandler;

            var item = list[selected.Value].Item;
            await _client.PlayAsync(item.Id, cancelSrc.Token);
            Console.WriteLine();

            Console.CancelKeyPress -= cancelHandler;
        }
    }
}