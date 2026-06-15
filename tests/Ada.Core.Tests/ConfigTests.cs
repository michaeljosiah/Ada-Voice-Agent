using Ada.Core;

namespace Ada.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Private_profile_stays_local_and_offers_no_cloud()
    {
        var p = Profiles.For(AdaProfile.Private);
        Assert.True(p.StayLocal);
        Assert.False(p.AllowCloudEscalation);
    }

    [Fact]
    public void Balanced_profile_allows_cloud_and_container()
    {
        var p = Profiles.For(AdaProfile.Balanced);
        Assert.False(p.StayLocal);
        Assert.True(p.AllowCloudEscalation);
        Assert.True(p.AllowContainerSandbox);
    }

    [Fact]
    public void Config_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ada_cfg_{Guid.NewGuid():n}.json");
        try
        {
            new ConfigStore(path).Save(new AdaConfig { Profile = AdaProfile.Power, SetupComplete = true, Autostart = true });
            var loaded = new ConfigStore(path).Load();
            Assert.Equal(AdaProfile.Power, loaded.Profile);
            Assert.True(loaded.SetupComplete);
            Assert.True(loaded.Autostart);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Autostart_registers_and_removes_under_the_run_key()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows-only feature

        var valueName = "AdaTest_" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            Assert.True(Autostart.Enable(@"C:\Ada\ada.exe", valueName));
            Assert.True(Autostart.IsEnabled(valueName));
            Assert.True(Autostart.Disable(valueName));
            Assert.False(Autostart.IsEnabled(valueName));
        }
        finally { Autostart.Disable(valueName); }
    }
}
