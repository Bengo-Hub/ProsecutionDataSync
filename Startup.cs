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
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
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

                    // Always get a fresh token for each sync attempt
                    await RetryWithExponentialBackoff(async () => await LoginAndGetToken(), "LoginAndGetToken", stoppingToken);

                    // Perform the hierarchical sync
                    await RetryWithExponentialBackoff(async () => await SyncDataHierarchically(), "SyncDataHierarchically", stoppingToken);

                    _logger.LogInformation("Data sync process completed successfully. Waiting for next cycle...");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while syncing data");
                }

                // Wait for 5 minutes before checking again
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // This is expected when the service is stopping
                    _logger.LogInformation("Sync service is stopping...");
                    break;
                }
            }
        }

        private async Task SyncDataHierarchically()
        {
            _logger.LogInformation("Fetching and syncing data in hierarchical order...");

            // First, fetch all casedetails that need to be synced
            var caseDetails = await FetchRecordsToSync("casedetails");

            foreach (var caseDetail in caseDetails)
            {
                try
                {
                    // Process the case detail and its entire hierarchy
                    await ProcessCaseHierarchy(caseDetail);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing case hierarchy for caseId: {caseDetail.GetProperty("id")}");
                }
            }

            _logger.LogInformation("Hierarchical data sync completed.");
        }
        private async Task ProcessCaseHierarchy(JsonElement caseDetail)
        {
            // Step 1: Sync the case detail itself
            int newCaseDetailId = await SyncRecord("casedetails", caseDetail);
            if (newCaseDetailId == -1) return; // Skip if failed to sync

            // Step 2: Sync related casedocs
            if (caseDetail.TryGetProperty("id", out var caseDetailId))
            {
                var caseDocs = await FetchChildRecords("casedocs", "casedetailsid", caseDetailId.GetInt32());
                foreach (var caseDoc in caseDocs)
                {
                    // Sync the casedoc
                    int newCaseDocId = await SyncRecord("casedocs", caseDoc, newCaseDetailId, "casedetailsid");
                    if (newCaseDocId == -1) continue;

                    // Step 3: Sync invoicing for this casedoc
                    var invoicings = await FetchChildRecords("invoicing", "casedocsid", caseDoc.GetProperty("id").GetInt32());
                    foreach (var invoicing in invoicings)
                    {
                        // Sync the invoicing
                        int newInvoicingId = await SyncRecord("invoicing", invoicing, newCaseDocId, "casedocsid");
                        if (newInvoicingId == -1) continue;

                        // Step 4: Sync receipts for this invoicing
                        var receipts = await FetchChildRecords("receipt", "invoicingid", invoicing.GetProperty("id").GetInt32());
                        foreach (var receipt in receipts)
                        {
                            await SyncRecord("receipt", receipt, newInvoicingId, "invoicingid");
                        }
                    }
                }
            }

            // Step 5: Sync eacact records (related by caseid)
            if (caseDetail.TryGetProperty("caseid", out var caseId))
            {
                var eacacts = await FetchChildRecords("eacact", "caseid", caseId.GetString());
                foreach (var eacact in eacacts)
                {
                    await SyncRecord("eacact", eacact, caseId.GetString(), "caseid");
                }
            }

            // Step 6: Sync caseresults (related by casedetailsid)
            if (caseDetail.TryGetProperty("id", out var cdId))
            {
                var caseResults = await FetchChildRecords("caseresults", "casedetailsid", cdId.GetInt32());
                foreach (var caseResult in caseResults)
                {
                    await SyncRecord("caseresults", caseResult, newCaseDetailId, "casedetailsid");
                }
            }
        }
        private async Task<int> SyncRecord(string tableName, JsonElement record, dynamic newParentId = null, string foreignKeyField = null)
        {
            try
            {
                // Prepare the data for syncing
                var dataToSend = record;

                // Update foreign key if parent ID is provided
                if (newParentId != null && !string.IsNullOrEmpty(foreignKeyField))
                {
                    dataToSend = UpdateForeignKey(dataToSend, foreignKeyField, newParentId);
                }

                // Clean and remove ID field
                var cleanedData = CleanData(dataToSend);
                var dataWithoutId = RemoveIdField(cleanedData);

                // Skip duplicate checking for caseresults and eacact
                bool shouldCheckDuplicates = !(tableName.Equals("caseresults", StringComparison.OrdinalIgnoreCase) ||
                                            tableName.Equals("eacact", StringComparison.OrdinalIgnoreCase));

                // Check for duplicates (skip for caseresults and eacact)
                if (shouldCheckDuplicates && await IsDuplicateRecord(tableName, dataWithoutId))
                {
                    _logger.LogWarning($"Duplicate record detected in {tableName}. Skipping...");
                    return -1;
                }
                _logger.LogInformation($"Sending api data: {dataWithoutId}");

                // Rest of the method remains the same...
                // Post to central API
                var response = await _httpClient.PostAsync(
                    $"{_centralApiBaseUrl}api/{tableName}",
                    new StringContent(dataWithoutId.ToString(), Encoding.UTF8, "application/json")
                );

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to sync record to {tableName}. Status: {response.StatusCode}");
                    return -1;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var responseData = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (responseData.TryGetProperty("id", out var newId))
                {
                    // Update exported status in local database
                    await UpdateExportedStatus(tableName, record);
                    return newId.GetInt32();
                }

                _logger.LogError($"No ID returned when syncing record to {tableName}");
                return -1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error syncing record to {tableName}");
                return -1;
            }
        }

        private JsonElement UpdateForeignKey(JsonElement data, string foreignKeyField, dynamic newValue)
        {
            using (JsonDocument doc = JsonDocument.Parse(data.ToString()))
            {
                var root = doc.RootElement;
                var updatedProperties = new Dictionary<string, object>();
                _logger.LogInformation($"Mapping FK for {foreignKeyField}");

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name.Equals(foreignKeyField, StringComparison.OrdinalIgnoreCase))
                    {
                        updatedProperties[property.Name] = newValue;
                        _logger.LogInformation($" {property.Name}:{property.Value}<==>{newValue}");
                    }
                    else
                    {
                        updatedProperties[property.Name] = property.Value;
                        _logger.LogInformation($" {property.Name}:{property.Value}");
                    }
                }
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = JsonSerializer.Serialize(updatedProperties, jsonOptions);
                return JsonSerializer.Deserialize<JsonElement>(updatedJson);
            }
        }

        private async Task<List<JsonElement>> FetchRecordsToSync(string tableName)
        {
            _logger.LogInformation($"Fetching {tableName} records to sync...");
            var response = await _httpClient.GetAsync($"{_localApiBaseUrl}api/{tableName}/search?exported=0&hasEacactOrInvoicing=true");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to fetch {tableName} records");
                return new List<JsonElement>();
            }

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<JsonElement>>(content) ?? new List<JsonElement>();
        }

        private async Task<List<JsonElement>> FetchChildRecords(string childTableName, string foreignKey, dynamic foreignKeyValue)
        {
            _logger.LogInformation($"Fetching {childTableName} records for {foreignKey}={foreignKeyValue}...");

            string url;
            if (foreignKeyValue is int)
            {
                url = $"{_localApiBaseUrl}api/{childTableName}/search?{foreignKey}={foreignKeyValue}";
            }
            else
            {
                url = $"{_localApiBaseUrl}api/{childTableName}/search?{foreignKey}={Uri.EscapeDataString(foreignKeyValue)}";
            }

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to fetch {childTableName} records");
                return new List<JsonElement>();
            }

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<List<JsonElement>>(content) ?? new List<JsonElement>();
        }

        private async Task UpdateExportedStatus(string tableName, JsonElement record)
        {
            if (record.TryGetProperty("id", out var id))
            {
                // Create a new JSON object with the exported field set to 1
                var updatedRecord = new Dictionary<string, object>();
                foreach (var property in record.EnumerateObject())
                {
                    updatedRecord[property.Name] = property.Value;
                }
                updatedRecord["exported"] = 1; // Set the exported field to 1

                // Serialize the updated record to JSON
                var jsonContent = JsonSerializer.Serialize(updatedRecord);

                // Send a PUT or PATCH request to update the record
                var updateUrl = $"{_localApiBaseUrl}api/{tableName}/{id.GetInt32()}";
                var response = await _httpClient.PutAsync(updateUrl, new StringContent(jsonContent, Encoding.UTF8, "application/json"));

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to update exported status for record {id.GetInt32()} in {tableName}");
                    throw new Exception($"Failed to update exported status for record {id.GetInt32()} in {tableName}");
                }

                _logger.LogInformation($"Successfully updated exported status for record {id.GetInt32()} in {tableName}");
            }
            else
            {
                _logger.LogError($"Record in {tableName} does not have an 'id' field.");
                throw new Exception($"Record in {tableName} does not have an 'id' field.");
            }
        }

        private async Task<bool> IsDuplicateRecord(string tableName, JsonElement data)
        {
            // Skip duplicate checking for caseresults and eacact
            if (tableName.Equals("caseresults", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("eacact", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string uniqueField = GetUniqueIdentifierField(tableName);

            if (!data.TryGetProperty(uniqueField, out var uniqueValue))
            {
                return false; // If no unique field, assume not duplicate
            }

            // Rest of the duplicate checking logic...
            string checkUrl;
            if (uniqueValue.ValueKind == JsonValueKind.Number)
            {
                checkUrl = $"{_centralApiBaseUrl}api/{tableName}/search?{uniqueField}={uniqueValue.GetInt32()}";
            }
            else
            {
                checkUrl = $"{_centralApiBaseUrl}api/{tableName}/search?{uniqueField}={Uri.EscapeDataString(uniqueValue.GetString())}";
            }

            var response = await _httpClient.GetAsync(checkUrl);

            if (!response.IsSuccessStatusCode)
            {
                return false; // If check fails, assume not duplicate
            }

            var content = await response.Content.ReadAsStringAsync();
            var existingRecords = JsonSerializer.Deserialize<List<JsonElement>>(content);

            return existingRecords != null && existingRecords.Any();
        }
        private string GetUniqueIdentifierField(string tableName)
        {
            switch (tableName.ToLower())
            {
                case "casedetails": return "caseid";
                case "casedocs": return "casedocid";
                case "caseresults": return "casedetailsid";
                case "eacact": return "casedocid";
                case "invoicing": return "invoicingid";
                case "receipt": return "receiptid";
                default: return "id";
            }
        }

        private JsonElement CleanData(JsonElement data)
        {
            var cleanedProperties = new Dictionary<string, object>();

            foreach (var property in data.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object &&
                    property.Value.ValueKind != JsonValueKind.Array)
                {
                    cleanedProperties[property.Name] = property.Value;
                }
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var cleanedJson = JsonSerializer.Serialize(cleanedProperties, jsonOptions);
            return JsonSerializer.Deserialize<JsonElement>(cleanedJson);
        }

        private JsonElement RemoveIdField(JsonElement data)
        {
            using (JsonDocument doc = JsonDocument.Parse(data.ToString()))
            {
                var root = doc.RootElement;
                var filteredProperties = new Dictionary<string, object>();

                foreach (var property in root.EnumerateObject())
                {
                    if (!property.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                    {
                        filteredProperties[property.Name] = property.Value;
                    }
                }

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var filteredJson = JsonSerializer.Serialize(filteredProperties, jsonOptions);
                return JsonSerializer.Deserialize<JsonElement>(filteredJson);
            }
        }

        private async Task RetryWithExponentialBackoff(Func<Task> action, string actionName, CancellationToken stoppingToken, int maxRetryCount = 3)
        {
            int retryCount = 0;
            while (true)
            {
                try
                {
                    await action();
                    break;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount > maxRetryCount)
                    {
                        _logger.LogError(ex, $"Max retry count reached for {actionName}");
                        throw;
                    }

                    int delay = (int)Math.Pow(2, retryCount) * 1000;
                    _logger.LogWarning(ex, $"Error in {actionName}, retrying in {delay}ms...");
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        private async Task<T> RetryWithExponentialBackoff<T>(Func<Task<T>> action, string actionName, CancellationToken stoppingToken, int maxRetryCount = 3)
        {
            int retryCount = 0;
            while (true)
            {
                try
                {
                    return await action();
                }
                catch (Exception ex)
                {
                    retryCount++;
                    if (retryCount > maxRetryCount)
                    {
                        _logger.LogError(ex, $"Max retry count reached for {actionName}");
                        throw;
                    }

                    int delay = (int)Math.Pow(2, retryCount) * 1000;
                    _logger.LogWarning(ex, $"Error in {actionName}, retrying in {delay}ms...");
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        private async Task LoginAndGetToken()
        {
            _logger.LogInformation("Logging in to get JWT token...");
            var loginData = new { email = "admin@admin.com", password = "@Admin123" };

            var response = await _httpClient.PostAsync(
                $"{_localApiBaseUrl}api/authmanagement/login",
                new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json")
            );

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("Failed to get JWT token");
            }

            var result = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<JsonElement>(result);
            _jwtToken = tokenResponse.GetProperty("token").GetString();

            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwtToken);

            _logger.LogInformation("Successfully retrieved JWT token.");
        }
    }
}