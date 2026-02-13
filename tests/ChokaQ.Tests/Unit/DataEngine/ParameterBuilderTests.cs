using ChokaQ.Storage.SqlServer.DataEngine;

namespace ChokaQ.Tests.Unit.DataEngine;

public class ParameterBuilderTests
{
    [Fact]
    public void BuildParameters_Null_ShouldReturnEmpty()
    {
        var (p, s) = ParameterBuilder.BuildParameters(null, "SELECT *");
        p.Should().BeEmpty();
        s.Should().Be("SELECT *");
    }

    [Fact]
    public void BuildParameters_AnonymousObject_ShouldBuildParams()
    {
        var (p, s) = ParameterBuilder.BuildParameters(new { Id = 1, Name = "Test" }, "SELECT *");

        p.Should().HaveCount(2);
        p.Should().Contain(x => x.ParameterName == "@Id" && (int)x.Value == 1);
        p.Should().Contain(x => x.ParameterName == "@Name" && (string)x.Value == "Test");
    }

    [Fact]
    public void BuildParameters_Dictionary_ShouldBuildParams()
    {
        var dict = new Dictionary<string, object?> { { "Id", 1 } };
        var (p, s) = ParameterBuilder.BuildParameters(dict, "SELECT *");

        p.Should().HaveCount(1);
        p.Should().Contain(x => x.ParameterName == "@Id" && (int)x.Value == 1);
    }

    [Fact]
    public void BuildParameters_Array_ShouldExpandInClause()
    {
        var ids = new[] { 1, 2 };
        var (p, s) = ParameterBuilder.BuildParameters(new { Ids = ids }, "SELECT * FROM T WHERE Id IN @Ids");

        p.Should().HaveCount(2);
        p[0].ParameterName.Should().Be("@Ids0");
        p[1].ParameterName.Should().Be("@Ids1");
        s.Should().Be("SELECT * FROM T WHERE Id IN (@Ids0, @Ids1)");
    }

    [Fact]
    public void BuildParameters_EmptyArray_ShouldReplaceWithFalseCondition()
    {
        var ids = Array.Empty<int>();
        var (p, s) = ParameterBuilder.BuildParameters(new { Ids = ids }, "SELECT * FROM T WHERE Id IN @Ids");

        p.Should().BeEmpty();
        s.Should().Be("SELECT * FROM T WHERE Id IN (SELECT NULL WHERE 1=0)");
    }
}
