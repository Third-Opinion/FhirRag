using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using FhirRag.Core.Abstractions;
using FhirRag.Core.Models;
using FhirRag.Core.Security;
using Polly;

namespace FhirRag.Infrastructure.Services;

/// <summary>
/// Configuration for Lambda orchestration service
/// </summary>
public class LambdaOrchestrationConfiguration
{
    public string Region { get; set; } = "us-east-1";
    public string FhirProcessingFunctionName { get; set; } = "fhir-rag-processor";
    public string EmbeddingFunctionName { get; set; } = "fhir-rag-embeddings";
    public string QueryFunctionName { get; set; } = "fhir-rag-query";
    public string DeadLetterQueueUrl { get; set; } = string.Empty;
    public string ProcessingQueueUrl { get; set; } = string.Empty;
    public int MaxRetries { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan LambdaTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
}

/// <summary>
/// AWS Lambda-based orchestration service for FHIR processing workflows
/// </summary>
public class LambdaOrchestrationService : IOrchestrationService
{
    private readonly LambdaOrchestrationConfiguration _configuration;
    private readonly IAmazonLambda _lambdaClient;
    private readonly IAmazonSQS _sqsClient;
    private readonly ILogger<LambdaOrchestrationService> _logger;
    private readonly SecurityContextProvider _securityContextProvider;
    private readonly IAsyncPolicy _retryPolicy;

    public LambdaOrchestrationService(
        LambdaOrchestrationConfiguration configuration,
        IAmazonLambda lambdaClient,
        IAmazonSQS sqsClient,
        ILogger<LambdaOrchestrationService> logger,
        SecurityContextProvider securityContextProvider)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _lambdaClient = lambdaClient ?? throw new ArgumentNullException(nameof(lambdaClient));
        _sqsClient = sqsClient ?? throw new ArgumentNullException(nameof(sqsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _securityContextProvider = securityContextProvider ?? throw new ArgumentNullException(nameof(securityContextProvider));

        _retryPolicy = Policy
            .Handle<AmazonLambdaException>()
            .Or<AmazonSQSException>()
            .WaitAndRetryAsync(
                retryCount: _configuration.MaxRetries,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)) + _configuration.RetryDelay,
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("Lambda orchestration retry {RetryCount} after {Delay}ms",
                        retryCount, timespan.TotalMilliseconds);
                });
    }

    public async Task<OrchestrationResult> StartWorkflowAsync(WorkflowRequest request, CancellationToken cancellationToken = default)
    {
        ValidateWorkflowRequest(request);

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogInformation("Starting workflow {WorkflowType} for tenant {TenantId}, resource {ResourceId}",
                request.WorkflowType, securityContext.TenantId, request.ResourceId);

            var workflowId = Guid.NewGuid().ToString();
            var orchestrationContext = new OrchestrationContext
            {
                WorkflowId = workflowId,
                TenantId = securityContext.TenantId,
                UserId = securityContext.UserId,
                WorkflowType = request.WorkflowType,
                ResourceId = request.ResourceId,
                ResourceType = request.ResourceType,
                Parameters = request.Parameters,
                StartedAt = DateTime.UtcNow,
                Status = "initiated"
            };

            // Queue initial processing step
            await QueueWorkflowStepAsync(orchestrationContext, "initialize", cancellationToken);

            var result = new OrchestrationResult
            {
                WorkflowId = workflowId,
                Status = "initiated",
                StartedAt = DateTime.UtcNow,
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep
                    {
                        Name = "initialize",
                        Status = "queued",
                        QueuedAt = DateTime.UtcNow
                    }
                }
            };

            _logger.LogDebug("Workflow {WorkflowId} initiated successfully", workflowId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting workflow for tenant {TenantId}",
                securityContext.TenantId);
            throw;
        }
    }

    public async Task<OrchestrationResult> GetWorkflowStatusAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new ArgumentException("Workflow ID cannot be null or empty", nameof(workflowId));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogDebug("Getting workflow status for {WorkflowId} in tenant {TenantId}",
                workflowId, securityContext.TenantId);

            // In a real implementation, this would query a state store (DynamoDB, etc.)
            // For now, we'll return a mock result
            var result = new OrchestrationResult
            {
                WorkflowId = workflowId,
                Status = "running",
                StartedAt = DateTime.UtcNow.AddMinutes(-5),
                Steps = new List<WorkflowStep>
                {
                    new WorkflowStep
                    {
                        Name = "initialize",
                        Status = "completed",
                        QueuedAt = DateTime.UtcNow.AddMinutes(-5),
                        StartedAt = DateTime.UtcNow.AddMinutes(-4),
                        CompletedAt = DateTime.UtcNow.AddMinutes(-3)
                    },
                    new WorkflowStep
                    {
                        Name = "process_fhir_data",
                        Status = "running",
                        QueuedAt = DateTime.UtcNow.AddMinutes(-3),
                        StartedAt = DateTime.UtcNow.AddMinutes(-2)
                    }
                }
            };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting workflow status for {WorkflowId}", workflowId);
            throw;
        }
    }

    public async Task<bool> CancelWorkflowAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new ArgumentException("Workflow ID cannot be null or empty", nameof(workflowId));

        var securityContext = _securityContextProvider.Current;
        if (securityContext == null)
            throw new UnauthorizedAccessException("Security context is required");

        try
        {
            _logger.LogInformation("Cancelling workflow {WorkflowId} for tenant {TenantId}",
                workflowId, securityContext.TenantId);

            // Send cancellation message to processing queue
            await SendCancellationMessageAsync(workflowId, cancellationToken);

            _logger.LogDebug("Workflow {WorkflowId} cancellation requested", workflowId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling workflow {WorkflowId}", workflowId);
            throw;
        }
    }

    private async Task QueueWorkflowStepAsync(OrchestrationContext context, string stepName, CancellationToken cancellationToken)
    {
        var message = new WorkflowStepMessage
        {
            WorkflowId = context.WorkflowId,
            TenantId = context.TenantId,
            UserId = context.UserId,
            StepName = stepName,
            WorkflowType = context.WorkflowType,
            ResourceId = context.ResourceId,
            ResourceType = context.ResourceType,
            Parameters = context.Parameters,
            QueuedAt = DateTime.UtcNow
        };

        var messageBody = JsonSerializer.Serialize(message);

        await _retryPolicy.ExecuteAsync(async () =>
        {
            await _sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _configuration.ProcessingQueueUrl,
                MessageBody = messageBody,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["WorkflowId"] = new MessageAttributeValue { StringValue = context.WorkflowId, DataType = "String" },
                    ["TenantId"] = new MessageAttributeValue { StringValue = context.TenantId, DataType = "String" },
                    ["StepName"] = new MessageAttributeValue { StringValue = stepName, DataType = "String" },
                    ["WorkflowType"] = new MessageAttributeValue { StringValue = context.WorkflowType, DataType = "String" }
                }
            }, cancellationToken);
        });

        _logger.LogDebug("Queued workflow step {StepName} for workflow {WorkflowId}",
            stepName, context.WorkflowId);
    }

    private async Task SendCancellationMessageAsync(string workflowId, CancellationToken cancellationToken)
    {
        var cancellationMessage = new
        {
            MessageType = "cancellation",
            WorkflowId = workflowId,
            RequestedAt = DateTime.UtcNow
        };

        var messageBody = JsonSerializer.Serialize(cancellationMessage);

        await _retryPolicy.ExecuteAsync(async () =>
        {
            await _sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _configuration.ProcessingQueueUrl,
                MessageBody = messageBody,
                MessageAttributes = new Dictionary<string, MessageAttributeValue>
                {
                    ["MessageType"] = new MessageAttributeValue { StringValue = "cancellation", DataType = "String" },
                    ["WorkflowId"] = new MessageAttributeValue { StringValue = workflowId, DataType = "String" }
                }
            }, cancellationToken);
        });
    }

    private void ValidateWorkflowRequest(WorkflowRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.WorkflowType))
            throw new ArgumentException("Workflow type cannot be null or empty", nameof(request));

        if (string.IsNullOrWhiteSpace(request.ResourceId))
            throw new ArgumentException("Resource ID cannot be null or empty", nameof(request));
    }
}

