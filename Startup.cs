using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using JsonException = Newtonsoft.Json.JsonException;

namespace ProsecutionDataSync
{
    public class Startup
    {
        private readonly HttpClient _httpClient;
        private string _jwtToken;
        private readonly string _localApiBaseUrl = "https://localhost:44365/";
        private readonly string _centralApiBaseUrl = "https://kenload.kenha.co.ke:4444/";

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
            _httpClient = new HttpClient();
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(builder =>
            {
                builder.AddConsole();
                builder.AddDebug();
            });
            services.AddControllers();
            services.AddHostedService<SyncService>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseHttpsRedirection();
            app.UseRouting();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }

    public class SyncService : BackgroundService
    {
        private readonly ILogger<SyncService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _localApiBaseUrl = "https://localhost:44365/";
        private readonly string _centralApiBaseUrl = "https://kenload.kenha.co.ke:4444/";
        private string _jwtToken;

        public SyncService(ILogger<SyncService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Starting data sync process...");
                    await LoginAndGetToken();
                    await SyncData();
                    _logger.LogInformation("Data sync process completed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while syncing data");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Sync every 5 minutes
            }
        }

        private async Task LoginAndGetToken()
        {
            try
            {
                _logger.LogInformation("Attempting to log in and retrieve JWT token...");
                var loginData = new { email = "admin@admin.com", password = "@Admin123" };
                var response = await _httpClient.PostAsync(
                    $"{_localApiBaseUrl}api/authmanagement/login",
                    new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json")
                );

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadAsStringAsync();
                    var tokenResponse = JsonSerializer.Deserialize<JsonElement>(result);
                    _jwtToken = tokenResponse.GetProperty("token").GetString();
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);
                    _logger.LogInformation("JWT token retrieved successfully.");
                }
                else
                {
                    _logger.LogError("Failed to get JWT token");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during login");
                throw;
            }
        }

        private async Task SyncData()
        {
            try
            {
                _logger.LogInformation("Fetching and syncing data...");

                // Fetch and sync data for each table
                //await FetchAndSyncTable("casedetails", "api/casedetails");
                //await FetchAndSyncTable("casedocs", "api/casedocs");
                //await FetchAndSyncTable("eacact", "api/eacact");
                await FetchAndSyncTable("invoicing", "api/invoicing");
                await FetchAndSyncTable("caseresults", "api/caseresults");
                await FetchAndSyncTable("receipt", "api/receipt");

                _logger.LogInformation("Data sync completed for all tables.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data sync");
            }
        }
        private async Task<bool> CheckIfExists(string url)
        {
            try
            {
                _logger.LogInformation($"Checking existence at: {url}");
                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    _logger.LogInformation($"Raw response from {url}: {content}");

                    // Attempt to deserialize only if the content is valid JSON
                    try
                    {
                        var data = JsonSerializer.Deserialize<List<JsonElement>>(content);
                        return data != null && data.Count > 0;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, $"Failed to deserialize response from {url}. Response content: {content}");
                        return false;
                    }
                }
                else
                {
                    _logger.LogError($"API call to {url} failed with status code: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking existence at {url}");
                return false;
            }
        }

        private async Task FetchAndSyncTable(string tableName, string endpoint)
        {
            try
            {
                _logger.LogInformation($"Fetching data from {tableName}...");

                // Fetch data from the local API
                var response = await _httpClient.GetAsync($"{_localApiBaseUrl}api/{tableName}");
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to fetch data from {tableName}");
                    return;
                }

                var content = await response.Content.ReadAsStringAsync();
                var dataList = JsonSerializer.Deserialize<List<JsonElement>>(content);

                if (dataList == null || !dataList.Any())
                {
                    _logger.LogInformation($"No data found in {tableName}");
                    return;
                }

                _logger.LogInformation($"Processing {dataList.Count} records from {tableName}...");

                foreach (var data in dataList)
                {
                    try
                    {
                        // Remove the 'id' field before sending to the central API
                        var dataWithoutId = data; //RemoveIdField(data);

                        // Send data to the central API
                        await SendToCentralApi(endpoint, dataWithoutId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing record from {tableName}");
                    }
                }

                _logger.LogInformation($"Data sync completed for {tableName}.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error fetching and syncing data from {tableName}");
            }
        }

        private async Task SendToCentralApi(string endpoint, JsonElement data)
        {
            try
            {
                _logger.LogInformation($"Sending data to {endpoint}");
                var response = await _httpClient.PostAsync(
                    $"{_centralApiBaseUrl}{endpoint}",
                    new StringContent(data.ToString(), Encoding.UTF8, "application/json")
                );

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to send data to {endpoint}");
                }
                else
                {
                    _logger.LogInformation($"Successfully synced data to {endpoint}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending data to {endpoint}");
            }
        }
        private JsonElement RemoveIdField(JsonElement data)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(data.ToString()))
                {
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        var filteredProperties = new Dictionary<string, object>();

                        foreach (var property in root.EnumerateObject())
                        {
                            if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            // Recursively remove 'id' fields from nested objects
                            if (property.Value.ValueKind == JsonValueKind.Object)
                            {
                                filteredProperties[property.Name] = RemoveIdField(property.Value);
                            }
                            else
                            {
                                filteredProperties[property.Name] = property.Value;
                            }
                        }

                        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                        var filteredJson = JsonSerializer.Serialize(filteredProperties, jsonOptions);
                        return JsonSerializer.Deserialize<JsonElement>(filteredJson);
                    }
                    else if (root.ValueKind == JsonValueKind.Array)
                    {
                        var filteredArray = new List<JsonElement>();

                        foreach (var item in root.EnumerateArray())
                        {
                            filteredArray.Add(RemoveIdField(item));
                        }

                        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                        var filteredJson = JsonSerializer.Serialize(filteredArray, jsonOptions);
                        return JsonSerializer.Deserialize<JsonElement>(filteredJson);
                    }
                    else
                    {
                        return data;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing 'id' field from data");
                return data;
            }
        }
    }
}