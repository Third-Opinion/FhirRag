using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;
using FhirRag.Core.Abstractions;
using FhirRag.Core.Models;
using FhirRag.Core.Security;
using Polly;
using Polly.Extensions.Http;

namespace FhirRag.Infrastructure.Services;

/// <summary>
/// Configuration for AWS Bedrock LLM service
/// </summary>
public class BedrockLlmConfiguration
{
    public string Region { get; set; } = "us-east-1";
    public string DefaultModelId { get; set; } = "anthropic.claude-3-5-sonnet-20241022-v2:0";
    public string EmbeddingModelId { get; set; } = "amazon.titan-embed-text-v2:0";
    public int MaxTokens { get; set; } = 4000;
    public double Temperature { get; set; } = 0.1;
    public double TopP { get; set; } = 0.9;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}

/// <summary>
/// AWS Bedrock-based Large Language Model service
/// </summary>
public class BedrockLlmService : ILlmService
{
    private readonly BedrockLlmConfiguration _configuration;
    private readonly IAmazonBedrockRuntime _bedrockClient;
    private readonly ILogger<BedrockLlmService> _logger;
    private readonly SecurityContextProvider _securityContextProvider;
    private readonly IAsyncPolicy _retryPolicy;

    public BedrockLlmService(
        BedrockLlmConfiguration configuration,
        IAmazonBedrockRuntime bedrockClient,
        ILogger<BedrockLlmService> logger,
        SecurityContextProvider securityContextProvider)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _bedrockClient = bedrockClient ?? throw new ArgumentNullException(nameof(bedrockClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _securityContextProvider = securityContextProvider ?? throw new ArgumentNullException(nameof(securityContextProvider));

        // Configure retry policy with exponential backoff
        _retryPolicy = Policy
            .Handle<AmazonBedrockRuntimeException>()
            .Or<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: _configuration.MaxRetries,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + _configuration.RetryDelay,
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("Bedrock API retry {RetryCount} after {Delay}ms",
                        retryCount, timespan.TotalMilliseconds);
                });
    }

    public async Task<LlmResponse> GenerateResponseAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        
        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Generating LLM response for tenant {TenantId}, model {ModelId}",
                securityContext.TenantId, request.ModelId ?? _configuration.DefaultModelId);

            var modelId = request.ModelId ?? _configuration.DefaultModelId;
            var requestBody = BuildRequestBody(request, modelId);

            var invokeRequest = new InvokeModelRequest
            {
                ModelId = modelId,
                Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(requestBody)),
                ContentType = "application/json"
            };

            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                return await _bedrockClient.InvokeModelAsync(invokeRequest, cancellationToken);
            });

            var responseBody = await ParseResponseAsync(response, modelId);

            var llmResponse = new LlmResponse
            {
                Content = responseBody.Content,
                ModelId = modelId,
                TokensUsed = responseBody.Usage?.TotalTokens ?? 0,
                FinishReason = responseBody.FinishReason ?? "completed",
                Metadata = new Dictionary<string, object>
                {
                    ["input_tokens"] = responseBody.Usage?.InputTokens ?? 0,
                    ["output_tokens"] = responseBody.Usage?.OutputTokens ?? 0,
                    ["model_id"] = modelId,
                    ["tenant_id"] = securityContext.TenantId,
                    ["temperature"] = request.Temperature ?? _configuration.Temperature
                }
            };

            _logger.LogDebug("LLM response generated successfully. Tokens used: {TokensUsed}",
                llmResponse.TokensUsed);

            return llmResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating LLM response for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
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

            var requestBody = new
            {
                inputText = text,
                dimensions = 1024,
                normalize = true
            };

            var invokeRequest = new InvokeModelRequest
            {
                ModelId = _configuration.EmbeddingModelId,
                Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(requestBody)),
                ContentType = "application/json"
            };

            var response = await _retryPolicy.ExecuteAsync(async () =>
            {
                return await _bedrockClient.InvokeModelAsync(invokeRequest, cancellationToken);
            });

            using var responseStream = response.Body;
            var responseJson = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream, cancellationToken: cancellationToken);

            var embeddingArray = responseJson.GetProperty("embedding").EnumerateArray()
                .Select(e => e.GetSingle())
                .ToArray();

            _logger.LogDebug("Embedding generated successfully. Dimensions: {Dimensions}",
                embeddingArray.Length);

            return embeddingArray;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple health check - try to invoke a basic request
            var testRequest = new LlmRequest
            {
                Prompt = "Hello",
                MaxTokens = 10,
                Temperature = 0.1
            };

            var response = await GenerateResponseAsync(testRequest, cancellationToken);
            return !string.IsNullOrEmpty(response.Content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bedrock LLM service health check failed");
            return false;
        }
    }

    private void ValidateRequest(LlmRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt cannot be null or empty", nameof(request));

        if (request.MaxTokens.HasValue && request.MaxTokens.Value <= 0)
            throw new ArgumentException("MaxTokens must be positive", nameof(request));

        if (request.Temperature.HasValue && (request.Temperature.Value < 0 || request.Temperature.Value > 1))
            throw new ArgumentException("Temperature must be between 0 and 1", nameof(request));
    }

    private object BuildRequestBody(LlmRequest request, string modelId)
    {
        // Different models have different request formats
        if (modelId.StartsWith("anthropic.claude"))
        {
            return new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = request.MaxTokens ?? _configuration.MaxTokens,
                temperature = request.Temperature ?? _configuration.Temperature,
                top_p = _configuration.TopP,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = request.Prompt
                    }
                },
                system = request.SystemPrompt ?? "You are a helpful AI assistant specialized in healthcare and FHIR data analysis."
            };
        }
        else if (modelId.StartsWith("amazon.titan"))
        {
            return new
            {
                inputText = request.Prompt,
                textGenerationConfig = new
                {
                    maxTokenCount = request.MaxTokens ?? _configuration.MaxTokens,
                    temperature = request.Temperature ?? _configuration.Temperature,
                    topP = _configuration.TopP,
                    stopSequences = request.StopSequences ?? Array.Empty<string>()
                }
            };
        }
        else
        {
            // Generic format
            return new
            {
                prompt = request.Prompt,
                max_tokens = request.MaxTokens ?? _configuration.MaxTokens,
                temperature = request.Temperature ?? _configuration.Temperature,
                top_p = _configuration.TopP
            };
        }
    }

    private async Task<BedrockResponse> ParseResponseAsync(InvokeModelResponse response, string modelId)
    {
        using var responseStream = response.Body;
        var responseJson = await JsonSerializer.DeserializeAsync<JsonElement>(responseStream);

        if (modelId.StartsWith("anthropic.claude"))
        {
            var content = responseJson.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
            var usage = responseJson.TryGetProperty("usage", out var usageElement) ? 
                new TokenUsage
                {
                    InputTokens = usageElement.GetProperty("input_tokens").GetInt32(),
                    OutputTokens = usageElement.GetProperty("output_tokens").GetInt32(),
                    TotalTokens = usageElement.GetProperty("input_tokens").GetInt32() + usageElement.GetProperty("output_tokens").GetInt32()
                } : null;

            return new BedrockResponse
            {
                Content = content,
                Usage = usage,
                FinishReason = responseJson.GetProperty("stop_reason").GetString()
            };
        }
        else if (modelId.StartsWith("amazon.titan"))
        {
            var results = responseJson.GetProperty("results")[0];
            var content = results.GetProperty("outputText").GetString() ?? string.Empty;
            
            return new BedrockResponse
            {
                Content = content,
                FinishReason = results.GetProperty("completionReason").GetString()
            };
        }
        else
        {
            // Generic parsing
            var content = responseJson.TryGetProperty("text", out var textElement) ? 
                textElement.GetString() ?? string.Empty :
                responseJson.ToString();

            return new BedrockResponse
            {
                Content = content,
                FinishReason = "completed"
            };
        }
    }
}

