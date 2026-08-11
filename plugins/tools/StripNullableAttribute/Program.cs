using System.Reflection;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: StripNullableAttribute <UnityEngine.CoreModule.dll> <BepInEx/core dir>");
    return 2;
}

var corePath = Path.GetFullPath(args[0]);
var bepInExCoreDir = Path.GetFullPath(args[1]);

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    var simpleName = eventArgs.Name.Split(',')[0];
    var candidate = Path.Combine(bepInExCoreDir, simpleName + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

_ = Assembly.LoadFrom(Path.Combine(bepInExCoreDir, "AsmResolver.dll"));
_ = Assembly.LoadFrom(Path.Combine(bepInExCoreDir, "AsmResolver.PE.File.dll"));
_ = Assembly.LoadFrom(Path.Combine(bepInExCoreDir, "AsmResolver.PE.dll"));
var asmResolverDotNet = Assembly.LoadFrom(Path.Combine(bepInExCoreDir, "AsmResolver.DotNet.dll"));
var moduleType = asmResolverDotNet.GetType("AsmResolver.DotNet.ModuleDefinition", throwOnError: true)!;
var module = moduleType.GetMethod("FromFile", new[] { typeof(string) })!.Invoke(null, new object[] { corePath })!;

static void RemoveNullableAttrs(dynamic? provider)
{
    if (provider is null)
        return;

    var customAttributes = provider.CustomAttributes;
    if (customAttributes is null)
        return;

    var snapshot = new List<object>();
    foreach (var attr in customAttributes)
        snapshot.Add(attr);

    foreach (dynamic attr in snapshot)
    {
        string? name = null;
        try
        {
            name = attr.Constructor?.DeclaringType?.FullName;
        }
        catch
        {
            // Ignore attributes we cannot inspect.
        }

        if (name is "System.Runtime.CompilerServices.NullableAttribute"
            or "System.Runtime.CompilerServices.NullableContextAttribute")
        {
            customAttributes.Remove(attr);
        }
    }
}

dynamic dynModule = module;
foreach (var typeObj in dynModule.GetAllTypes())
{
    dynamic type = typeObj;
    RemoveNullableAttrs(type);

    foreach (var methodObj in type.Methods)
    {
        dynamic method = methodObj;
        RemoveNullableAttrs(method);
        if (method.ParameterDefinitions is not null)
        {
            foreach (var parameter in method.ParameterDefinitions)
                RemoveNullableAttrs(parameter);
        }
    }

    foreach (var field in type.Fields)
        RemoveNullableAttrs(field);
    foreach (var property in type.Properties)
        RemoveNullableAttrs(property);
    foreach (var evt in type.Events)
        RemoveNullableAttrs(evt);
    foreach (var genericParameter in type.GenericParameters)
        RemoveNullableAttrs(genericParameter);
}

object? nullableType = null;
foreach (var typeObj in dynModule.GetAllTypes())
{
    dynamic type = typeObj;
    if ((string)type.FullName == "System.Runtime.CompilerServices.NullableAttribute")
    {
        nullableType = typeObj;
        break;
    }
}

if (nullableType is not null)
{
    dynamic t = nullableType;
    object collection = t.DeclaringType != null
        ? (object)t.DeclaringType.NestedTypes
        : (object)dynModule.TopLevelTypes;

    var remove = collection.GetType().GetMethod("Remove", new[] { nullableType.GetType() })
        ?? collection.GetType().GetMethods().FirstOrDefault(m =>
            m.Name == "Remove" && m.GetParameters().Length == 1);

    if (remove is null)
        throw new InvalidOperationException("Could not find Remove method on type collection.");

    remove.Invoke(collection, new[] { nullableType });
}

module.GetType().GetMethod("Write", new[] { typeof(string) })!.Invoke(module, new object[] { corePath });
Console.WriteLine("Patched: " + corePath);
return 0;
