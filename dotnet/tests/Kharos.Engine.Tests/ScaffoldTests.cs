namespace Kharos.Engine.Tests;

public sealed class ScaffoldTests
{
    [Fact]
    public void EngineAssemblyLoads() => Assert.Equal("Kharos.Engine", typeof(EngineInfo).Assembly.GetName().Name);
}