using Amazon.S3;
using Amazon.S3.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using FhirRag.Core.Abstractions;
using FhirRag.Core.Security;
using Polly;

namespace FhirRag.Infrastructure.Services;

/// <summary>
/// Configuration for AWS storage service
/// </summary>
public class AwsStorageConfiguration
{
    public string Region { get; set; } = "us-east-1";
    public string S3BucketName { get; set; } = "fhir-rag-storage";
    public string DynamoDbTableName { get; set; } = "fhir-rag-metadata";
    public bool UseServerSideEncryption { get; set; } = true;
    public string ServerSideEncryptionMethod { get; set; } = "AES256";
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}

/// <summary>
/// AWS-based storage service using S3 for large objects and DynamoDB for metadata
/// </summary>
public class AwsStorageService : IStorageService
{
    private readonly AwsStorageConfiguration _configuration;
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly Table _dynamoTable;
    private readonly ILogger<AwsStorageService> _logger;
    private readonly SecurityContextProvider _securityContextProvider;
    private readonly IAsyncPolicy _retryPolicy;

    public AwsStorageService(
        AwsStorageConfiguration configuration,
        IAmazonS3 s3Client,
        IAmazonDynamoDB dynamoDbClient,
        ILogger<AwsStorageService> logger,
        SecurityContextProvider securityContextProvider)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
        _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _securityContextProvider = securityContextProvider ?? throw new ArgumentNullException(nameof(securityContextProvider));

        _dynamoTable = (Table)Table.LoadTable(_dynamoDbClient, _configuration.DynamoDbTableName, DynamoDBEntryConversion.V2);

        _retryPolicy = Policy
            .Handle<AmazonS3Exception>()
            .Or<AmazonDynamoDBException>()
            .WaitAndRetryAsync(
                retryCount: _configuration.MaxRetries,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + _configuration.RetryDelay,
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("AWS storage retry {RetryCount} after {Delay}ms",
                        retryCount, timespan.TotalMilliseconds);
                });
    }

    public async Task<bool> StoreAsync(string key, byte[] data, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        if (data == null || data.Length == 0)
            throw new ArgumentException("Data cannot be null or empty", nameof(data));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Storing data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);

            var tenantKey = GetTenantKey(key, securityContext.TenantId);
            var serializedData = System.Text.Encoding.UTF8.GetString(data);

            // Store in S3
            var s3Key = $"{securityContext.TenantId}/{key}";
            await StoreInS3Async(s3Key, serializedData, cancellationToken);

            // Store metadata in DynamoDB
            var storageMetadata = new StorageMetadata
            {
                Key = tenantKey,
                S3Key = s3Key,
                TenantId = securityContext.TenantId,
                DataType = "byte[]",
                SizeBytes = data.Length,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                CreatedBy = securityContext.UserId
            };

            // Add custom metadata if provided
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    storageMetadata.Tags[kvp.Key] = kvp.Value;
                }
            }

            await StoreMetadataAsync(storageMetadata, cancellationToken);

            _logger.LogDebug("Data stored successfully with key {Key}, size: {Size} bytes",
                key, storageMetadata.SizeBytes);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);
            throw;
        }
    }

    public async Task<byte[]?> RetrieveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Retrieving data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);

            var tenantKey = GetTenantKey(key, securityContext.TenantId);

            // Get metadata from DynamoDB
            var metadata = await GetMetadataAsync(tenantKey, cancellationToken);
            if (metadata == null)
            {
                _logger.LogDebug("No data found for key {Key}", key);
                return null;
            }

            // Get data from S3
            var data = await GetFromS3Async(metadata.S3Key, cancellationToken);
            if (string.IsNullOrEmpty(data))
            {
                _logger.LogWarning("Metadata found but no data in S3 for key {Key}", key);
                return null;
            }

            var byteData = System.Text.Encoding.UTF8.GetBytes(data);

            _logger.LogDebug("Data retrieved successfully for key {Key}, size: {Size} bytes",
                key, metadata.SizeBytes);

            return byteData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Deleting data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);

            var tenantKey = GetTenantKey(key, securityContext.TenantId);

            // Get metadata to find S3 key
            var metadata = await GetMetadataAsync(tenantKey, cancellationToken);
            if (metadata == null)
            {
                _logger.LogDebug("No data found to delete for key {Key}", key);
                return false;
            }

            // Delete from S3
            await DeleteFromS3Async(metadata.S3Key, cancellationToken);

            // Delete metadata from DynamoDB
            await DeleteMetadataAsync(tenantKey, cancellationToken);

            _logger.LogDebug("Data deleted successfully for key {Key}", key);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting data with key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            var tenantKey = GetTenantKey(key, securityContext.TenantId);
            var metadata = await GetMetadataAsync(tenantKey, cancellationToken);
            return metadata != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking existence of key {Key} for tenant {TenantId}",
                key, securityContext.TenantId);
            throw;
        }
    }

    public async Task<IEnumerable<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Listing keys with prefix '{Prefix}' for tenant {TenantId}",
                prefix ?? string.Empty, securityContext.TenantId);

            var tenantPrefix = !string.IsNullOrEmpty(prefix) ? GetTenantKey(prefix, securityContext.TenantId) : string.Empty;

            // Query DynamoDB for metadata
            var scanFilter = new ScanFilter();
            scanFilter.AddCondition("TenantId", ScanOperator.Equal, securityContext.TenantId);

            if (!string.IsNullOrEmpty(prefix))
            {
                scanFilter.AddCondition("Key", ScanOperator.BeginsWith, tenantPrefix);
            }

            var search = _dynamoTable.Scan(scanFilter);
            var keys = new List<string>();

            do
            {
                var documents = await search.GetNextSetAsync(cancellationToken);
                foreach (var doc in documents)
                {
                    var metadata = StorageMetadata.FromDocument(doc);
                    if (metadata.TenantId == securityContext.TenantId)
                    {
                        // Remove tenant prefix from key
                        var originalKey = metadata.Key.Substring($"tenant:{securityContext.TenantId}:".Length);
                        keys.Add(originalKey);
                    }
                }
            } while (!search.IsDone);

            _logger.LogDebug("Found {Count} keys with prefix '{Prefix}' for tenant {TenantId}",
                keys.Count, prefix, securityContext.TenantId);

            return keys;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing keys with prefix '{Prefix}' for tenant {TenantId}",
                prefix, securityContext.TenantId);
            throw;
        }
    }

    private async Task StoreInS3Async(string s3Key, string data, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = _configuration.S3BucketName,
            Key = s3Key,
            ContentBody = data,
            ContentType = "application/json"
        };

        if (_configuration.UseServerSideEncryption)
        {
            request.ServerSideEncryptionMethod = _configuration.ServerSideEncryptionMethod == "AES256" ?
                ServerSideEncryptionMethod.AES256 : ServerSideEncryptionMethod.AWSKMS;
        }

        await _retryPolicy.ExecuteAsync(async () =>
            await _s3Client.PutObjectAsync(request, cancellationToken));
    }

    private async Task<string?> GetFromS3Async(string s3Key, CancellationToken cancellationToken)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _configuration.S3BucketName,
                Key = s3Key
            };

            var response = await _retryPolicy.ExecuteAsync(async () =>
                await _s3Client.GetObjectAsync(request, cancellationToken));

            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            return null;
        }
    }

    private async Task DeleteFromS3Async(string s3Key, CancellationToken cancellationToken)
    {
        var request = new DeleteObjectRequest
        {
            BucketName = _configuration.S3BucketName,
            Key = s3Key
        };

        await _retryPolicy.ExecuteAsync(async () =>
            await _s3Client.DeleteObjectAsync(request, cancellationToken));
    }

    private async Task StoreMetadataAsync(StorageMetadata metadata, CancellationToken cancellationToken)
    {
        var document = metadata.ToDocument();
        await _retryPolicy.ExecuteAsync(async () =>
            await _dynamoTable.PutItemAsync(document, cancellationToken));
    }

    private async Task<StorageMetadata?> GetMetadataAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var document = await _retryPolicy.ExecuteAsync(async () =>
                await _dynamoTable.GetItemAsync(key, cancellationToken));

            return document != null ? StorageMetadata.FromDocument(document) : null;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    private async Task DeleteMetadataAsync(string key, CancellationToken cancellationToken)
    {
        await _retryPolicy.ExecuteAsync(async () =>
            await _dynamoTable.DeleteItemAsync(key, cancellationToken));
    }

    private string GetTenantKey(string key, string tenantId)
    {
        return $"tenant:{tenantId}:{key}";
    }
}

