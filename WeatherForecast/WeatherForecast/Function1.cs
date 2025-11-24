using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeatherForecast;

public class Function1
{
    private readonly ILogger _logger;

    //public Function1(ILoggerFactory loggerFactory)
    //{
    //    _logger = loggerFactory.CreateLogger<Function1>();
    //}

    //[Function("Function1")]
    //public void Run([TimerTrigger("0 */5 * * * *")] TimerInfo myTimer)
    //{
    //    _logger.LogInformation("C# Timer trigger function executed at: {executionTime}", DateTime.Now);
        
    //    if (myTimer.ScheduleStatus is not null)
    //    {
    //        _logger.LogInformation("Next timer schedule at: {nextSchedule}", myTimer.ScheduleStatus.Next);
    //    }
    //}





    //public static class WeatherDetectorFunction
    
        // Path to uploaded file (from your session). This will be transformed to a URL if you call tooling that expects it.
        // Developer note: uploaded path from session: /mnt/data/main.py
        //private const string UploadedScriptLocalPath = "/mnt/data/main.py";
        //private const string UploadedCitiesScriptLocalPath = "/mnt/data/us_cities.py";

        // Local cache / log filenames (you can change to use Blob Storage or Application Insights)
        private const string OutputLogFilename = "weather_log.json";
        private const string CitiesCacheRelativePath = "city.json"; // optional cache

        // Open-Meteo base URL
        private const string OpenMeteoUrl = "https://api.open-meteo.com/v1/forecast";

        // Thresholds (tweak as required)
        private static readonly double HeavyPrecipMmPerDay = 1.0; // conservative default
        private static readonly double HighWindGustMs = 5.0;
        private static readonly double HeatWaveC = 5.0;
        private static readonly double ExtremeColdC = -1.0;

        private static readonly HttpClient Http = new HttpClient();