/// <summary>
/// Response structure for Bedrock models
/// </summary>
internal class BedrockResponse
{
    public string Content { get; set; } = string.Empty;
    public TokenUsage? Usage { get; set; }
    public string? FinishReason { get; set; }
}

/// <summary>
/// Token usage information
/// </summary>
internal class TokenUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
}

/// <summary>
/// Bedrock service health check endpoint
/// </summary>
public class BedrockHealthCheckService : IHealthCheck
{
    private readonly BedrockLlmService _llmService;
    private readonly ILogger<BedrockHealthCheckService> _logger;

    public BedrockHealthCheckService(
        BedrockLlmService llmService,
        ILogger<BedrockHealthCheckService> logger)
    {
        _llmService = llmService ?? throw new ArgumentNullException(nameof(llmService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Performs a comprehensive health check of Bedrock services
    /// </summary>
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var isHealthy = await _llmService.IsHealthyAsync(cancellationToken);
            var duration = DateTime.UtcNow - startTime;

            var data = new Dictionary<string, object>
            {
                ["bedrock_accessible"] = isHealthy,
                ["response_time_ms"] = (int)duration.TotalMilliseconds
            };

            if (isHealthy)
            {
                _logger.LogDebug("Bedrock health check passed in {Duration}ms", (int)duration.TotalMilliseconds);
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Bedrock LLM service is responding correctly", data);
            }
            else
            {
                _logger.LogWarning("Bedrock health check failed");
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Bedrock LLM service is not responding correctly", null, data);
            }
        }
        catch (Exception ex)
        {
            var data = new Dictionary<string, object>
            {
                ["exception"] = ex.GetType().Name
            };
            _logger.LogError(ex, "Bedrock health check threw exception");
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy($"Bedrock health check failed: {ex.Message}", ex, data);
        }
    }
}

/// <summary>
/// Health check result
/// </summary>
public class HealthCheckResult
{
    public string ServiceName { get; set; } = string.Empty;
    public bool IsHealthy { get; set; }
    public DateTime Timestamp { get; set; }
    public int ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Details { get; set; } = new();
}