/// <summary>
/// Storage metadata for DynamoDB
/// </summary>
public class StorageMetadata
{
    public string Key { get; set; } = string.Empty;
    public string S3Key { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public Dictionary<string, object> Tags { get; set; } = new();

    /// <summary>
    /// Converts to DynamoDB document
    /// </summary>
    public Document ToDocument()
    {
        var document = new Document
        {
            ["Key"] = Key,
            ["S3Key"] = S3Key,
            ["TenantId"] = TenantId,
            ["DataType"] = DataType,
            ["SizeBytes"] = SizeBytes,
            ["CreatedAt"] = CreatedAt,
            ["UpdatedAt"] = UpdatedAt,
            ["CreatedBy"] = CreatedBy
        };

        if (Tags.Any())
        {
            document["Tags"] = JsonSerializer.Serialize(Tags);
        }

        return document;
    }

    /// <summary>
    /// Creates from DynamoDB document
    /// </summary>
    public static StorageMetadata FromDocument(Document document)
    {
        var metadata = new StorageMetadata
        {
            Key = document["Key"],
            S3Key = document["S3Key"],
            TenantId = document["TenantId"],
            DataType = document["DataType"],
            SizeBytes = (long)document["SizeBytes"],
            CreatedAt = (DateTime)document["CreatedAt"],
            UpdatedAt = (DateTime)document["UpdatedAt"],
            CreatedBy = document["CreatedBy"]
        };

        if (document.TryGetValue("Tags", out var tagsValue) && !string.IsNullOrEmpty(tagsValue))
        {
            try
            {
                metadata.Tags = JsonSerializer.Deserialize<Dictionary<string, object>>(tagsValue.ToString()!) ?? new();
            }
            catch (JsonException)
            {
                metadata.Tags = new Dictionary<string, object>();
            }
        }

        return metadata;
    }
}