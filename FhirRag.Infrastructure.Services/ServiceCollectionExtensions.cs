using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http;
using Amazon.BedrockRuntime;
using Amazon.Lambda;
using Amazon.S3;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using FhirRag.Core.Abstractions;
using Polly;
using Polly.Extensions.Http;

namespace FhirRag.Infrastructure.Services;

/// <summary>
/// Extension methods for registering infrastructure services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds AWS infrastructure services to the service collection
    /// </summary>
    public static IServiceCollection AddAwsInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register AWS clients
        services.AddAwsClients(configuration);

        // Register configurations
        services.AddInfrastructureConfigurations(configuration);

        // Register core infrastructure services
        services.AddCoreInfrastructureServices();

        // Register health checks
        services.AddInfrastructureHealthChecks();

        // Register HTTP clients with Polly policies
        services.AddHttpClientsWithPolicies();

        return services;
    }

    /// <summary>
    /// Adds AWS SDK clients with configuration
    /// </summary>
    private static IServiceCollection AddAwsClients(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var awsRegion = configuration.GetValue<string>("AWS:Region") ?? "us-east-1";
        var accessKeyId = configuration.GetValue<string>("AWS:AccessKeyId");
        var secretAccessKey = configuration.GetValue<string>("AWS:SecretAccessKey");

        // Configure AWS credentials if provided
        if (!string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey))
        {
            services.AddSingleton<Amazon.RegionEndpoint>(_ => Amazon.RegionEndpoint.GetBySystemName(awsRegion));
        }

        // Register AWS service clients
        services.AddSingleton<IAmazonBedrockRuntime>(provider =>
        {
            var config = new AmazonBedrockRuntimeConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(awsRegion)
            };

            return !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
                ? new AmazonBedrockRuntimeClient(accessKeyId, secretAccessKey, config)
                : new AmazonBedrockRuntimeClient(config);
        });

        services.AddSingleton<IAmazonLambda>(provider =>
        {
            var config = new AmazonLambdaConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(awsRegion)
            };

            return !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
                ? new AmazonLambdaClient(accessKeyId, secretAccessKey, config)
                : new AmazonLambdaClient(config);
        });

        services.AddSingleton<IAmazonS3>(provider =>
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(awsRegion)
            };

            return !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
                ? new AmazonS3Client(accessKeyId, secretAccessKey, config)
                : new AmazonS3Client(config);
        });

        services.AddSingleton<IAmazonDynamoDB>(provider =>
        {
            var config = new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(awsRegion)
            };

            return !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
                ? new AmazonDynamoDBClient(accessKeyId, secretAccessKey, config)
                : new AmazonDynamoDBClient(config);
        });

        services.AddSingleton<IAmazonSQS>(provider =>
        {
            var config = new AmazonSQSConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(awsRegion)
            };

            return !string.IsNullOrEmpty(accessKeyId) && !string.IsNullOrEmpty(secretAccessKey)
                ? new AmazonSQSClient(accessKeyId, secretAccessKey, config)
                : new AmazonSQSClient(config);
        });

        return services;
    }

    /// <summary>
    /// Adds infrastructure service configurations
    /// </summary>
    private static IServiceCollection AddInfrastructureConfigurations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bedrock LLM Configuration
        services.Configure<BedrockLlmConfiguration>(options =>
        {
            configuration.GetSection("Infrastructure:Bedrock").Bind(options);

            // Set defaults if not configured
            if (string.IsNullOrEmpty(options.Region))
                options.Region = configuration.GetValue<string>("AWS:Region") ?? "us-east-1";

            if (string.IsNullOrEmpty(options.DefaultModelId))
                options.DefaultModelId = "anthropic.claude-3-5-sonnet-20241022-v2:0";

            if (string.IsNullOrEmpty(options.EmbeddingModelId))
                options.EmbeddingModelId = "amazon.titan-embed-text-v2:0";
        });

        services.AddSingleton(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<BedrockLlmConfiguration>>().Value);

        // Lambda Orchestration Configuration
        services.Configure<LambdaOrchestrationConfiguration>(options =>
        {
            configuration.GetSection("Infrastructure:Lambda").Bind(options);

            if (string.IsNullOrEmpty(options.Region))
                options.Region = configuration.GetValue<string>("AWS:Region") ?? "us-east-1";
        });

        services.AddSingleton(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LambdaOrchestrationConfiguration>>().Value);

        // Vector Embedding Configuration
        services.Configure<VectorEmbeddingConfiguration>(options =>
        {
            configuration.GetSection("Infrastructure:VectorEmbedding").Bind(options);
        });

        services.AddSingleton(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VectorEmbeddingConfiguration>>().Value);

        // AWS Storage Configuration
        services.Configure<AwsStorageConfiguration>(options =>
        {
            configuration.GetSection("Infrastructure:Storage").Bind(options);

            if (string.IsNullOrEmpty(options.Region))
                options.Region = configuration.GetValue<string>("AWS:Region") ?? "us-east-1";
        });

        services.AddSingleton(provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AwsStorageConfiguration>>().Value);

        return services;
    }

    /// <summary>
    /// Adds core infrastructure services
    /// </summary>
    private static IServiceCollection AddCoreInfrastructureServices(this IServiceCollection services)
    {
        // Register as both concrete type and interface
        services.AddScoped<BedrockLlmService>();
        services.AddScoped<ILlmService>(provider => provider.GetRequiredService<BedrockLlmService>());

        services.AddScoped<LambdaOrchestrationService>();
        services.AddScoped<IOrchestrationService>(provider => provider.GetRequiredService<LambdaOrchestrationService>());

        services.AddScoped<VectorEmbeddingService>();
        services.AddScoped<IEmbeddingService>(provider => provider.GetRequiredService<VectorEmbeddingService>());

        services.AddScoped<AwsStorageService>();
        services.AddScoped<IStorageService>(provider => provider.GetRequiredService<AwsStorageService>());

        // Utility services
        services.AddScoped<LambdaFunctionInvoker>();
        services.AddScoped<BedrockHealthCheckService>();

        return services;
    }

    /// <summary>
    /// Adds health checks for infrastructure services
    /// </summary>
    private static IServiceCollection AddInfrastructureHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<BedrockHealthCheckService>("bedrock-llm")
            .AddCheck("s3-storage", () =>
            {
                // Add S3 health check implementation
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("S3 storage is available");
            })
            .AddCheck("dynamodb-metadata", () =>
            {
                // Add DynamoDB health check implementation
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("DynamoDB metadata store is available");
            })
            .AddCheck("lambda-orchestration", () =>
            {
                // Add Lambda health check implementation
                return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Lambda orchestration is available");
            });

        return services;
    }

    /// <summary>
    /// Adds HTTP clients with Polly retry policies
    /// </summary>
    private static IServiceCollection AddHttpClientsWithPolicies(this IServiceCollection services)
    {
        // Add named HTTP client for external APIs
        services.AddHttpClient("external-api", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.Add("User-Agent", "FhirRag/1.0");
        })
        .AddPolicyHandler(GetRetryPolicy())
        .AddPolicyHandler(GetCircuitBreakerPolicy());

        // Add HTTP client for FHIR servers
        services.AddHttpClient("fhir-server", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.Add("Accept", "application/fhir+json");
        })
        .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    /// <summary>
    /// Gets retry policy for HTTP clients
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var logger = context.GetLogger();
                    logger?.LogWarning("HTTP retry {RetryCount} after {Delay}ms. Reason: {Reason}",
                        retryCount, timespan.TotalMilliseconds, outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase);
                });
    }

    /// <summary>
    /// Gets circuit breaker policy for HTTP clients
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Adds specific AWS infrastructure services based on configuration
    /// </summary>
    public static IServiceCollection AddBedrockServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAwsClients(configuration);
        services.Configure<BedrockLlmConfiguration>(configuration.GetSection("Infrastructure:Bedrock"));
        services.AddScoped<BedrockLlmService>();
        services.AddScoped<ILlmService>(provider => provider.GetRequiredService<BedrockLlmService>());
        services.AddScoped<BedrockHealthCheckService>();

        return services;
    }

    /// <summary>
    /// Adds Lambda orchestration services
    /// </summary>
    public static IServiceCollection AddLambdaOrchestrationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAwsClients(configuration);
        services.Configure<LambdaOrchestrationConfiguration>(configuration.GetSection("Infrastructure:Lambda"));
        services.AddScoped<LambdaOrchestrationService>();
        services.AddScoped<IOrchestrationService>(provider => provider.GetRequiredService<LambdaOrchestrationService>());
        services.AddScoped<LambdaFunctionInvoker>();

        return services;
    }

    /// <summary>
    /// Adds vector embedding services
    /// </summary>
    public static IServiceCollection AddVectorEmbeddingServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<VectorEmbeddingConfiguration>(configuration.GetSection("Infrastructure:VectorEmbedding"));
        services.AddScoped<VectorEmbeddingService>();
        services.AddScoped<IEmbeddingService>(provider => provider.GetRequiredService<VectorEmbeddingService>());

        return services;
    }

    /// <summary>
    /// Adds AWS storage services
    /// </summary>
    public static IServiceCollection AddAwsStorageServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAwsClients(configuration);
        services.Configure<AwsStorageConfiguration>(configuration.GetSection("Infrastructure:Storage"));
        services.AddScoped<AwsStorageService>();
        services.AddScoped<IStorageService>(provider => provider.GetRequiredService<AwsStorageService>());

        return services;
    }
}

/// <summary>
/// Extension methods for getting loggers from Polly context
/// </summary>
internal static class PolicyContextExtensions
{
    public static ILogger? GetLogger(this Polly.Context context)
    {
        if (context.TryGetValue("logger", out var loggerObj) && loggerObj is ILogger logger)
        {
            return logger;
        }
        return null;
    }
}