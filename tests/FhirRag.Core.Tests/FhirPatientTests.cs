using FluentAssertions;
using FhirRag.Core.Models;
using Xunit;
using LocalCoding = FhirRag.Core.Models;

namespace FhirRag.Core.Tests;

public class FhirPatientTests
{
    [Fact]
    public void FhirPatient_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var patient = new FhirPatient();

        // Assert
        patient.Id.Should().BeEmpty();
        patient.Identifier.Should().BeEmpty();
        patient.TenantId.Should().BeEmpty();
        patient.Gender.Should().BeEmpty();
        patient.BirthDate.Should().BeNull();
        patient.ConditionIds.Should().NotBeNull().And.BeEmpty();
        patient.ObservationIds.Should().NotBeNull().And.BeEmpty();
        patient.MedicationIds.Should().NotBeNull().And.BeEmpty();
        patient.ProcedureIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void FhirPatient_Should_Allow_Setting_Properties()
    {
        // Arrange
        var patient = new FhirPatient();
        var patientId = "patient-123";
        var identifier = "MRN-12345";
        var tenantId = "hospital-a";
        var gender = "male";
        var birthDate = new DateTime(1990, 5, 15);

        // Act
        patient.Id = patientId;
        patient.Identifier = identifier;
        patient.TenantId = tenantId;
        patient.Gender = gender;
        patient.BirthDate = birthDate;

        // Assert
        patient.Id.Should().Be(patientId);
        patient.Identifier.Should().Be(identifier);
        patient.TenantId.Should().Be(tenantId);
        patient.Gender.Should().Be(gender);
        patient.BirthDate.Should().Be(birthDate);
    }

    [Fact]
    public void FhirPatient_Should_Allow_Adding_Related_Resource_Ids()
    {
        // Arrange
        var patient = new FhirPatient();

        // Act
        patient.ConditionIds.Add("condition-1");
        patient.ConditionIds.Add("condition-2");
        patient.ObservationIds.Add("observation-1");
        patient.MedicationIds.Add("medication-1");
        patient.ProcedureIds.Add("procedure-1");

        // Assert
        patient.ConditionIds.Should().HaveCount(2);
        patient.ConditionIds.Should().Contain("condition-1");
        patient.ConditionIds.Should().Contain("condition-2");
        patient.ObservationIds.Should().HaveCount(1);
        patient.ObservationIds.Should().Contain("observation-1");
        patient.MedicationIds.Should().HaveCount(1);
        patient.MedicationIds.Should().Contain("medication-1");
        patient.ProcedureIds.Should().HaveCount(1);
        patient.ProcedureIds.Should().Contain("procedure-1");
    }
}

public class FhirConditionTests
{
    [Fact]
    public void FhirCondition_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var condition = new FhirCondition();

        // Assert
        condition.Id.Should().BeEmpty();
        condition.PatientId.Should().BeEmpty();
        condition.TenantId.Should().BeEmpty();
        condition.Code.Should().NotBeNull();
        condition.ClinicalStatus.Should().BeNull();
        condition.VerificationStatus.Should().BeNull();
        condition.OnsetDateTime.Should().BeNull();
        condition.AbatementDateTime.Should().BeNull();
    }

    [Fact]
    public void FhirCondition_Should_Allow_Setting_Properties()
    {
        // Arrange
        var condition = new FhirCondition();
        var conditionId = "condition-123";
        var patientId = "patient-456";
        var tenantId = "hospital-b";
        var code = new LocalCoding.CodeableConcept 
        { 
            Coding = new List<LocalCoding.Coding> 
            { 
                new() { Code = "I25.10", Display = "Atherosclerotic heart disease", System = "http://snomed.info/sct" } 
            },
            Text = "Atherosclerotic heart disease"
        };
        var clinicalStatus = "active";
        var verificationStatus = "confirmed";
        var onsetDate = DateTime.Now.AddYears(-2);

        // Act
        condition.Id = conditionId;
        condition.PatientId = patientId;
        condition.TenantId = tenantId;
        condition.Code = code;
        condition.ClinicalStatus = clinicalStatus;
        condition.VerificationStatus = verificationStatus;
        condition.OnsetDateTime = onsetDate;

        // Assert
        condition.Id.Should().Be(conditionId);
        condition.PatientId.Should().Be(patientId);
        condition.TenantId.Should().Be(tenantId);
        condition.Code.Should().Be(code);
        condition.ClinicalStatus.Should().Be(clinicalStatus);
        condition.VerificationStatus.Should().Be(verificationStatus);
        condition.OnsetDateTime.Should().Be(onsetDate);
    }
}

public class ProcessingResultTests
{
    [Fact]
    public void ProcessingResult_Should_Initialize_With_Default_Values()
    {
        // Arrange & Act
        var result = new ProcessingResult();

        // Assert
        result.ResourceId.Should().BeEmpty();
        result.ResourceType.Should().BeEmpty();
        result.TenantId.Should().BeEmpty();
        result.Status.Should().Be(ProcessingStatus.Pending);
        result.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.CompletedAt.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.Steps.Should().NotBeNull().And.BeEmpty();
        result.Metadata.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ProcessingResult_Should_Allow_Setting_Properties()
    {
        // Arrange
        var result = new ProcessingResult();
        var resourceId = "patient-123";
        var resourceType = "Patient";
        var tenantId = "hospital-c";
        var status = ProcessingStatus.Completed;
        var completedAt = DateTime.UtcNow;

        // Act
        result.ResourceId = resourceId;
        result.ResourceType = resourceType;
        result.TenantId = tenantId;
        result.Status = status;
        result.CompletedAt = completedAt;

        // Assert
        result.ResourceId.Should().Be(resourceId);
        result.ResourceType.Should().Be(resourceType);
        result.TenantId.Should().Be(tenantId);
        result.Status.Should().Be(status);
        result.CompletedAt.Should().Be(completedAt);
    }

    [Fact]
    public void ProcessingResult_Should_Allow_Adding_Steps()
    {
        // Arrange
        var result = new ProcessingResult();
        var step = new ProcessingStep
        {
            Name = "validate_fhir_data",
            Status = ProcessingStepStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-3)
        };

        // Act
        result.Steps.Add(step);

        // Assert
        result.Steps.Should().HaveCount(1);
        result.Steps.Should().Contain(step);
    }

    [Fact]
    public void ProcessingResult_Should_Allow_Adding_Metadata()
    {
        // Arrange
        var result = new ProcessingResult();

        // Act
        result.Metadata["processing_time_ms"] = 1500;
        result.Metadata["validation_score"] = 0.95;
        result.Metadata["source_system"] = "epic";

        // Assert
        result.Metadata.Should().HaveCount(3);
        result.Metadata["processing_time_ms"].Should().Be(1500);
        result.Metadata["validation_score"].Should().Be(0.95);
        result.Metadata["source_system"].Should().Be("epic");
    }
}