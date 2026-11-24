using DncDbg.Remote;
using System.Buffers.Binary;
using System.Text.Json;

internal static class PrivateBootstrapChecks
{
    public static void Run(Action<bool, string> check)
    {
        PrivateBootstrapIdentity supported = PrivateBootstrapPolicy.SupportedBuilds[0];
        string[] exports = ["DllMain", "CoreCLRCreateCordbObject3"];
        check(PrivateBootstrapPolicy.Validate(supported, exports) == null, "exact pinned private bootstrap identity accepted");
        Reject(supported with { RuntimeVersion = "11.0.0" }, exports, "runtime version");
        Reject(supported with { Platform = "macOS" }, exports, "platform");
        Reject(supported with { Architecture = "x64" }, exports, "architecture");
        Reject(supported with { RuntimeBuildId = new string('0', 32) }, exports, "runtime build identity");
        Reject(supported with { RuntimeHash = new string('0', 64) }, exports, "runtime code hash");
        Reject(supported with { DbiBuildId = new string('0', 32) }, exports, "DBI build identity");
        Reject(supported with { DbiHash = new string('0', 64) }, exports, "DBI code hash");
        Reject(supported with { DacHash = new string('0', 64) }, exports, "DAC code hash");
        Reject(supported with { DacBuildId = new string('0', 32) }, exports, "DAC build identity");
        Reject(supported, ["CoreCLRCreateCordbObject3"], "missing DllMain export");
        Reject(supported, ["DllMain"], "missing factory export");
        var androidGa = new PrivateBootstrapIdentity("Android", "Arm64", "11.0.0",
            "D06B0D176C747F61A57D6929A0915324B4936770", "7A0E0C6E52F626F3DD112C5A3C9748B2454B39B3A356984F0D86357C30D693C8",
            "5FDC6503F66820C1D4ACAB9CC5A9B9C929D1CD38", "F73B25E7268C52698EBEC2F797F3BA6108A31438679FE5D6236587DAD84E6C47",
            "9732F10D985F26F9600A220EFB71B69DBA6642A5", "26C254DE4F484AB637E042C833BBE5318DF0777D4814F8DAC5C1A64776E7A1CA");
        check(PrivateBootstrapPolicy.Validate(androidGa, exports) == null, "signed released Android arm64 CoreCLR/DBI/DAC tuple accepted");
        Reject(androidGa with { RuntimeVersion = "11.0.1" }, exports, "unqualified Android patch");
        Reject(androidGa with { Architecture = "X64" }, exports, "Android arm64 GA substituted for x64");
        Reject(androidGa with { Platform = "iOSDevice" }, exports, "Android GA substituted for Apple");
        Reject(androidGa with { RuntimeVersion = PrivateBootstrapPolicy.RuntimeVersion }, exports, "GA images relabeled as RC1");
        Reject(androidGa with { RuntimeBuildId = new string('0', 40) }, exports, "changed Android GA runtime build ID");
        Reject(androidGa with { RuntimeHash = new string('0', 64) }, exports, "changed Android GA runtime bytes");
        Reject(androidGa with { DbiBuildId = new string('0', 40) }, exports, "changed Android GA DBI build ID");
        Reject(androidGa with { DbiHash = new string('0', 64) }, exports, "changed Android GA DBI bytes");
        Reject(androidGa with { DacBuildId = new string('0', 40) }, exports, "changed Android GA DAC build ID");
        Reject(androidGa with { DacHash = new string('0', 64) }, exports, "changed Android GA DAC bytes");
        Reject(androidGa, ["CoreCLRCreateCordbObject3"], "Android GA missing DllMain export");
        Reject(androidGa, ["DllMain"], "Android GA missing factory export");
        CheckApplicationProfiles(androidGa, check);
        PrivateBootstrapIdentity androidX64 = PrivateBootstrapPolicy.SupportedBuilds.Single(x =>
            x.Platform == "Android" && x.Architecture == "X64");
        check(PrivateBootstrapPolicy.Validate(androidX64, exports) == null, "exact Android x64 mobile private tuple accepted");
        Reject(androidX64 with { Platform = "LinuxNativeHost" }, exports, "Android x64 DBI substituted for desktop Linux");
        Reject(androidX64 with { DbiHash = supported.DbiHash }, exports, "iOS DBI substituted for Android x64");
        Reject(androidX64 with { Architecture = "Arm64" }, exports, "Android x64 identity substituted for local arm64");
        Reject(androidX64, ["DllMain"], "Android x64 missing factory export");
        byte[] elf = new byte[140];
        new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(elf, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(18), 62);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(64), 4);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(72), 120);
        BinaryPrimitives.WriteUInt64LittleEndian(elf.AsSpan(96), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(120), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(124), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(128), 3);
        "GNU\0"u8.CopyTo(elf.AsSpan(132));
        check(NativeImageIdentity.Read(elf, "Android").BuildId == "00000000",
            "read Android x64 ELF build identity and code hash");
        check(PrivateBootstrapPolicy.NativeHostBuilds.Count == 3, "native host has explicit macOS arm64 and Linux x64/arm64 tuples");
        foreach (PrivateBootstrapIdentity native in PrivateBootstrapPolicy.NativeHostBuilds)
        {
            check(PrivateBootstrapPolicy.Validate(native, exports) == null, "exact native-host private tuple accepted: " + native.Platform + "/" + native.Architecture);
            Reject(native with { Platform = "iOSSimulator" }, exports, "native-host DBI substituted for simulator DBI");
            Reject(native with { Platform = "Android" }, exports, "native-host DBI substituted for Android DBI");
            Reject(native with { DbiHash = new string('0', 64) }, exports, "changed native-host DBI hash");
            Reject(native with { RuntimeVersion = "11.0.0" }, exports, "unapproved native-host runtime patch");
        }
        foreach (string platform in new[] { "iOSSimulator", "Android", "macOS" })
        {
            foreach (int length in new[] { 0, 3, 32, 63 })
            {
                try { _ = NativeImageIdentity.Read(new byte[length], platform); }
                catch (InvalidDataException) { check(true, $"reject truncated {platform} image ({length} bytes)"); continue; }
                throw new InvalidOperationException("Malformed native image accepted.");
            }
        }
        byte[] image = new byte[236];
        Write(0, 0xfeedfacf);
        Write(4, 0x0100000c);
        Write(16, 3);
        Write(20, 200);
        Write(32, 0x1b); Write(36, 24);
        Write(56, 0x32); Write(60, 24); Write(64, 7);
        Write(80, 0x19); Write(84, 152); Write(144, 1);
        "__text"u8.CopyTo(image.AsSpan(152));
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(192), 4);
        Write(200, 232);
        NativeImageIdentity valid = NativeImageIdentity.Read(image, "iOSSimulator");
        check(valid.BuildId.Length == 32 && valid.CodeHash.Length == 64, "read simulator UUID and signature-independent code hash");
        Write(64, 1);
        RejectImage("reject desktop Mach-O before target activation");
        Write(64, 7); Write(4, 0x01000007);
        RejectImage("reject x64 Mach-O before target activation");
        using var prior = new RemoteLeaseTable<int>(_ => { });
        using var next = new RemoteLeaseTable<int>(_ => { });
        long stale = prior.Add(1), current = next.Add(2);
        check(stale != current, "different connection generations never alias frame handles");
        try { _ = next.Get(stale); }
        catch (InvalidOperationException) { check(true, "stale connection generation rejected"); return; }
        throw new InvalidOperationException("Stale generation accepted.");

        void Reject(PrivateBootstrapIdentity identity, string[] available, string name) =>
            check(PrivateBootstrapPolicy.Validate(identity, available) is { Length: > 0 }, "reject " + name + " before private activation");
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        void RejectImage(string name)
        {
            try { _ = NativeImageIdentity.Read(image, "iOSSimulator"); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new InvalidOperationException(name + " accepted invalid native image.");
        }
    }

    private static void CheckApplicationProfiles(PrivateBootstrapIdentity androidGa, Action<bool, string> check)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "build/validation/private-bootstrap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string manifest = Path.Combine(directory, "dncdbg-agent.json");
        try
        {
            foreach (var (rid, platform, architecture) in new[]
            {
                ("android-arm64", "Android", "Arm64"), ("android-x64", "Android", "X64"),
                ("iossimulator-arm64", "iOSSimulator", "Arm64"), ("ios-arm64", "iOSDevice", "Arm64"),
            })
            {
                var agent = new TargetAgentIdentity("dncdbg-target", rid, RemoteProtocol.Version,
                    RemoteContract.ModelVersion, new string('b', 64), new string('c', 64),
                    rid.StartsWith("ios", StringComparison.Ordinal) ? "linked-executable" : "native-library");
                File.WriteAllText(manifest, JsonSerializer.Serialize(agent, TargetJson.Default.TargetAgentIdentity));
                PrivateBootstrapIdentity expected = rid == "android-arm64" ? androidGa :
                    PrivateBootstrapPolicy.SupportedBuilds.Single(x => x.Platform == platform && x.Architecture == architecture);
                check(ApplicationProfiles.ForRid(rid, manifest).Runtime == expected,
                    "unambiguous default application profile: " + rid);
                if (rid == "android-arm64")
                {
                    TargetProfile gaProfile = ApplicationProfiles.ForRid(rid, manifest, "11.0.0");
                    PrivateBootstrapIdentity preview = ApplicationProfiles.ForRid(rid, manifest,
                        PrivateBootstrapPolicy.RuntimeVersion).Runtime;
                    check(gaProfile.Runtime == androidGa && preview.RuntimeVersion == PrivateBootstrapPolicy.RuntimeVersion &&
                        preview.RuntimeHash != androidGa.RuntimeHash,
                        "explicit Android versions select distinct exact runtime/DBI/DAC tuples");
                    string session = new('a', 32);
                    var observed = new TargetManifest(1, session, gaProfile with { Runtime = preview },
                        new TargetProcess(123, "fixture.dll", session),
                        new RemoteEndpoint(12345, new string('d', 64), session, 123), new TargetTransport("127.0.0.1"));
                    try
                    {
                        TargetManifestPolicy.Validate(observed, gaProfile, session);
                        throw new InvalidOperationException("RC1 target substituted for an expected GA target.");
                    }
                    catch (InvalidDataException) { check(true, "an independently allowlisted RC1 target cannot replace expected GA"); }
                }
                else
                {
                    RejectVersion("11.0.0");
                }
                RejectVersion("11.0.1");

                void RejectVersion(string version)
                {
                    try { _ = ApplicationProfiles.ForRid(rid, manifest, version); }
                    catch (PlatformNotSupportedException) { check(true, "reject unqualified profile " + rid + "/" + version); return; }
                    throw new InvalidOperationException("An unqualified application profile was selected.");
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
