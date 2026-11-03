using PcMigrate.Core.Health;

namespace PcMigrate.Tests;

public class HealthTests
{
    [Fact]
    public void PredictedFailureIsFailing() =>
        Assert.Equal(HealthLevel.Failing, DriveHealth.Assess(new HealthReadings { WindowsHealth = 1, OperationalStatus = [5] }).Level);

    [Fact]
    public void UncorrectableReadErrorsAreFailing()
    {
        var (level, _, details) = DriveHealth.Assess(new HealthReadings { WindowsHealth = 0, ReadErrorsUncorrected = 12 });
        Assert.Equal(HealthLevel.Failing, level);
        Assert.Contains(details, d => d.Contains("12 read error"));
    }

    [Fact]
    public void WornOrHotDrivesAreWarnings()
    {
        Assert.Equal(HealthLevel.Warning, DriveHealth.Assess(new HealthReadings { WindowsHealth = 0, IsSsd = true, WearPercent = 93 }).Level);
        Assert.Equal(HealthLevel.Warning, DriveHealth.Assess(new HealthReadings { WindowsHealth = 0, TemperatureC = 58 }).Level);
        Assert.Equal(HealthLevel.Good, DriveHealth.Assess(new HealthReadings { WindowsHealth = 0, IsSsd = true, TemperatureC = 58 }).Level);
    }

    [Fact]
    public void HealthyDriveListsItsReadings()
    {
        var (level, _, details) = DriveHealth.Assess(new HealthReadings { WindowsHealth = 0, TemperatureC = 38, PowerOnHours = 8760 });
        Assert.Equal(HealthLevel.Good, level);
        Assert.Contains("38 °C", details);
        Assert.Contains(details, d => d.Contains("about 1 year)"));
    }

    [Fact]
    public void NothingReportedIsUnknown() =>
        Assert.Equal(HealthLevel.Unknown, DriveHealth.Assess(new HealthReadings { WindowsHealth = 5 }).Level);
}
