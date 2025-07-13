using Microsoft.Extensions.Logging;
using System.Text.Json;
using FhirRag.Core.Abstractions;
using FhirRag.Core.Models;
using FhirRag.Core.Security;

namespace FhirRag.Infrastructure.Services;

/// <summary>
/// Configuration for vector embedding service
/// </summary>
public class VectorEmbeddingConfiguration
{
    public string EmbeddingModel { get; set; } = "amazon.titan-embed-text-v2:0";
    public int Dimensions { get; set; } = 1024;
    public bool NormalizeVectors { get; set; } = true;
    public int BatchSize { get; set; } = 100;
    public TimeSpan BatchTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxTextLength { get; set; } = 8192;
    public string TextTruncationStrategy { get; set; } = "end"; // "start", "end", "middle"
}

/// <summary>
/// Vector embedding service for generating and managing embeddings
/// </summary>
public class VectorEmbeddingService : IEmbeddingService
{
    private readonly VectorEmbeddingConfiguration _configuration;
    private readonly ILlmService _llmService;
    private readonly IStorageService _storageService;
    private readonly ILogger<VectorEmbeddingService> _logger;
    private readonly SecurityContextProvider _securityContextProvider;

    public VectorEmbeddingService(
        VectorEmbeddingConfiguration configuration,
        ILlmService llmService,
        IStorageService storageService,
        ILogger<VectorEmbeddingService> logger,
        SecurityContextProvider securityContextProvider)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _securityContextProvider = securityContextProvider ?? throw new ArgumentNullException(nameof(securityContextProvider));
    }

    public async Task<float[]> GenerateTextEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Generating embedding for tenant {TenantId}, text length: {TextLength}",
                securityContext.TenantId, text.Length);

            // Truncate text if it exceeds max length
            var processedText = TruncateText(text);

            // Generate embedding using the LLM service
            var embedding = await _llmService.GenerateEmbeddingAsync(processedText, cancellationToken);

            // Normalize if configured
            if (_configuration.NormalizeVectors)
            {
                embedding = NormalizeVector(embedding);
            }

            _logger.LogDebug("Embedding generated successfully. Dimensions: {Dimensions}",
                embedding.Length);

            return embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<float[]> GenerateStructuredEmbeddingAsync(Dictionary<string, object> structuredData, CancellationToken cancellationToken = default)
    {
        if (structuredData == null || !structuredData.Any())
            throw new ArgumentException("Structured data cannot be null or empty", nameof(structuredData));

        // Convert structured data to text representation
        var textRepresentation = JsonSerializer.Serialize(structuredData, new JsonSerializerOptions { WriteIndented = true });
        return await GenerateTextEmbeddingAsync(textRepresentation, cancellationToken);
    }

    public async Task<float[]> GenerateMultiModalEmbeddingAsync(string text, Dictionary<string, object> structuredData, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text cannot be null or empty", nameof(text));

        if (structuredData == null || !structuredData.Any())
            throw new ArgumentException("Structured data cannot be null or empty", nameof(structuredData));

        // Combine text and structured data
        var structuredText = JsonSerializer.Serialize(structuredData, new JsonSerializerOptions { WriteIndented = true });
        var combinedText = $"Text: {text}\n\nStructured Data:\n{structuredText}";

        return await GenerateTextEmbeddingAsync(combinedText, cancellationToken);
    }

    public float CalculateSimilarity(float[] embedding1, float[] embedding2)
    {
        return (float)CalculateCosineSimilarity(embedding1, embedding2);
    }

    public async Task<IEnumerable<EmbeddingResult>> GenerateBatchEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts == null || !texts.Any())
            throw new ArgumentException("Texts cannot be null or empty", nameof(texts));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            var textList = texts.ToList();
            _logger.LogInformation("Generating batch embeddings for {Count} texts in tenant {TenantId}",
                textList.Count, securityContext.TenantId);

            var results = new List<EmbeddingResult>();
            var batches = textList.Chunk(_configuration.BatchSize);

            foreach (var batch in batches)
            {
                var batchTasks = batch.Select(async (text, index) =>
                {
                    try
                    {
                        var embedding = await GenerateTextEmbeddingAsync(text, cancellationToken);
                        return new EmbeddingResult
                        {
                            Text = text,
                            Embedding = embedding,
                            IsSuccess = true,
                            Dimensions = embedding.Length
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate embedding for text at index {Index}", index);
                        return new EmbeddingResult
                        {
                            Text = text,
                            Embedding = Array.Empty<float>(),
                            IsSuccess = false,
                            ErrorMessage = ex.Message
                        };
                    }
                });

                var batchResults = await Task.WhenAll(batchTasks);
                results.AddRange(batchResults);

                // Add delay between batches to avoid rate limiting
                if (batches.Count() > 1)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            var successCount = results.Count(r => r.IsSuccess);
            _logger.LogInformation("Batch embedding generation completed. Success: {SuccessCount}, Failed: {FailedCount}",
                successCount, results.Count - successCount);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating batch embeddings for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<IEnumerable<SimilarityResult>> FindSimilarAsync(
        float[] queryEmbedding,
        string resourceType,
        int topK = 10,
        double threshold = 0.7,
        CancellationToken cancellationToken = default)
    {
        if (queryEmbedding == null || queryEmbedding.Length == 0)
            throw new ArgumentException("Query embedding cannot be null or empty", nameof(queryEmbedding));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Finding similar vectors for tenant {TenantId}, resource type: {ResourceType}, topK: {TopK}",
                securityContext.TenantId, resourceType, topK);

            // Normalize query embedding if configured
            var normalizedQuery = _configuration.NormalizeVectors ?
                NormalizeVector(queryEmbedding) : queryEmbedding;

            // In a real implementation, this would query a vector database (Pinecone, Weaviate, etc.)
            // For now, we'll return a mock result
            var results = new List<SimilarityResult>();

            // This is where you would integrate with your vector database
            // Example: Query Pinecone, Weaviate, or Amazon OpenSearch with vector search

            _logger.LogDebug("Found {Count} similar vectors above threshold {Threshold}",
                results.Count, threshold);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding similar vectors for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<bool> StoreEmbeddingAsync(
        string resourceId,
        string resourceType,
        float[] embedding,
        Dictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("Resource ID cannot be null or empty", nameof(resourceId));

        if (embedding == null || embedding.Length == 0)
            throw new ArgumentException("Embedding cannot be null or empty", nameof(embedding));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Storing embedding for resource {ResourceId} of type {ResourceType} in tenant {TenantId}",
                resourceId, resourceType, securityContext.TenantId);

            var embeddingData = new EmbeddingData
            {
                ResourceId = resourceId,
                ResourceType = resourceType,
                TenantId = securityContext.TenantId,
                Embedding = embedding,
                Dimensions = embedding.Length,
                Metadata = metadata ?? new Dictionary<string, object>(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            // Store in the storage service
            var key = $"embeddings/{securityContext.TenantId}/{resourceType}/{resourceId}";
            var jsonData = JsonSerializer.SerializeToUtf8Bytes(embeddingData);
            await _storageService.StoreAsync(key, jsonData, cancellationToken: cancellationToken);

            _logger.LogDebug("Embedding stored successfully for resource {ResourceId}", resourceId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing embedding for resource {ResourceId} in tenant {TenantId}",
                resourceId, securityContext.TenantId);
            throw;
        }
    }

    public async Task<float[]?> GetEmbeddingAsync(
        string resourceId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("Resource ID cannot be null or empty", nameof(resourceId));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Retrieving embedding for resource {ResourceId} of type {ResourceType} in tenant {TenantId}",
                resourceId, resourceType, securityContext.TenantId);

            var key = $"embeddings/{securityContext.TenantId}/{resourceType}/{resourceId}";
            var jsonData = await _storageService.RetrieveAsync(key, cancellationToken);

            if (jsonData == null)
            {
                _logger.LogDebug("No embedding found for resource {ResourceId}", resourceId);
                return null;
            }

            var embeddingData = JsonSerializer.Deserialize<EmbeddingData>(jsonData);

            _logger.LogDebug("Embedding retrieved successfully for resource {ResourceId}, dimensions: {Dimensions}",
                resourceId, embeddingData.Dimensions);

            return embeddingData.Embedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving embedding for resource {ResourceId} in tenant {TenantId}",
                resourceId, securityContext.TenantId);
            throw;
        }
    }

    private string TruncateText(string text)
    {
        if (text.Length <= _configuration.MaxTextLength)
            return text;

        _logger.LogDebug("Truncating text from {OriginalLength} to {MaxLength} characters",
            text.Length, _configuration.MaxTextLength);

        return _configuration.TextTruncationStrategy switch
        {
            "start" => text.Substring(text.Length - _configuration.MaxTextLength),
            "middle" => TruncateMiddle(text),
            "end" => text.Substring(0, _configuration.MaxTextLength),
            _ => text.Substring(0, _configuration.MaxTextLength)
        };
    }

    private string TruncateMiddle(string text)
    {
        var halfLength = _configuration.MaxTextLength / 2;
        var start = text.Substring(0, halfLength);
        var end = text.Substring(text.Length - halfLength);
        return start + "..." + end;
    }

    private float[] NormalizeVector(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(x => x * x));

        if (magnitude == 0)
            return vector;

        return vector.Select(x => (float)(x / magnitude)).ToArray();
    }

    /// <summary>
    /// Calculates cosine similarity between two vectors
    /// </summary>
    public static double CalculateCosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length)
            throw new ArgumentException("Vectors must have the same dimensions");

        var dotProduct = vectorA.Zip(vectorB, (a, b) => a * b).Sum();
        var magnitudeA = Math.Sqrt(vectorA.Sum(x => x * x));
        var magnitudeB = Math.Sqrt(vectorB.Sum(x => x * x));

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }
}

/// <summary>
/// Result of embedding generation
/// </summary>
public class EmbeddingResult
{
    public string Text { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public bool IsSuccess { get; set; }
    public int Dimensions { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Result of similarity search
/// </summary>
public class SimilarityResult
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public double SimilarityScore { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Embedding data storage model
/// </summary>
public class EmbeddingData
{
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int Dimensions { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}