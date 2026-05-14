using ChokaQ.Abstractions.Jobs;
using ChokaQ.Core;
using ChokaQ.Core.Serialization;
using System.Text.Json;

namespace ChokaQ.Tests.Unit.Serialization;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class SystemTextJsonChokaQJobSerializerTests
{
    [Fact]
    public void Serialize_DefaultOptions_ShouldPreserveExistingPascalCasePayloadContract()
    {
        var serializer = new SystemTextJsonChokaQJobSerializer(new ChokaQSerializationOptions());

        var payload = serializer.Serialize(new TestJob { Id = "job-1", Message = "hello" }, typeof(TestJob));

        payload.Should().Contain("\"Id\"");
        payload.Should().Contain("\"Message\"");
        payload.Should().NotContain("\"id\"");
    }

    [Fact]
    public void Serialize_CustomOptions_ShouldApplyConfiguredJsonOptions()
    {
        var serializer = new SystemTextJsonChokaQJobSerializer(new ChokaQSerializationOptions
        {
            JsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }
        });

        var payload = serializer.Serialize(new TestJob { Id = "job-1", Message = "hello" }, typeof(TestJob));

        payload.Should().Contain("\"id\"");
        payload.Should().Contain("\"message\"");
    }

    [Fact]
    public void Deserialize_ShouldUseConfiguredJsonOptions()
    {
        var serializer = new SystemTextJsonChokaQJobSerializer(new ChokaQSerializationOptions
        {
            JsonSerializerOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }
        });

        var job = serializer.Deserialize("""{"id":"job-1","message":"hello"}""", typeof(TestJob));

        job.Should().BeOfType<TestJob>();
        ((TestJob)job!).Message.Should().Be("hello");
    }

    private sealed class TestJob : IChokaQJob
    {
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
    }
}

