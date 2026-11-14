using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using ClrDebug;
using DncDbg.Engine;
using DncDbg.Engine.Symbols;

internal static class ParameterDisplayChecks
{
    public static int Run()
    {
        var fixture = new MetadataBuilder();
        fixture.AddModule(0, fixture.GetOrAddString("DisplayFixture.dll"),
            fixture.GetOrAddGuid(Guid.NewGuid()), default, default);
        fixture.AddAssembly(fixture.GetOrAddString("DisplayFixture"), new Version(1, 0), default, default, 0, 0);
        fixture.AddTypeDefinition(TypeAttributes.NotPublic, default, fixture.GetOrAddString("<Module>"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        TypeDefinitionHandle declaringType = fixture.AddTypeDefinition(TypeAttributes.Public, fixture.GetOrAddString("Fixtures"),
            fixture.GetOrAddString("Signatures`1"), default, MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        fixture.AddGenericParameter(declaringType, 0, fixture.GetOrAddString("T"), 0);
        // TypeRef coded indices: Box`1 = 5, Exception = 9, Modifier = 13.
        fixture.AddTypeReference(default, fixture.GetOrAddString("Fixtures"), fixture.GetOrAddString("Box`1"));
        fixture.AddTypeReference(default, fixture.GetOrAddString("System"), fixture.GetOrAddString("Exception"));
        fixture.AddTypeReference(default, fixture.GetOrAddString("Fixtures"), fixture.GetOrAddString("Modifier"));
        var vectors = new List<(int Token, string Expected, string?[]? Arguments, string?[]? TypeArguments, string Name)>();
        byte[] voidType = [0x01], integer = [0x08], boolean = [0x02], pointer = [0x0f, 0x08];
        byte[] exceptionPointer = [0x0f, 0x12, 0x09];
        byte[] byrefPointer = [0x10, 0x0f, 0x08];
        Add("supported", voidType, [integer, boolean], "(int p0, bool p1)");
        Add("return-pointer", pointer, [integer, boolean], "(p0, bool p1)", [null, "bool"]);
        Add("return-pointer-no-runtime", pointer, [integer, boolean], "(p0, p1)");
        Add("first-pointer", voidType, [pointer, boolean, exceptionPointer],
            "(p0, bool p1, System.Exception* p2)", [null, "bool", "System.Exception*"]);
        Add("first-pointer-no-runtime", voidType, [pointer, boolean], "(p0, p1)");
        Add("prefix-then-pointer", voidType, [integer, pointer, boolean],
            "(int p0, p1, string p2)", [null, null, "string"]);
        Add("available-pointers", voidType, [pointer, exceptionPointer],
            "(int* p0, System.Exception* p1)", ["int*", "System.Exception*"]);
        Add("later-runtime-type", voidType, [pointer, integer], "(p0, Fixtures.Derived p1)", [null, "Fixtures.Derived"]);
        Add("custom-modifiers", voidType, [[0x1f, 0x0d, 0x20, 0x0d, 0x08], boolean], "(int p0, bool p1)");
        Add("modified-pointer", voidType, [[0x20, 0x0d, 0x0f, 0x08], integer], "(p0, p1)");
        Add("array-pointer", voidType, [[0x1d, 0x0f, 0x08], boolean], "(p0, p1)");
        Add("multidimensional-pointer", voidType, [[0x14, 0x0f, 0x08, 0x02, 0x00, 0x00], boolean], "(p0, p1)");
        Add("supported-arrays", voidType, [[0x1d, 0x14, 0x08, 0x02, 0x00, 0x00], boolean], "(int[,][] p0, bool p1)");
        Add("generic-pointer-is-skipped", voidType, [[0x15, 0x12, 0x05, 0x01, 0x0f, 0x08], boolean],
            "(Fixtures.Box<int*> p0, bool p1)");
        Add("nested-generic-array-pointer-is-skipped", voidType,
            [[0x15, 0x12, 0x05, 0x01, 0x15, 0x12, 0x05, 0x01, 0x1d, 0x0f, 0x08], boolean],
            "(Fixtures.Box<Fixtures.Box<int*[]>> p0, bool p1)");
        Add("typed-reference", voidType, [[0x16], boolean], "(p0, p1)");
        Add("function-pointer", voidType, [[0x1b, 0x00, 0x00, 0x01], boolean], "(p0, p1)");
        Add("return-typed-reference", [0x16], [boolean], "(p0)");
        Add("return-function-pointer", [0x1b, 0x00, 0x00, 0x01], [boolean], "(bool p0)", ["bool"]);
        Add("ref-pointer", voidType, [byrefPointer, boolean], "(ref p0, p1)");
        Add("out-pointer", voidType, [byrefPointer, boolean], "(out p0, p1)", attributes: [ParameterAttributes.Out, 0]);
        Add("in-pointer", voidType, [byrefPointer, boolean], "(in p0, p1)", attributes: [ParameterAttributes.In, 0]);
        Add("in-out-pointer", voidType, [byrefPointer], "(ref p0)",
            attributes: [ParameterAttributes.In | ParameterAttributes.Out]);
        Add("available-ref-pointer", voidType, [byrefPointer], "(out int* p0)", ["int*"], attributes: [ParameterAttributes.Out]);
        Add("modifier-after-failure", voidType, [pointer, [0x10, 0x08]], "(p0, int p1)",
            [null, "int"], attributes: [0, ParameterAttributes.Out]);
        Add("supported-ref", voidType, [[0x10, 0x08]], "(out int p0)", attributes: [ParameterAttributes.Out]);
        Add("generic-substitution", voidType, [[0x13, 0x00], [0x1e, 0x00]],
            "(int p0, string p1)", typeArguments: ["System.Int32", "System.String"], generic: true);
        Add("generic-ref-unavailable", voidType, [[0x10, 0x1e, 0x00]], "(ref p0)", [null],
            typeArguments: ["System.Int32", "Fixtures.StateMachine"], generic: true);
        Add("generic-ref-empty", voidType, [[0x10, 0x1e, 0x00]], "(ref p0)", [""],
            typeArguments: ["System.Int32", "Fixtures.StateMachine"], generic: true);
        Add("generic-ref-available", voidType, [[0x10, 0x1e, 0x00]], "(ref Fixtures.ActualState p0)", ["Fixtures.ActualState"],
            typeArguments: ["System.Int32", "Fixtures.StateMachine"], generic: true);
        Add("generic-ref-metadata-only", voidType, [[0x10, 0x1e, 0x00]], "(ref U p0)", generic: true);
        Add("generic-ref-no-fetched-values", voidType, [[0x10, 0x1e, 0x00]], "(ref p0)", [],
            typeArguments: ["System.Int32", "Fixtures.StateMachine"], generic: true);
        Add("generic-value-unavailable", voidType, [[0x13, 0x00]], "(p0)", [null], typeArguments: ["Fixtures.State"]);
        Add("generic-array-unavailable", voidType, [[0x1d, 0x1e, 0x00]], "(p0)", [null], generic: true);
        Add("generic-ref-out", voidType, [[0x10, 0x1e, 0x00]], "(out p0)", [null],
            attributes: [ParameterAttributes.Out], generic: true);
        Add("generic-ref-in", voidType, [[0x10, 0x1e, 0x00]], "(in Fixtures.ActualState p0)", ["Fixtures.ActualState"],
            attributes: [ParameterAttributes.In], generic: true);
        Add("nongeneric-ref-runtime-type", voidType, [[0x10, 0x12, 0x09]],
            "(ref Fixtures.Derived p0)", ["Fixtures.Derived"]);
        Add("nongeneric-ref-empty-type", voidType, [[0x10, 0x08]], "(out p0)", [""], attributes: [ParameterAttributes.Out]);
        Add("nongeneric-empty-type", voidType, [integer], "(p0)", [""]);
        Add("zero", voidType, [], "()");
        int instance = Add("instance", voidType, [integer, boolean], "(int p0, bool p1)", isStatic: false);
        int instanceZero = Add("instance-zero", voidType, [], "()", isStatic: false);
        int staticZero = Add("static-zero", voidType, [], "()");

        string directory = Path.Combine(Path.GetTempPath(), "dncdbg-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int checks = 0;
        var errors = new List<string>();
        try
        {
            string path = Path.Combine(directory, "DisplayFixture.dll");
            var image = new BlobBuilder();
            new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(fixture),
                new BlobBuilder()).Serialize(image);
            File.WriteAllBytes(path, image.ToArray());
            using ModuleMetadata metadata = ModuleMetadata.TryOpen(path)
                ?? throw new InvalidOperationException("Cannot open the parameter-display fixture metadata.");
            foreach (var vector in vectors)
            {
                string actual = metadata.GetParameterDisplay(vector.Token, vector.TypeArguments, vector.Arguments);
                Check(actual == vector.Expected, $"{vector.Name}: expected '{vector.Expected}', got '{actual}'");
            }
            CheckArguments(metadata, instance, count: 3, expectedIndices: [1, 2]);
            CheckArguments(metadata, instanceZero, count: 1, expectedIndices: []);
            CheckArguments(metadata, staticZero, count: 0, expectedIndices: []);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        if (errors.Count != 0)
            throw new InvalidOperationException("Parameter display checks failed:\n" + string.Join('\n', errors));
        return checks + RunAsyncDeclarations();

        int Add(string name, byte[] result, byte[][] parameters, string expected, string?[]? arguments = null,
            ParameterAttributes[]? attributes = null, string?[]? typeArguments = null, bool generic = false, bool isStatic = true)
        {
            var signature = new BlobBuilder();
            signature.WriteByte((byte)((isStatic ? 0 : 0x20) | (generic ? 0x10 : 0)));
            if (generic) signature.WriteCompressedInteger(1);
            signature.WriteCompressedInteger(parameters.Length);
            signature.WriteBytes(result);
            foreach (byte[] parameter in parameters) signature.WriteBytes(parameter);
            ParameterHandle firstParameter = MetadataTokens.ParameterHandle(fixture.GetRowCount(TableIndex.Param) + 1);
            MethodDefinitionHandle method = fixture.AddMethodDefinition(MethodAttributes.Public |
                (isStatic ? MethodAttributes.Static : 0), MethodImplAttributes.IL,
                fixture.GetOrAddString(name), fixture.GetOrAddBlob(signature), 0, firstParameter);
            for (int i = 0; i < parameters.Length; i++)
                fixture.AddParameter(attributes?[i] ?? 0, fixture.GetOrAddString("p" + i), i + 1);
            if (generic) fixture.AddGenericParameter(method, 0, fixture.GetOrAddString("U"), 0);
            int token = MetadataTokens.GetToken(method);
            vectors.Add((token, expected, arguments, typeArguments, name));
            return token;
        }

        void Check(bool success, string message)
        {
            checks++;
            if (!success) errors.Add(message);
        }

        void CheckArguments(ModuleMetadata metadata, int token, int count, int[] expectedIndices)
        {
            var indices = new List<int>();
            ICorDebugValueEnum values = EvaluationChecks.CallbackProxy.Make<ICorDebugValueEnum>((method, arguments) =>
            {
                if (method == "GetCount")
                {
                    arguments![0] = count;
                    return HRESULT.S_OK;
                }
                if (method == "Next")
                {
                    arguments![2] = 0;
                    return HRESULT.S_FALSE;
                }
                throw new NotSupportedException(method);
            });
            ICorDebugILFrame raw = EvaluationChecks.CallbackProxy.Make<ICorDebugILFrame>((method, arguments) =>
            {
                if (method == "EnumerateArguments")
                {
                    arguments![0] = values;
                    return HRESULT.S_OK;
                }
                if (method == "GetArgument")
                {
                    indices.Add((int)arguments![0]!);
                    return HRESULT.CORDBG_E_IL_VAR_NOT_AVAILABLE;
                }
                throw new NotSupportedException(method);
            });
            using var engine = new DebugEngine();
            var result = (IReadOnlyList<string?>?)typeof(DebugEngine).GetMethod("GetArgumentTypeNames",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [new CorDebugILFrame(raw), metadata, token]);
            Check(indices.SequenceEqual(expectedIndices), $"argument count {count}: queried [{string.Join(',', indices)}]");
            Check(result != null && result.Count == expectedIndices.Length && result.All(value => value == null),
                $"argument count {count}: preserve individual unavailable arguments and exclude the receiver");
        }
    }

    public static int RunAsyncDeclarations()
    {
    string assembly = typeof(ParameterDisplayChecks).Assembly.Location;
    using ModuleMetadata metadata = ModuleMetadata.TryOpen(assembly)
        ?? throw new InvalidOperationException("Cannot open async declaration metadata.");
    using MetadataReaderProvider symbols = MetadataReaderProvider.FromPortablePdbStream(
        File.OpenRead(Path.ChangeExtension(assembly, ".pdb")));
    MetadataReader pdb = symbols.GetMetadataReader();
    int checks = 0;
    var errors = new List<string>();
    foreach (string name in new[] { "Static", "Instance", "Concrete" })
    {
        MethodInfo declaration = typeof(AsyncFixture<,>).GetMethod(name)!;
        Type stateMachine = declaration.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        MethodInfo moveNext = stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodDefinitionHandle kickoff = pdb.GetMethodDebugInformation(
            (MethodDefinitionHandle)MetadataTokens.Handle(moveNext.MetadataToken)).GetStateMachineKickoffMethod();
        Check(metadata.HasSymbols && MetadataTokens.GetToken(kickoff) == declaration.MetadataToken,
            name + ": the actual compiler-generated MoveNext must map to its declaration in the portable PDB");
        string expected = name == "Concrete" ? "(string text, int number)"
            : "(string left, int right, Fixtures.Closed value)";
        foreach (string?[]? physicalArguments in new string?[]?[] { null, [], [null], [null, null, null] })
        {
            string actual = metadata.GetParameterDisplay(moveNext.MetadataToken,
                ["System.String", "System.Int32", "Fixtures.Closed"], physicalArguments);
            Check(actual == expected, $"{name}: remapped declaration expected '{expected}', got '{actual}'");
        }
    }
    if (errors.Count != 0)
        throw new InvalidOperationException("Async parameter display checks failed:\n" + string.Join('\n', errors));
    return checks;

    void Check(bool success, string message)
    {
        checks++;
        if (!success) errors.Add(message);
    }
    }

    private sealed class AsyncFixture<TLeft, TRight>
    {
    public static async Task<TValue> Static<TValue>(TLeft left, TRight right, TValue value)
    {
        await Task.Yield();
        return value;
    }

    public async Task<TValue> Instance<TValue>(TLeft left, TRight right, TValue value)
    {
        await Task.Yield();
        return value;
    }

    public static async Task<int> Concrete(string text, int number)
    {
        await Task.Yield();
        return number;
    }
    }
}
