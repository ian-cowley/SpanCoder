using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SpanCoder.Contracts;

namespace SpanCoder.Shell
{
    public class NuGetApiClient
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public class PackageData
        {
            public string Id { get; set; } = "";
            public string Version { get; set; } = "";
            public string Description { get; set; } = "";
            public List<string> Authors { get; set; } = new();
            public string IconUrl { get; set; } = "";
            public string ProjectUrl { get; set; } = "";
            public string LicenseUrl { get; set; } = "";
            public long TotalDownloads { get; set; }
            public List<VersionEntry> Versions { get; set; } = new();
        }

        public class VersionEntry
        {
            public string Version { get; set; } = "";
            public long Downloads { get; set; }
        }

        public static async Task<List<PackageData>> SearchPackagesAsync(string query, bool includePrerelease, int take = 30)
        {
            try
            {
                string encodedQuery = Uri.EscapeDataString(query);
                string url = $"https://azuresearch-usnc.nuget.org/query?q={encodedQuery}&prerelease={includePrerelease.ToString().ToLower()}&take={take}";

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("SpanCoder/1.0 NuGetClient");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                
                var list = new List<PackageData>();
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataProp.EnumerateArray())
                        {
                            var pkg = ParsePackageData(item);
                            if (pkg != null)
                            {
                                list.Add(pkg);
                            }
                        }
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetApiClient] Search failed: {ex}");
                return new List<PackageData>();
            }
        }

        public static async Task<PackageData?> GetPackageDetailsAsync(string packageId)
        {
            try
            {
                string url = $"https://azuresearch-usnc.nuget.org/query?q=packageid:{packageId.ToLower()}&prerelease=true&take=1";
                
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("SpanCoder/1.0 NuGetClient");

                var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataProp.EnumerateArray())
                        {
                            return ParsePackageData(item);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Log($"[NuGetApiClient] Details fetch failed: {ex}");
            }
            return null;
        }

        private static PackageData? ParsePackageData(JsonElement item)
        {
            try
            {
                var pkg = new PackageData();
                if (item.TryGetProperty("id", out var idProp)) pkg.Id = idProp.GetString() ?? "";
                if (item.TryGetProperty("version", out var verProp)) pkg.Version = verProp.GetString() ?? "";
                if (item.TryGetProperty("description", out var descProp)) pkg.Description = descProp.GetString() ?? "";
                if (item.TryGetProperty("iconUrl", out var iconProp)) pkg.IconUrl = iconProp.GetString() ?? "";
                if (item.TryGetProperty("projectUrl", out var projProp)) pkg.ProjectUrl = projProp.GetString() ?? "";
                if (item.TryGetProperty("licenseUrl", out var licProp)) pkg.LicenseUrl = licProp.GetString() ?? "";
                
                if (item.TryGetProperty("totalDownloads", out var dlProp))
                {
                    if (dlProp.ValueKind == JsonValueKind.Number) pkg.TotalDownloads = dlProp.GetInt64();
                }

                if (item.TryGetProperty("authors", out var authProp))
                {
                    if (authProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in authProp.EnumerateArray())
                        {
                            pkg.Authors.Add(a.GetString() ?? "");
                        }
                    }
                    else if (authProp.ValueKind == JsonValueKind.String)
                    {
                        pkg.Authors.Add(authProp.GetString() ?? "");
                    }
                }

                if (item.TryGetProperty("versions", out var versProp) && versProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in versProp.EnumerateArray())
                    {
                        var entry = new VersionEntry();
                        if (v.TryGetProperty("version", out var vVer)) entry.Version = vVer.GetString() ?? "";
                        if (v.TryGetProperty("downloads", out var vDl) && vDl.ValueKind == JsonValueKind.Number)
                        {
                            entry.Downloads = vDl.GetInt64();
                        }
                        pkg.Versions.Add(entry);
                    }
                }

                return pkg;
            }
            catch
            {
                return null;
            }
        }
    }
}
