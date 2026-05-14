using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core.Execution;

namespace ChokaQ.Tests.Unit.Execution;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class JobTypeRegistryCollisionTests
{
    [Fact]
    public void UnregisteredFallback_ShouldNotCollideForDuplicateShortNames()
    {
        var registry = new JobTypeRegistry();

        var firstKey = registry.GetPersistedTypeKey(typeof(CollisionA.SendEmailJob));
        var secondKey = registry.GetPersistedTypeKey(typeof(CollisionB.SendEmailJob));

        firstKey.Should().NotBe(secondKey);
        firstKey.Should().Contain(typeof(CollisionA.SendEmailJob).FullName!);
        secondKey.Should().Contain(typeof(CollisionB.SendEmailJob).FullName!);
        registry.ResolvePersistedType(firstKey).Should().Be(typeof(CollisionA.SendEmailJob));
        registry.ResolvePersistedType(secondKey).Should().Be(typeof(CollisionB.SendEmailJob));
    }

    private static class CollisionA
    {
        public sealed class SendEmailJob : IChokaQJob
        {
            public string Id { get; set; } = "";
        }
    }

    private static class CollisionB
    {
        public sealed class SendEmailJob : IChokaQJob
        {
            public string Id { get; set; } = "";
        }
    }
}

