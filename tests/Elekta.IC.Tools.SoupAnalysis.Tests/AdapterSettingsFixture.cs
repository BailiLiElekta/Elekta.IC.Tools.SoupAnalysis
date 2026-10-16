namespace Elekta.IC.Tools.SoupAnalysis.Tests;

internal sealed class AdapterSettingsFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "soup-adapter-" + Guid.NewGuid().ToString("N"));
    public AdapterSettingsFixture() => System.IO.Directory.CreateDirectory(Directory);
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}