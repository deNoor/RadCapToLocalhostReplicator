using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RadCapToLocalhostReplicator
{
    public partial class Options
    {
        internal const string FileName = "setting.json";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };

        public static Options Default { get; } = new()
        {
            SongNameFilePath =
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ObsNowPlaying", "LocalStationCurrentSong.txt"),
            RadCapStationUrl = @"http://79.120.39.202:8000/darkelectro",
            LocalUrl = "http://localhost:51111/",
            FirstStart = true,
        };

        [JsonIgnore]
        public bool FirstStart { get; set; }

        public static async Task<Options?> InitAsync()
        {
            Options? options;
            if (!File.Exists(FileName))
            {
                await using var fs = new FileStream(
                    FileName,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);

                await JsonSerializer.SerializeAsync(fs, Default, _jsonOptions);
                Path.GetFullPath(FileName);
                options = Default;
            }
            else
            {
                await using var fs = new FileStream(
                    FileName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous);

                options = await JsonSerializer.DeserializeAsync<Options>(fs, _jsonOptions);
                if (options is not null)
                {
                    options.FirstStart = false;
                }
            }

            return options;
        }

        public static bool AreInvalid(Options? options, out string message)
        {
            if (options is null)
            {
                message = $"Unable to read program settings from {Path.GetFullPath(FileName)}";
                return true;
            }
            if (string.IsNullOrWhiteSpace(options.RadCapStationUrl))
            {
                message = InvalidPropertyMessage(nameof(RadCapStationUrl));
                return true;
            }
            if (string.IsNullOrWhiteSpace(options.LocalUrl))
            {
                message = InvalidPropertyMessage(nameof(LocalUrl));
                return true;
            }
            if (string.IsNullOrWhiteSpace(options.SongNameFilePath))
            {
                message = InvalidPropertyMessage(nameof(SongNameFilePath));
                return true;
            }
            if (options.FirstStart)
            {
                message =
                    $"First launch detected. Verify settings at {Path.GetFullPath(FileName)} and restart the program.";
                return true;
            }
            message = string.Empty;
            return false;

            static string InvalidPropertyMessage(string propertyName)
                => $"Invalid {propertyName}, check {Path.GetFullPath(FileName)}";
        }
    }
}