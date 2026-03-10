using System.Text;

namespace FBIS.Tests.TestInfrastructure;

public static class MockSnapshotStore
{
    public static string CreateSnapshotFile(string filePrefix, IEnumerable<object> transactions)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fbis-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{filePrefix}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(transactions));
        return path;
    }
}
