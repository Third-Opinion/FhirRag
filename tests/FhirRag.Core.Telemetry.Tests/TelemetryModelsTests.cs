using FluentAssertions;
using FhirRag.Core.Telemetry;
using Xunit;

namespace FhirRag.Core.Telemetry.Tests;

public class TelemetryModelsTests
{
    [Fact]
    public void TelemetryContext_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var context = new TelemetryContext();

        // Assert
        context.SessionId.Should().NotBeEmpty();
        context.TenantId.Should().BeEmpty();
        context.UserId.Should().BeEmpty();
        context.ResourceType.Should().BeEmpty();
        context.ResourceId.Should().BeEmpty();
        context.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        context.Steps.Should().NotBeNull().And.BeEmpty();
        context.Metadata.Should().NotBeNull().And.BeEmpty();
        context.EnableS3Storage.Should().BeTrue();
    }

    [Fact]
    public void TelemetryContext_StartStep_Should_Create_New_Step()
    {
        // Arrange
        var context = new TelemetryContext();
        var stepName = "process_patient_data";
        var description = "Processing patient FHIR data";

        // Act
        var step = context.StartStep(stepName, description);

        // Assert
        step.Should().NotBeNull();
        step.Name.Should().Be(stepName);
        step.Description.Should().Be(description);
        step.SessionId.Should().Be(context.SessionId);
        step.Status.Should().Be(TelemetryStepStatus.InProgress);
        step.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        step.CompletedAt.Should().BeNull();

        context.Steps.Should().HaveCount(1);
        context.Steps.Should().Contain(step);
    }

    [Fact]
    public void TelemetryContext_StartStep_Should_Use_StepName_As_Description_When_Not_Provided()
    {
        // Arrange
        var context = new TelemetryContext();
        var stepName = "validate_fhir_resource";

        // Act
        var step = context.StartStep(stepName);

        // Assert
        step.Name.Should().Be(stepName);
        step.Description.Should().Be(stepName);
    }

    [Fact]
    public void TelemetryContext_Complete_Should_Complete_All_InProgress_Steps()
    {
        // Arrange
        var context = new TelemetryContext();
        var step1 = context.StartStep("step1");
        var step2 = context.StartStep("step2");
        var step3 = context.StartStep("step3");

        // Complete step2 manually
        step2.Complete(true);

        // Act
        context.Complete(true, null);

        // Assert
        step1.Status.Should().Be(TelemetryStepStatus.Completed);
        step2.Status.Should().Be(TelemetryStepStatus.Completed); // Already completed
        step3.Status.Should().Be(TelemetryStepStatus.Completed);

        step1.CompletedAt.Should().NotBeNull();
        step3.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void TelemetryContext_Complete_Should_Mark_Steps_As_Failed_When_Not_Successful()
    {
        // Arrange
        var context = new TelemetryContext();
        var step = context.StartStep("failing_step");
        var errorMessage = "Processing failed";

        // Act
        context.Complete(false, errorMessage);

        // Assert
        step.Status.Should().Be(TelemetryStepStatus.Failed);
        step.ErrorMessage.Should().Be(errorMessage);
        step.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void TelemetryContext_GetTotalDuration_Should_Return_Zero_When_No_Completed_Steps()
    {
        // Arrange
        var context = new TelemetryContext();
        context.StartStep("incomplete_step");

        // Act
        var duration = context.GetTotalDuration();

        // Assert
        duration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TelemetryContext_GetTotalDuration_Should_Calculate_Correctly()
    {
        // Arrange
        var context = new TelemetryContext();
        var step1 = context.StartStep("step1");
        var step2 = context.StartStep("step2");

        // Simulate time passing
        step1.StartedAt = DateTime.UtcNow.AddMinutes(-5);
        step1.Complete(true);
        step1.CompletedAt = DateTime.UtcNow.AddMinutes(-3);

        step2.StartedAt = DateTime.UtcNow.AddMinutes(-2);
        step2.Complete(true);
        step2.CompletedAt = DateTime.UtcNow;

        // Act
        var duration = context.GetTotalDuration();

        // Assert
        duration.Should().BeCloseTo(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void TelemetryContext_GetMetrics_Should_Return_Correct_Metrics()
    {
        // Arrange
        var context = new TelemetryContext
        {
            ResourceType = "Patient",
            ResourceId = "patient-123"
        };

        var step1 = context.StartStep("step1");
        step1.Complete(true);

        var step2 = context.StartStep("step2");
        step2.Complete(false, "Error occurred");

        var step3 = context.StartStep("step3");
        step3.Complete(true);

        // Act
        var metrics = context.GetMetrics();

        // Assert
        metrics.SessionId.Should().Be(context.SessionId);
        metrics.TotalSteps.Should().Be(3);
        metrics.SuccessfulSteps.Should().Be(2);
        metrics.FailedSteps.Should().Be(1);
        metrics.ResourceType.Should().Be("Patient");
        metrics.ResourceId.Should().Be("patient-123");
        metrics.TotalDuration.Should().BeGreaterThan(TimeSpan.Zero);
        metrics.AverageStepDuration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void TelemetryStep_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var step = new TelemetryStep();

        // Assert
        step.Name.Should().BeEmpty();
        step.Description.Should().BeEmpty();
        step.SessionId.Should().BeEmpty();
        step.Status.Should().Be(TelemetryStepStatus.InProgress);
        step.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        step.CompletedAt.Should().BeNull();
        step.ErrorMessage.Should().BeNull();
        step.Data.Should().NotBeNull().And.BeEmpty();
        step.Duration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void TelemetryStep_Complete_Should_Set_Completion_Properties()
    {
        // Arrange
        var step = new TelemetryStep();
        // Set StartedAt to 1 second ago to ensure duration > 0
        step.StartedAt = DateTime.UtcNow.AddSeconds(-1);
        var errorMessage = "Step failed";

        // Act - Complete successfully
        step.Complete(true);

        // Assert
        step.Status.Should().Be(TelemetryStepStatus.Completed);
        step.CompletedAt.Should().NotBeNull();
        step.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        step.ErrorMessage.Should().BeNull();
        step.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public void TelemetryStep_Complete_Should_Set_Failed_Status_And_Error()
    {
        // Arrange
        var step = new TelemetryStep();
        var errorMessage = "Step failed";

        // Act - Complete with failure
        step.Complete(false, errorMessage);

        // Assert
        step.Status.Should().Be(TelemetryStepStatus.Failed);
        step.CompletedAt.Should().NotBeNull();
        step.ErrorMessage.Should().Be(errorMessage);
    }

    [Fact]
    public void TelemetryStep_AddData_Should_Add_KeyValue_Pair()
    {
        // Arrange
        var step = new TelemetryStep();
        var key = "processing_time_ms";
        var value = 1500;

        // Act
        step.AddData(key, value);

        // Assert
        step.Data.Should().ContainKey(key);
        step.Data[key].Should().Be(value);
    }

    [Fact]
    public void TelemetryStep_GetData_Should_Return_Typed_Value()
    {
        // Arrange
        var step = new TelemetryStep();
        step.Data["count"] = 42;
        step.Data["name"] = "test_step";

        // Act
        var count = step.GetData<int>("count");
        var name = step.GetData<string>("name");
        var missing = step.GetData<double>("missing");

        // Assert
        count.Should().Be(42);
        name.Should().Be("test_step");
        missing.Should().Be(0); // Default value for double
    }

    [Fact]
    public void TelemetryMetrics_Should_Calculate_Success_Rate_Correctly()
    {
        // Arrange
        var metrics = new TelemetryMetrics
        {
            TotalSteps = 10,
            SuccessfulSteps = 8,
            FailedSteps = 2
        };

        // Act
        var successRate = metrics.SuccessRate;

        // Assert
        successRate.Should().Be(0.8);
    }

    [Fact]
    public void TelemetryMetrics_Should_Return_Zero_Success_Rate_When_No_Steps()
    {
        // Arrange
        var metrics = new TelemetryMetrics
        {
            TotalSteps = 0,
            SuccessfulSteps = 0,
            FailedSteps = 0
        };

        // Act
        var successRate = metrics.SuccessRate;

        // Assert
        successRate.Should().Be(0.0);
    }

    [Theory]
    [InlineData(10, 10, 0, 5)] // Perfect success, fast - 5 stars
    [InlineData(10, 9, 1, 4)]  // 90% success - 4 stars  
    [InlineData(10, 8, 2, 3)]  // 80% success - 3 stars
    [InlineData(10, 5, 5, 2)]  // 50% success - 2 stars
    [InlineData(10, 0, 10, 1)] // Complete failure - 1 star
    [InlineData(0, 0, 0, 0)]   // No steps - 0 stars
    public void TelemetryMetrics_GetPerformanceRating_Should_Return_Correct_Rating(
        int totalSteps, int successfulSteps, int failedSteps, int expectedRating)
    {
        // Arrange
        var metrics = new TelemetryMetrics
        {
            TotalSteps = totalSteps,
            SuccessfulSteps = successfulSteps,
            FailedSteps = failedSteps,
            AverageStepDuration = TimeSpan.FromSeconds(10) // Fast execution
        };

        // Act
        var rating = metrics.GetPerformanceRating();

        // Assert
        rating.Should().Be(expectedRating);
    }
}