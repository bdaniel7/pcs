using NUnit.Framework;

namespace PlexCompatibleServer.Tests;

public sealed class BasicTests
{
    [Test]
    public void ProjectIsAlive()
    {
        Assert.That(2 + 2, Is.EqualTo(4));
    }
}