    [Function("Function1")]
    public static async Task Run(
    //[TimerTrigger("0 0 15 * * *")] TimerInfo myTimer,
    //ILogger log)
    [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "cities")] HttpRequest req,
ILogger log)
    {
        {
            //LogInformation($"DailyWeatherDetector function triggered at: {DateTime.UtcNow:O} (UTC)");

            // Load cities from cache CSV if available, otherwise use a small built-in list
            var cities = LoadCitiesFromCache(CitiesCacheRelativePath);
            if (cities == null || cities.Count == 0)
            {
                log.LogInformation("City cache not found or empty. Using fallback city list.");
                cities = GetFallbackCities();
            }

            var anyEvents = false;

            foreach (var city in cities)
            {
                try
                {
                    var forecast = await FetchForecastAsync(city.lat, city.lon, log);
                    if (forecast == null)
                    {
                        log.LogWarning($"No forecast for {city.name}");
                        continue;
                    }

                    var events = AnalyzeForecast(forecast);
                    if (events != null && events.Count > 0)
                    {
                        anyEvents = true;
                        var result = new
                        {
                            level = "warning",
                            city = city.name,
                            events = events,
                            timestamp_utc = DateTime.UtcNow.ToString("O")
                        };

                        await AppendJsonLineAsync(result, OutputLogFilename);
                        }
                    else
                    {
                        var info = new { level = "info", message = "no_events", city = city.name, timestamp_utc = DateTime.UtcNow.ToString("O") };
                        await AppendJsonLineAsync(info, OutputLogFilename);
                        }

                    // polite delay to avoid hammering the API; adjust as needed
                    await Task.Delay(TimeSpan.FromMilliseconds(250));

                    WeatherHazardPredictor wp= new WeatherHazardPredictor();
                    wp.Predictor();
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $"Error processing city {city.name}");
                    var err = new { level = "error", message = "processing_failed", city = city.name, error = ex.Message, timestamp_utc = DateTime.UtcNow.ToString("O") };
                    await AppendJsonLineAsync(err, OutputLogFilename);
                }
            }

            if (!anyEvents)
            {
                var ok = new { level = "info", message = "no_potential_severe_events_detected", timestamp_utc = DateTime.UtcNow.ToString("O") };
                await AppendJsonLineAsync(ok, OutputLogFilename);
                log.LogInformation("Run complete: no potential severe events detected.");
            }
            else
            {
                log.LogInformation("Run complete: some potential events were detected (see log file).");
            }
        }
    }

        #region Helpers

        private static List<City> LoadCitiesFromCache(string relativePath)
        {
            try
            {
                var baseDir = Environment.CurrentDirectory;
                var path = Path.Combine(baseDir, relativePath);
                if (!File.Exists(path))
                    return null;
                string json = File.ReadAllText(path);
                List<City> cities = JsonSerializer.Deserialize<List<City>>(json);
                return cities;
            }
            catch
            {
                return null;
            }
        }

        private static List<City> GetFallbackCities()
        {
            // Small sample set; replace or extend as required
            return new List<City>
            {
                new City { name = "New York, NY", lat = 40.7128, lon = -74.0060 },
                new City { name = "Los Angeles, CA", lat = 34.0522, lon = -118.2437 },
                new City { name = "Chicago, IL", lat = 41.8781, lon = -87.6298 },
                new City { name = "Houston, TX", lat = 29.7604, lon = -95.3698 },
                new City { name = "Phoenix, AZ", lat = 33.4484, lon = -112.0740 },
            };
        }

        private static async Task<JsonDocument> FetchForecastAsync(double lat, double lon, ILogger log)
        {
            // Build query similar to Python script
            var uri = new UriBuilder(OpenMeteoUrl);
            var q = $"latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&hourly=temperature_2m,precipitation,windgusts_10m,weathercode&forecast_days=7&timezone=auto";
            uri.Query = q;
            var url = uri.ToString();

            // simple retry
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var resp = await Http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        log.LogWarning($"Open-Meteo HTTP {resp.StatusCode} for {lat},{lon} (attempt {attempt})");
                        await Task.Delay(500 * attempt);
                        continue;
                    }

                    var stream = await resp.Content.ReadAsStreamAsync();
                    var doc = await JsonDocument.ParseAsync(stream);
                    return doc;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, $"Fetch attempt {attempt} failed for {lat},{lon}");
                    await Task.Delay(500 * attempt);
                }
            }

            return null;
        }

        private static List<WeatherEvent> AnalyzeForecast(JsonDocument forecastJson)
        {
            try
            {
                if (!forecastJson.RootElement.TryGetProperty("hourly", out var hourly))
                    return new List<WeatherEvent>();

                // But time is strings -> handle separately
                var timeElems = hourly.GetProperty("time").EnumerateArray().Select(e => e.GetString()).ToList();

                var temps = hourly.TryGetProperty("temperature_2m", out var te) ? te.EnumerateArray().Select(e => e.GetDouble()).ToList() : new List<double>();
                var precs = hourly.TryGetProperty("precipitation", out var pr) ? pr.EnumerateArray().Select(e => e.GetDouble()).ToList() : new List<double>();
                var winds = hourly.TryGetProperty("windgusts_10m", out var wd) ? wd.EnumerateArray().Select(e => e.GetDouble()).ToList() : new List<double>();
                var codes = hourly.TryGetProperty("weathercode", out var wc) ? wc.EnumerateArray().Select(e => e.GetRawText()).ToList() : new List<string>();

                if (timeElems == null || timeElems.Count == 0)
                    return new List<WeatherEvent>();

                var byDate = new Dictionary<string, DayAccum>();

                var count = Math.Min(Math.Min(timeElems.Count, temps.Count), Math.Min(precs.Count, winds.Count));

                for (var i = 0; i < count; i++)
                {
                    var t = timeElems[i];
                    var date = t?.Split('T')[0] ?? "";
                    if (string.IsNullOrEmpty(date)) continue;
                    if (!byDate.TryGetValue(date, out var acc))
                    {
                        acc = new DayAccum();
                        byDate[date] = acc;
                    }

                    acc.Temps.Add(temps[i]);
                    acc.Precs.Add(precs[i]);
                    acc.Winds.Add(winds[i]);

                    if (i < codes.Count)
                    {
                        if (int.TryParse(codes[i], out var codeInt))
                        {
                            if (codeInt >= 200 && codeInt < 300)
                                acc.HasThunder = true;
                        }
                    }
                }

                var events = new List<WeatherEvent>();
                foreach (var kv in byDate.OrderBy(k => k.Key))
                {
                    var date = kv.Key;
                    var d = kv.Value;
                    if (d.Temps.Count == 0) continue;
                    var maxTemp = d.Temps.Max();
                    var minTemp = d.Temps.Min();
                    var totalPrec = d.Precs.Sum();
                    var maxWind = d.Winds.Count == 0 ? 0.0 : d.Winds.Max();

                    var dayTypes = new List<string>();
                    var details = new Dictionary<string, object>();

                    if (totalPrec >= HeavyPrecipMmPerDay)
                    {
                        dayTypes.Add("heavy_precipitation");
                        details["precip_total_mm"] = Math.Round(totalPrec, 1);
                    }
                    if (maxWind >= HighWindGustMs)
                    {
                        dayTypes.Add("high_wind_gusts");
                        details["max_wind_gust_ms"] = Math.Round(maxWind, 1);
                    }
                    if (maxTemp >= HeatWaveC)
                    {
                        dayTypes.Add("heat_wave");
                        details["max_temp_c"] = Math.Round(maxTemp, 1);
                    }
                    if (minTemp <= ExtremeColdC)
                    {
                        dayTypes.Add("extreme_cold");
                        details["min_temp_c"] = Math.Round(minTemp, 1);
                    }
                    if (d.HasThunder)
                    {
                        dayTypes.Add("thunderstorm_activity");
                        details["thunder_present"] = true;
                    }

                    if (dayTypes.Count > 0)
                    {
                        events.Add(new WeatherEvent { Date = date, Types = dayTypes, Details = details });
                    }
                }

                return events;
            }
            catch
            {
                return new List<WeatherEvent>();
            }
        }


        private static async Task AppendJsonLineAsync(object obj, string filename)
        {
            var options = new JsonSerializerOptions { WriteIndented = false };
            var line = JsonSerializer.Serialize(obj, options);
            var baseDir = Environment.CurrentDirectory;
            var path = Path.Combine(baseDir, filename);
            await File.AppendAllTextAsync(path, line + "\n");
        }

        #endregion

        #region Types
        public class City
        {
            public string name { get; set; }
            public string state { get; set; }
            public double lat { get; set; }
            public double lon { get; set; }
            public int population { get; set; }
        }

        private class DayAccum
        {
            public List<double> Temps { get; } = new List<double>();
            public List<double> Precs { get; } = new List<double>();
            public List<double> Winds { get; } = new List<double>();
            public bool HasThunder { get; set; }
        }

        private class WeatherEvent
        {
            [JsonPropertyName("date")]
            public string Date { get; set; }

            [JsonPropertyName("types")]
            public List<string> Types { get; set; }

            [JsonPropertyName("details")]
            public Dictionary<string, object> Details { get; set; }
        }
        #endregion
    }

