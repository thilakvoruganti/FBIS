namespace FBIS.App.Application.Options;

public class JobOptions
{
    public int LookbackHours { get; set; } = 24;
    public bool EnableFinalization { get; set; } = true;
}
