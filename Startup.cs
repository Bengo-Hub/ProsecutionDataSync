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
        private readonly string _localApiBaseUrl = "http://localhost:4444/";
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
                    await RetryWithExponentialBackoff(async () => await LoginAndGetToken(), "LoginAndGetToken", stoppingToken);
                    await RetryWithExponentialBackoff(async () => await SyncData(), "SyncData", stoppingToken);
                    _logger.LogInformation("Data sync process completed successfully.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while syncing data");
                }

                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Sync every 5 minutes
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
                        break; // Exit loop
                    }

                    int delay = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff
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

                    int delay = (int)Math.Pow(2, retryCount) * 1000; // Exponential backoff
                    _logger.LogWarning(ex, $"Error in {actionName}, retrying in {delay}ms...");
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }

        private async Task LoginAndGetToken()
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
                throw new Exception("Failed to get JWT token");
            }
        }

        private async Task SyncData()
        {
            _logger.LogInformation("Fetching and syncing data...");

            // Start with the root table (casedetails)
            await RetryWithExponentialBackoff(async () => await FetchAndSyncTable("casedetails", "api/casedetails"), "FetchAndSyncTable-casedetails", CancellationToken.None);

            _logger.LogInformation("Data sync completed for all tables.");
        }

        private async Task FetchAndSyncTable(string tableName, string endpoint, int limit = 1, int offset = 0)
        {
            while (true)
            {
                _logger.LogInformation($"Fetching data from {tableName} with offset {offset}...");

                string filterQuery = $"?exported=0&limit={limit}&offset={offset}";
                switch (tableName.ToLower())
                {
                    case "invoicing":
                        filterQuery += "&deleted=0";
                        break;
                    case "casedocs":
                        filterQuery += "&cancelled=N";
                        break;
                }

                var response = await _httpClient.GetAsync($"{_localApiBaseUrl}api/{tableName}/search/{filterQuery}");
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to fetch data from {tableName}");
                    throw new Exception($"Failed to fetch data from {tableName}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var dataList = JsonSerializer.Deserialize<List<JsonElement>>(content);

                if (dataList == null || !dataList.Any())
                {
                    _logger.LogInformation($"No more data found in {tableName}.");
                    break;
                }

                _logger.LogInformation($"Processing {dataList.Count} records from {tableName}...");

                foreach (var data in dataList)
                {
                    try
                    {
                        // Sync the parent record and all its children recursively
                        await SyncRecordAndChildren(tableName, endpoint, data);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing record from {tableName}");
                    }
                }

                offset += limit;
            }

            _logger.LogInformation($"Data sync completed for {tableName}.");
        }

        private async Task SyncRecordAndChildren(string tableName, string endpoint, JsonElement data)
        {
            // First check if this record should be synced based on business rules
            if (!await ShouldSyncRecord(tableName, data))
            {
                _logger.LogInformation($"Skipping {tableName} record as it doesn't meet sync criteria");
                return;
            }

            // Send the parent record to the central API and get the new ID
            int newParentId = await RetryWithExponentialBackoff<int>(
                async () => await SendToCentralApi(endpoint, ParseToJsonElement(data)),
                $"SendToCentralApi-{endpoint}",
                CancellationToken.None
            );

            if (newParentId == -1)
            {
                return; // Skip if the parent record was not posted
            }

            // Get all child records for this parent
            var childrenToSync = GetChildTablesToSync(tableName);

            foreach (var childTable in childrenToSync)
            {
                var childRecords = await FetchChildRecords(childTable.TableName, childTable.ForeignKey,
                    childTable.IsForeignKeyInt ? (object)data.GetProperty(childTable.LocalKey).GetInt32() :
                                               data.GetProperty(childTable.LocalKey).GetString());

                if (childRecords.Any())
                {
                    _logger.LogInformation($"Found {childRecords.Count} child records in {childTable.TableName} for parent {tableName}");

                    foreach (var childRecord in childRecords)
                    {
                        // Recursively sync each child record and its children
                        await SyncRecordAndChildren(
                            childTable.TableName,
                            $"api/{childTable.TableName}",
                            UpdateForeignKey(childRecord, tableName, newParentId, data)
                        );
                    }
                }
            }
        }

        private async Task<bool> ShouldSyncRecord(string tableName, JsonElement data)
        {
            // Apply table-specific sync rules
            switch (tableName.ToLower())
            {
                case "casedetails":
                    // Only sync casedetails if it has corresponding eacact or invoicing records
                    var hasEacact = (await FetchChildRecords("eacact", "caseid", data.GetProperty("caseid").GetString())).Any();
                    var hasInvoicing = (await FetchChildRecords("invoicing", "caseid", data.GetProperty("caseid").GetString())).Any();
                    return hasEacact || hasInvoicing;

                case "invoicing":
                    // Only send invoices whose parent casedocs cancelled status is 'N'
                    return await IsParentCasedocsNotCancelled(data);

                default:
                    return true;
            }
        }

        private List<ChildTableInfo> GetChildTablesToSync(string parentTableName)
        {
            var children = new List<ChildTableInfo>();

            switch (parentTableName.ToLower())
            {
                case "casedetails":
                    children.Add(new ChildTableInfo("casedocs", "casedetailsid", "id", false));
                    children.Add(new ChildTableInfo("caseresults", "casedetailsid", "id", false));
                    children.Add(new ChildTableInfo("eacact", "caseid", "caseid", false));
                    children.Add(new ChildTableInfo("invoicing", "caseid", "caseid", false));
                    break;

                case "casedocs":
                    children.Add(new ChildTableInfo("invoicing", "casedocsid", "id", true));
                    break;

                case "invoicing":
                    children.Add(new ChildTableInfo("receipt", "invoicingid", "id", true));
                    break;
            }

            return children;
        }

        private async Task<int> SendToCentralApi(string endpoint, JsonElement data)
        {
            var tableName = endpoint.Split("/")[1];
            var cleanedData = CleanData(data);
            var dataWithoutId = RemoveIdField(cleanedData);

            _logger.LogInformation($"Sending cleaned data to {endpoint}: {dataWithoutId}");

            if (await IsDuplicateRecord(endpoint, dataWithoutId))
            {
                _logger.LogWarning($"Duplicate record detected at {endpoint}. Skipping...");
                return -1;
            }

            var response = await _httpClient.PostAsync(
                $"{_centralApiBaseUrl}{endpoint}",
                new StringContent(dataWithoutId.ToString(), Encoding.UTF8, "application/json")
            );

            var responseContent = await response.Content.ReadAsStringAsync();
            var responseData = JsonSerializer.Deserialize<JsonElement>(responseContent);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    _logger.LogWarning($"Duplicate record detected at {endpoint}. Skipping...");
                    return -1;
                }

                _logger.LogError($"Failed to send data to {endpoint}");
                _logger.LogInformation($"Error Response from {endpoint}: {responseData}");
                throw new Exception($"Failed to send data to {endpoint}");
            }

            _logger.LogInformation($"Response from {endpoint}: {responseData}");

            if (responseData.TryGetProperty("id", out var newId))
            {
                await UpdateExportedStatus(tableName, data);
                _logger.LogInformation($"Successfully synced data to {endpoint} with new ID: {newId.GetInt32()}");
                return newId.GetInt32();
            }

            _logger.LogError($"No 'id' field found in the response from {endpoint}");
            throw new Exception($"No 'id' field found in the response from {endpoint}");
        }

        private async Task<bool> IsDuplicateRecord(string endpoint, JsonElement data)
        {
            var tableName = endpoint.Split("/")[1];
            string uniqueIdentifierField = GetUniqueIdentifierField(tableName);

            if (data.TryGetProperty(uniqueIdentifierField, out var uniqueIdentifier))
            {
                string checkUrl = $"{_centralApiBaseUrl}api/{tableName}/search?{uniqueIdentifierField}=" +
                    (uniqueIdentifier.ValueKind == JsonValueKind.Number ?
                     uniqueIdentifier.GetInt32().ToString() :
                     uniqueIdentifier.GetString());

                var checkResponse = await _httpClient.GetAsync(checkUrl);

                if (checkResponse.IsSuccessStatusCode)
                {
                    var checkContent = await checkResponse.Content.ReadAsStringAsync();
                    var existingRecords = JsonSerializer.Deserialize<List<JsonElement>>(checkContent);
                    return existingRecords != null && existingRecords.Any();
                }
            }
            return false;
        }

        private JsonElement UpdateForeignKey(JsonElement data, string parentTableName, int newParentId, JsonElement parentRecord)
        {
            using (JsonDocument doc = JsonDocument.Parse(data.ToString()))
            {
                var root = doc.RootElement;
                var updatedProperties = new Dictionary<string, object>();

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name.Equals($"{parentTableName}id", StringComparison.OrdinalIgnoreCase))
                    {
                        updatedProperties[property.Name] = newParentId;
                        _logger.LogInformation($"Updated child {property.Name}:{property.Value} ==> {newParentId}");
                    }
                    else
                    {
                        updatedProperties[property.Name] = property.Value;
                    }
                }

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = JsonSerializer.Serialize(updatedProperties, jsonOptions);
                return JsonSerializer.Deserialize<JsonElement>(updatedJson);
            }
        }

        private async Task UpdateExportedStatus(string tableName, JsonElement record)
        {
            if (record.TryGetProperty("id", out var id))
            {
                var updatedRecord = new Dictionary<string, object>();
                foreach (var property in record.EnumerateObject())
                {
                    updatedRecord[property.Name] = property.Value;
                }
                updatedRecord["exported"] = 1;

                var jsonContent = JsonSerializer.Serialize(updatedRecord);
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

        private async Task<List<JsonElement>> FetchChildRecords(string childTableName, string foreignKey, dynamic foreignKeyValue)
        {
            _logger.LogInformation($"Fetching related {childTableName} records for {foreignKey} = {foreignKeyValue}...");

            var response = await _httpClient.GetAsync($"{_localApiBaseUrl}api/{childTableName}/search?{foreignKey}={foreignKeyValue}&exported=0");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Failed to fetch related {childTableName} records");
                return new List<JsonElement>();
            }

            var content = await response.Content.ReadAsStringAsync();
            var dataList = JsonSerializer.Deserialize<List<JsonElement>>(content);

            return dataList ?? new List<JsonElement>();
        }

        private async Task<bool> IsParentCasedocsNotCancelled(JsonElement invoicingRecord)
        {
            if (invoicingRecord.TryGetProperty("casedocsid", out var caseId))
            {
                var casedocs = await FetchChildRecords("casedocs", "id", caseId.GetInt32());
                return casedocs.Any() && casedocs.All(doc => doc.TryGetProperty("cancelled", out var cancelled) && cancelled.GetString() == "N");
            }
            return false;
        }

        private JsonElement ParseToJsonElement(object data)
        {
            var jsonString = JsonSerializer.Serialize(data);
            using (JsonDocument doc = JsonDocument.Parse(jsonString))
            {
                return doc.RootElement.Clone();
            }
        }

        private JsonElement RemoveIdField(JsonElement data, string tableName = null)
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

                            if (property.Value.ValueKind == JsonValueKind.Object)
                            {
                                filteredProperties[property.Name] = RemoveIdField(property.Value, tableName);
                            }
                            else if (property.Value.ValueKind == JsonValueKind.Array)
                            {
                                var filteredArray = new List<JsonElement>();
                                foreach (var item in property.Value.EnumerateArray())
                                {
                                    filteredArray.Add(RemoveIdField(item, tableName));
                                }
                                filteredProperties[property.Name] = filteredArray;
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
                            filteredArray.Add(RemoveIdField(item, tableName));
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

        private JsonElement CleanData(JsonElement data)
        {
            var cleanedProperties = new Dictionary<string, object>();

            foreach (var property in data.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object && property.Value.ValueKind != JsonValueKind.Array)
                {
                    cleanedProperties[property.Name] = property.Value;
                }
            }

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            var cleanedJson = JsonSerializer.Serialize(cleanedProperties, jsonOptions);
            return JsonSerializer.Deserialize<JsonElement>(cleanedJson);
        }

        private string GetUniqueIdentifierField(string tableName)
        {
            switch (tableName.ToLower())
            {
                case "casedetails":
                    return "caseticket";
                case "casedocs":
                    return "casedocid";
                case "caseresults":
                    return "casedetailsid";
                case "eacact":
                    return "casedocid";
                case "invoicing":
                    return "invoicingid";
                case "receipt":
                    return "receiptid";
                default:
                    throw new Exception($"Unknown table name: {tableName}");
            }
        }

        private class ChildTableInfo
        {
            public string TableName { get; }
            public string ForeignKey { get; }
            public string LocalKey { get; }
            public bool IsForeignKeyInt { get; }

            public ChildTableInfo(string tableName, string foreignKey, string localKey, bool isForeignKeyInt)
            {
                TableName = tableName;
                ForeignKey = foreignKey;
                LocalKey = localKey;
                IsForeignKeyInt = isForeignKeyInt;
            }
        }
    }
}