using ChokaQ.Abstractions.Enums;
using ChokaQ.Storage.SqlServer.DataEngine;
using System.Data;

namespace ChokaQ.Tests.Integration;

/// <summary>
/// Tests for the internal TypeMapper class which maps IDataReader to objects.
/// </summary>
public class TypeMapperTests
{
    private readonly IDataReader _reader;

    public TypeMapperTests()
    {
        _reader = Substitute.For<IDataReader>();
    }

    [Fact]
    public void MapRow_ShouldMapRecordType_ViaConstructor()
    {
        // Arrange
        SetupReader(new Dictionary<string, object>
        {
            { "Id", 123 },
            { "Name", "Test Record" }
        });

        // Act
        var result = TypeMapper.MapRow<TestRecord>(_reader);

        // Assert
        result.Id.Should().Be(123);
        result.Name.Should().Be("Test Record");
    }

    [Fact]
    public void MapRow_ShouldMapClass_ViaProperties()
    {
        // Arrange
        SetupReader(new Dictionary<string, object>
        {
            { "Id", 456 },
            { "Name", "Test Class" }
        });

        // Act
        var result = TypeMapper.MapRow<TestClass>(_reader);

        // Assert
        result.Id.Should().Be(456);
        result.Name.Should().Be("Test Class");
    }

    [Fact]
    public void MapRow_ShouldHandleNullableColumns()
    {
        // Arrange
        SetupReader(new Dictionary<string, object>
        {
            { "Id", 789 },
            { "NullableInt", DBNull.Value }
        });

        // Act
        var result = TypeMapper.MapRow<TestClass>(_reader);

        // Assert
        result.Id.Should().Be(789);
        result.NullableInt.Should().BeNull();
    }

    [Fact]
    public void MapRow_ShouldHandleEnumConversion()
    {
        // Arrange
        SetupReader(new Dictionary<string, object>
        {
            { "Status", (int)JobStatus.Processing }
        });

        // Act
        var result = TypeMapper.MapRow<TestEnumClass>(_reader);

        // Assert
        result.Status.Should().Be(JobStatus.Processing);
    }

    [Fact]
    public void MapRow_ShouldIgnoreUnmatchedColumns()
    {
        // Arrange
        SetupReader(new Dictionary<string, object>
        {
            { "Id", 1 },
            { "ExtraColumn", "IgnoreMe" } // No matching property
        });

        // Act
        var result = TypeMapper.MapRow<TestRecord>(_reader);

        // Assert
        result.Id.Should().Be(1);
    }

    private void SetupReader(Dictionary<string, object> data)
    {
        _reader.FieldCount.Returns(data.Count);

        // Mock GetName(i)
        for (int i = 0; i < data.Count; i++)
        {
            _reader.GetName(i).Returns(data.Keys.ElementAt(i));
        }

        // Mock GetValue(i)
        for (int i = 0; i < data.Count; i++)
        {
            _reader.GetValue(i).Returns(data.Values.ElementAt(i));
        }
    }

    // Test Types
    public record TestRecord(int Id, string Name);

    public class TestClass
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int? NullableInt { get; set; }
    }

    public class TestEnumClass
    {
        public JobStatus Status { get; set; }
    }
}
