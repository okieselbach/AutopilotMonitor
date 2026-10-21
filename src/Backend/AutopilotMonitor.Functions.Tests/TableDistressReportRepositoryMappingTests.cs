using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Entity ↔ entry round trip of <see cref="TableDistressReportRepository"/>: a field added to
/// <see cref="DistressReportEntry"/> but not to both mappers is lost silently in storage.
/// </summary>
public class TableDistressReportRepositoryMappingTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 26, 54, DateTimeKind.Utc);

    private static DistressReportEntry FullyPopulated() => new()
    {
        TenantId = "11111111-2222-3333-4444-555555555555",
        ErrorType = "TpmPssUnsupported",
        Manufacturer = "HP",
        Model = "EliteBook 840 G5",
        SerialNumber = "SN-TEST-1",
        AgentVersion = "2.0.1500+abc",
        HttpStatusCode = 403,
        Message = "pkcs1=ok pss256=fail(0x80290423)",
        AgentTimestamp = T0,
        IngestedAt = T0.AddSeconds(1),
        SourceIp = "203.0.113.7",
        IsCloudPc = true,
        CertSourceState = "Found",
        CertThumbprint = new string('A', 40),
        CertSubject = "CN=device",
        CertIssuer = "CN=Microsoft Intune MDM Device CA",
        CertNotBefore = T0.AddDays(-30),
        CertNotAfter = T0.AddDays(335),
        OsBuild = "26200.6899",
        OobeState = "in_progress",
        TpmVersion = "IFX 7.63.3353.0",
        EspPhase = "account_setup",
        DeviceBootUtc = T0.AddMinutes(-20),
    };

    [Fact]
    public void Fixture_SetsEveryField()
    {
        // Guards the round trip below: a new field must be set here to be checked there.
        var entry = FullyPopulated();
        foreach (var property in typeof(DistressReportEntry).GetProperties())
        {
            var value = property.GetValue(entry);
            Assert.False(
                value == null || (property.PropertyType.IsValueType && value.Equals(Activator.CreateInstance(property.PropertyType))),
                $"{property.Name} is not set in the fixture");
        }
    }

    [Fact]
    public void Roundtrip_PreservesEveryField()
    {
        var entry = FullyPopulated();

        var back = TableDistressReportRepository.MapToEntry(
            TableDistressReportRepository.MapToEntity(entry.TenantId, "2516098015901843767", entry));

        foreach (var property in typeof(DistressReportEntry).GetProperties())
            Assert.True(Equals(property.GetValue(entry), property.GetValue(back)), $"{property.Name} lost in the round trip");
    }

    [Fact]
    public void Entity_PartitionKeyIsLowerCasedTenant()
    {
        var entity = TableDistressReportRepository.MapToEntity(
            "AAAAAAAA-2222-3333-4444-555555555555", "rk", FullyPopulated());

        Assert.Equal("aaaaaaaa-2222-3333-4444-555555555555", entity.PartitionKey);
    }
}
