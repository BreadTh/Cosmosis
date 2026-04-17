using System.Reflection;
using BreadTh.Cosmosis.Query;

namespace BreadTh.Cosmosis.UnitTests;

public class InterfaceCompletenessTests
{
    private static readonly Type[] QueryInterfaces = typeof(CosmosisQuery<>)
        .GetInterfaces()
        .Where(i => i.IsGenericType)
        .Select(i => i.GetGenericTypeDefinition())
        .ToArray();

    private static string GetMethodSignature(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(p => $"{p.ParameterType} {p.Name}");
        var genericSuffix = method.IsGenericMethod ? $"<{method.GetGenericArguments().Length}>" : "";
        return $"{method.Name}{genericSuffix}({string.Join(", ", parameters)})";
    }

    private static HashSet<string> GetAllMethodSignatures()
    {
        var allSignatures = new HashSet<string>();

        foreach (var iface in QueryInterfaces)
        {
            var closedInterface = iface.IsGenericTypeDefinition
                ? iface.MakeGenericType(typeof(object))
                : iface;

            foreach (var method in closedInterface.GetMethods().Where(m => !m.IsSpecialName))
                allSignatures.Add(GetMethodSignature(method));
        }

        return allSignatures;
    }

    [Fact]
    public void AllInterfacesImplementedByCosmosisQuery_HaveTheSameMethodSignatures()
    {
        var allSignatures = GetAllMethodSignatures();

        var failures = new List<string>();

        foreach (var iface in QueryInterfaces)
        {
            var closedInterface = iface.IsGenericTypeDefinition
                ? iface.MakeGenericType(typeof(object))
                : iface;

            var interfaceSignatures = closedInterface
                .GetMethods()
                .Where(m => !m.IsSpecialName)
                .Select(GetMethodSignature)
                .ToHashSet();

            var missing = allSignatures.Except(interfaceSignatures).Order().ToList();

            foreach (var sig in missing)
                failures.Add($"{iface.Name} is missing: {sig}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void AllInterfaceMethodsImplementedByCosmosisQuery_HaveNoDefaultImplementation()
    {
        var failures = new List<string>();

        foreach (var iface in QueryInterfaces)
        {
            var closedInterface = iface.IsGenericTypeDefinition
                ? iface.MakeGenericType(typeof(object))
                : iface;

            foreach (var method in closedInterface.GetMethods().Where(m => !m.IsSpecialName && !m.IsAbstract))
                failures.Add($"{iface.Name}.{GetMethodSignature(method)} has a default implementation");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
