using System.Data;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.EntityFrameworkCore;

namespace MonitoringApp.Tests;

internal static class PersistenceArchitecture
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Guid TypeDefinitionDocuments = new("932e74bc-dba9-4478-8d46-0f32a7bab3d3");

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value);

    public static IReadOnlyList<string> FindViolations(
        Assembly assembly, Func<string, bool> isCoreSource, IReadOnlyCollection<Type>? selectedTypes = null,
        Func<Type, bool>? isForbiddenType = null, Func<MemberInfo, bool>? isForbiddenReference = null)
    {
        isForbiddenType ??= IsDatabaseType;
        using var symbols = MetadataReaderProvider.FromPortablePdbStream(
            File.OpenRead(Path.ChangeExtension(assembly.Location, ".pdb")));
        var reader = symbols.GetMetadataReader();
        using var image = new PEReader(File.OpenRead(assembly.Location));
        var codeView = image.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        var pdbId = reader.DebugMetadataHeader
            ?? throw new InvalidOperationException("Architecture checks require portable PDB metadata.");
        if (image.ReadCodeViewDebugDirectoryData(codeView).Guid != new Guid(pdbId.Id.Take(16).ToArray()))
            throw new InvalidOperationException("Application assembly and architecture-check PDB do not match.");

        var allTypes = assembly.GetTypes();
        var methodSources = allTypes.SelectMany(Methods).ToDictionary(
            method => method.MetadataToken, method => Sources(method, reader));
        var typeSources = allTypes.ToDictionary(type => type, type => Methods(type)
            .SelectMany(method => methodSources[method.MetadataToken]).Concat(Sources(type, reader))
            .ToHashSet(StringComparer.Ordinal));
        foreach (var type in allTypes)
        {
            for (var parent = type.DeclaringType; parent is not null; parent = parent.DeclaringType)
                typeSources[parent].UnionWith(typeSources[type]);
        }

        string[] TypeSources(Type type)
        {
            for (Type? current = type; current is not null; current = current.DeclaringType)
            {
                if (typeSources[current].Count > 0)
                    return typeSources[current].ToArray();
            }
            return [];
        }

        var violations = new HashSet<string>(StringComparer.Ordinal);
        void Check(string location, IReadOnlyCollection<string> sources, IEnumerable<Type> dependencies)
        {
            if (sources.Count > 0 && sources.All(isCoreSource))
                return;
            foreach (var dependency in dependencies.Where(isForbiddenType))
            {
                var paths = sources.Count == 0 ? "<missing source mapping>" : string.Join(", ", sources);
                violations.Add($"{paths}: {location} depends on {dependency}.");
            }
        }

        foreach (var type in selectedTypes ?? allTypes)
        {
            var sources = TypeSources(type);
            Check(type.FullName!, sources, new[] { type }
                .Concat(type.BaseType is { } parent ? [parent] : [])
                .Concat(type.GetInterfaces()).Concat(AttributeTypes(type)));
            foreach (var field in type.GetFields(DeclaredMembers))
                Check($"{type.FullName}.{field.Name}", sources, new[] { field.FieldType }.Concat(AttributeTypes(field)));
            foreach (var property in type.GetProperties(DeclaredMembers))
                Check($"{type.FullName}.{property.Name}", sources, AttributeTypes(property));

            foreach (var method in Methods(type))
            {
                var documents = methodSources[method.MetadataToken];
                var methodDocuments = documents.Length > 0 ? documents : sources;
                if (methodDocuments.Length > 0 && methodDocuments.All(isCoreSource))
                    continue;
                var location = $"{type.FullName}.{method.Name}";
                Check(location, methodDocuments, SignatureTypes(method).Concat(AttributeTypes(method)));
                var body = method.GetMethodBody();
                if (body is null)
                    continue;
                Check(location, methodDocuments, body.LocalVariables.Select(variable => variable.LocalType));
                Check(location, methodDocuments, body.ExceptionHandlingClauses
                    .Where(clause => clause.Flags == ExceptionHandlingClauseOptions.Clause)
                    .Select(clause => clause.CatchType!));
                foreach (var reference in References(method, body.GetILAsByteArray() ?? []))
                {
                    Check(location, methodDocuments, SignatureTypes(reference));
                    if (isForbiddenReference?.Invoke(reference) == true)
                    {
                        var paths = methodDocuments.Length == 0 ? "<missing source mapping>" : string.Join(", ", methodDocuments);
                        violations.Add($"{paths}: {location} references forbidden member {reference}.");
                    }
                }
            }
        }
        return violations.OrderBy(violation => violation, StringComparer.Ordinal).ToArray();
    }

    public static bool IsDatabaseType(Type type) => ContainsType(type, IsDirectDatabaseType);

    public static bool ContainsType(Type type, Func<Type, bool> matches) => ContainsType(type, matches, []);

    private static bool ContainsType(Type type, Func<Type, bool> matches, HashSet<Type> visited)
    {
        if (!visited.Add(type))
            return false;
        if (type.HasElementType)
            return ContainsType(type.GetElementType()!, matches, visited);
        if (type.IsFunctionPointer)
            return type.GetFunctionPointerParameterTypes().Append(type.GetFunctionPointerReturnType())
                .Any(dependency => ContainsType(dependency, matches, visited));
        if (type.IsGenericParameter)
            return type.GetGenericParameterConstraints().Any(constraint => ContainsType(constraint, matches, visited));

        return matches(type) || type.IsGenericType &&
            type.GetGenericArguments().Any(argument => ContainsType(argument, matches, visited));
    }

    private static bool IsDirectDatabaseType(Type type)
    {
        if (type.FullName is "Microsoft.EntityFrameworkCore.DbUpdateException"
            or "Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"
            or "Microsoft.Data.SqlClient.SqlException"
            or "Microsoft.Data.SqlClient.SqlError"
            or "Microsoft.Data.SqlClient.SqlErrorCollection")
            return false;

        var space = type.Namespace ?? "";
        if (new[] { "Microsoft.EntityFrameworkCore", "Microsoft.Data.SqlClient", "System.Data.SqlClient",
            "System.Data.Common", "Microsoft.Data.Sqlite", "System.Data.SQLite", "System.Data.Odbc",
            "System.Data.OleDb", "Dapper" }.Any(prefix => space == prefix || space.StartsWith(prefix + ".", StringComparison.Ordinal)))
            return true;
        if (typeof(DbContext).IsAssignableFrom(type)
            || typeof(IDbConnection).IsAssignableFrom(type)
            || typeof(IDbCommand).IsAssignableFrom(type)
            || typeof(IDbTransaction).IsAssignableFrom(type)
            || typeof(IDataReader).IsAssignableFrom(type)
            || typeof(IDataAdapter).IsAssignableFrom(type))
            return true;
        return false;
    }

    private static IEnumerable<MethodBase> Methods(Type type) =>
        type.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(type.GetConstructors(DeclaredMembers));

    private static IEnumerable<string> Sources(Type type, MetadataReader reader)
    {
        var handle = MetadataTokens.TypeDefinitionHandle(type.MetadataToken & 0x00ffffff);
        foreach (var information in reader.GetCustomDebugInformation(handle))
        {
            var record = reader.GetCustomDebugInformation(information);
            if (reader.GetGuid(record.Kind) != TypeDefinitionDocuments)
                continue;
            var documents = reader.GetBlobReader(record.Value);
            while (documents.RemainingBytes > 0)
                yield return reader.GetString(reader.GetDocument(
                    MetadataTokens.DocumentHandle(documents.ReadCompressedInteger())).Name);
        }
    }

    private static string[] Sources(MethodBase method, MetadataReader reader)
    {
        var information = reader.GetMethodDebugInformation(
            MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff));
        var documents = information.GetSequencePoints()
            .Where(point => !point.IsHidden && !point.Document.IsNil)
            .Select(point => point.Document);
        if (!information.Document.IsNil)
            documents = documents.Append(information.Document);
        return documents.Distinct().Select(document => reader.GetString(reader.GetDocument(document).Name)).ToArray();
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        if (member is Type type)
        {
            yield return type;
            yield break;
        }
        if (member.DeclaringType is not null)
            yield return member.DeclaringType;
        if (member is FieldInfo field)
            yield return field.FieldType;
        if (member is MethodInfo method)
        {
            yield return method.ReturnType;
            foreach (var argument in method.GetGenericArguments())
                yield return argument;
        }
        if (member is MethodBase callable)
        {
            foreach (var parameter in callable.GetParameters())
                yield return parameter.ParameterType;
        }
    }

    private static IEnumerable<Type> AttributeTypes(MemberInfo member)
    {
        foreach (var attribute in member.GetCustomAttributesData())
        {
            yield return attribute.AttributeType;
            foreach (var argument in attribute.ConstructorArguments
                .Concat(attribute.NamedArguments.Select(argument => argument.TypedValue)))
            {
                foreach (var type in AttributeArgumentTypes(argument))
                    yield return type;
            }
        }
    }

    private static IEnumerable<Type> AttributeArgumentTypes(CustomAttributeTypedArgument argument)
    {
        if (argument.Value is Type type)
            yield return type;
        if (argument.Value is IEnumerable<CustomAttributeTypedArgument> values)
        {
            foreach (var value in values)
            {
                foreach (var nested in AttributeArgumentTypes(value))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<MemberInfo> References(MethodBase method, byte[] code)
    {
        var typeArguments = method.DeclaringType?.GetGenericArguments();
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var offset = 0; offset < code.Length;)
        {
            var value = (short)code[offset++];
            if (value == 0xfe)
                value = unchecked((short)(0xfe00 | code[offset++]));
            var opcode = OpCodesByValue[value];
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField
                or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(code, offset);
                yield return method.Module.ResolveMember(token, typeArguments, methodArguments)
                    ?? throw new InvalidOperationException($"Unresolved IL reference in {method} at {offset}.");
            }
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(code, offset)),
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                _ => throw new InvalidOperationException($"Unsupported IL operand {opcode.OperandType} in {method}.")
            };
        }
    }
}
