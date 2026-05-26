using launcherdotnet.PluginAPI;
using Semver;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace MSZInstallerPlugin
{
    public class Installer : IGameInstaller
    {
        public string GameName => "Miside Zero";
        public string Name => "Miside Zero Installer";
        public string Description => "Downloads and installs Miside Zero from my Github mirror";
        public SemVersion TargetApiVersion => new SemVersion(0, 8, 0);

        private readonly string _releasesEndpoint = "https://api.github.com/repos/Gameknight963/MSZVersionArchive/releases";

        List<ReleaseInfo> versions = new List<ReleaseInfo>();

        public async Task Initialize()
        {

            HttpClient Http = new();
            Http.DefaultRequestHeaders.Add("User-Agent", "launcher.NET");
            HttpResponseMessage resp = await Http.GetAsync(_releasesEndpoint);
            resp.EnsureSuccessStatusCode();

            string responseString = await resp.Content.ReadAsStringAsync();
            JsonArray releases = JsonNode.Parse(responseString)?.AsArray()
                ?? throw new InvalidOperationException("Invalid stable releases API response.");

            foreach (JsonNode? release in releases)
            {
                if (!SemVersion.TryParse(release?["tag_name"]?.ToString(), SemVersionStyles.Any, out SemVersion? relVersion))
                    continue;

                JsonArray? assets = release?["assets"]?.AsArray();
                if (assets == null) continue;

                JsonNode? downloadAsset = assets.FirstOrDefault(x =>
                    x?["name"]?.ToString().Contains("win64") == true);

                if (downloadAsset == null) continue;

                string downloadUrl = downloadAsset["browser_download_url"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(downloadUrl)) continue;

                versions.Add(new ReleaseInfo { Version = relVersion, Url = downloadUrl });
            }
            GameInstallerRegistry.RegisterGameInstallPlugin(this, versions);
        }

        public async Task<PluginGameInfo> Install(
            string installDir,
            ReleaseInfo release,
            IProgress<double> progress,
            IProgress<string> status)
        {
            ReleaseInfo? found = versions.FirstOrDefault(r => r == release);
            if (found == null) throw new InvalidOperationException("Selected version not found.");

            using (HttpResponseMessage response = await launcherdotnet.Networking.LauncherHttp.Client.GetAsync(found.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                using (InstanceTempDir tempDir = new InstanceTempDir())
                {
                    string zipPath = Path.Combine(tempDir.Path, $"MSZ-{Guid.NewGuid()}.zip");

                    // --- download with progress ---
                    using (Stream responseStream = await response.Content.ReadAsStreamAsync())
                    using (FileStream fileStream = File.Create(zipPath))
                    {
                        long total = response.Content.Headers.ContentLength ?? -1;
                        long readSoFar = 0;
                        byte[] buffer = new byte[81920]; // 80KB buffer
                        int bytesRead;
                        while ((bytesRead = await responseStream.ReadAsync(buffer)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                            readSoFar += bytesRead;

                            if (total > 0)
                                progress?.Report(readSoFar * 100.0 / total);

                            status?.Report($"Downloading... {readSoFar / 1024.0 / 1024.0:0.0} MB");
                        }
                    }

                    // --- extract with per-entry progress ---
                    using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                    {
                        int totalEntries = archive.Entries.Count;
                        int extractedEntries = 0;

                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            string destinationPath = Path.Combine(installDir, entry.FullName);

                            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                            if (!string.IsNullOrEmpty(entry.Name))
                                entry.ExtractToFile(destinationPath, true);

                            extractedEntries++;
                            progress?.Report(100.0 * extractedEntries / totalEntries);
                            status?.Report($"Extracting {entry.FullName} ({extractedEntries}/{totalEntries})");
                        }
                    }

                    string exePath = Path.Combine(installDir, "MiSide Zero.exe");
                    if (!File.Exists(exePath))
                        throw new InvalidOperationException("Extraction failed! Game exe not found");

                    return new PluginGameInfo(exePath);
                }
            }
        }
    }
}