/// <summary>
/// Internal orchestration context
/// </summary>
internal class OrchestrationContext
{
    public string WorkflowId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Message format for workflow steps
/// </summary>
internal class WorkflowStepMessage
{
    public string WorkflowId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string WorkflowType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
    public DateTime QueuedAt { get; set; }
}

/// <summary>
/// Lambda function invoker for specific processing tasks
/// </summary>
public class LambdaFunctionInvoker
{
    private readonly IAmazonLambda _lambdaClient;
    private readonly ILogger<LambdaFunctionInvoker> _logger;
    private readonly IAsyncPolicy _retryPolicy;

    public LambdaFunctionInvoker(
        IAmazonLambda lambdaClient,
        ILogger<LambdaFunctionInvoker> logger)
    {
        _lambdaClient = lambdaClient ?? throw new ArgumentNullException(nameof(lambdaClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _retryPolicy = Policy
            .Handle<AmazonLambdaException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    _logger.LogWarning("Lambda invocation retry {RetryCount} after {Delay}ms",
                        retryCount, timespan.TotalMilliseconds);
                });
    }

    /// <summary>
    /// Invokes a Lambda function with the specified payload
    /// </summary>
    public async Task<LambdaInvocationResult> InvokeAsync(
        string functionName,
        object payload,
        bool isAsync = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new ArgumentException("Function name cannot be null or empty", nameof(functionName));

        try
        {
            _logger.LogDebug("Invoking Lambda function {FunctionName}, async: {IsAsync}",
                functionName, isAsync);

            var payloadJson = JsonSerializer.Serialize(payload);

            var request = new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = isAsync ? InvocationType.Event : InvocationType.RequestResponse,
                Payload = payloadJson
            };

            var response = await _retryPolicy.ExecuteAsync(async () =>
                await _lambdaClient.InvokeAsync(request, cancellationToken));

            var result = new LambdaInvocationResult
            {
                FunctionName = functionName,
                StatusCode = response.StatusCode ?? 0,
                IsSuccess = response.StatusCode == 200,
                ExecutedVersion = response.ExecutedVersion,
                LogResult = response.LogResult,
                Payload = response.Payload != null ?
                    System.Text.Encoding.UTF8.GetString(response.Payload.ToArray()) : string.Empty
            };

            if (response.FunctionError != null)
            {
                result.IsSuccess = false;
                result.ErrorMessage = response.FunctionError;
                _logger.LogError("Lambda function {FunctionName} returned error: {Error}",
                    functionName, response.FunctionError);
            }
            else
            {
                _logger.LogDebug("Lambda function {FunctionName} invoked successfully",
                    functionName);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking Lambda function {FunctionName}", functionName);
            throw;
        }
    }
}

/// <summary>
/// Result of Lambda function invocation
/// </summary>
public class LambdaInvocationResult
{
    public string FunctionName { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public bool IsSuccess { get; set; }
    public string ExecutedVersion { get; set; } = string.Empty;
    public string LogResult { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}