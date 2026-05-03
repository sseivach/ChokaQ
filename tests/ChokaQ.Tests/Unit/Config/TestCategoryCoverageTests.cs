using System.Reflection;

namespace ChokaQ.Tests.Unit.Config;

[Trait(TestCategories.Category, TestCategories.Unit)]
public class TestCategoryCoverageTests
{
    [Fact]
    public void Every_test_class_should_have_a_stable_category_trait()
    {
        // CI filters depend on Category=Unit and Category=Integration, not on folder names.
        // This guard makes that contract visible: adding a new test class without a category
        // should fail fast locally instead of quietly disappearing from one of the pipelines.
        var testClasses = typeof(TestCategoryCoverageTests).Assembly
            .GetTypes()
            .Where(type =>
                type is { IsClass: true, IsAbstract: false } &&
                type.Namespace is not null &&
                type.Namespace.StartsWith("ChokaQ.Tests.", StringComparison.Ordinal) &&
                HasXunitTests(type))
            .ToArray();

        var missingOrInvalidCategories = testClasses
            .Where(type =>
            {
                var categories = type.GetCustomAttributesData()
                    .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
                    .Where(attribute =>
                        attribute.ConstructorArguments.Count == 2 &&
                        attribute.ConstructorArguments[0].Value as string == TestCategories.Category)
                    .Select(attribute => attribute.ConstructorArguments[1].Value as string)
                    .ToArray();

                return categories.Length != 1 ||
                       categories[0] is not (TestCategories.Unit or TestCategories.Integration);
            })
            .Select(type => type.FullName)
            .Order()
            .ToArray();

        missingOrInvalidCategories.Should().BeEmpty(
            "every xUnit class must declare exactly one stable Category trait for CI filtering");
    }

    private static bool HasXunitTests(Type type)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(method =>
                method.GetCustomAttribute<FactAttribute>() is not null ||
                method.GetCustomAttribute<TheoryAttribute>() is not null);
    }
}
