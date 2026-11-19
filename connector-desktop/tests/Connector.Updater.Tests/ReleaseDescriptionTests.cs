using System.Text;
using Connector.Updates;

namespace Connector.Updater.Tests;

public sealed class ReleaseDescriptionTests : IDisposable
{
    private readonly TestRelease _release = new();

    private ReleaseDescription Verify(string json, string channel = "stable")
    {
        var (bytes, signature) = _release.Signed(json);
        using var verifier = _release.Verifier();
        return verifier.Verify(bytes, signature, channel);
    }

    private string Code(string json, string channel = "stable") =>
        Assert.Throws<UpdateRejectedException>(() => Verify(json, channel)).Code;

    [Fact]
    public void Valid_description_is_accepted_with_every_field()
    {
        var release = Verify(TestRelease.Json(size: 416820148));
        Assert.Equal("stable", release.Channel);
        Assert.Equal(new ReleaseVersion(1, 1, 16), release.Version);
        Assert.Equal(new ReleaseVersion(1, 1, 15), release.MinimumVersion);
        Assert.False(release.Paused);
        Assert.Equal("PlatformConnector-1.1.16.exe", release.Installer.FileName);
        Assert.Equal(416820148, release.Installer.Size);
        Assert.Equal(["Быстрее подключение"], release.Notes);
        Assert.Equal(DateTimeOffset.Parse("2026-11-25T10:00:00Z"), release.PublishedAtUtc);
    }

    [Fact]
    public void Signature_covers_the_exact_bytes()
    {
        var (bytes, signature) = _release.Signed(TestRelease.Json());
        using var verifier = _release.Verifier();
        var tampered = (byte[])bytes.Clone();
        tampered[Array.IndexOf(tampered, (byte)'6')] = (byte)'7'; // 1.1.16 -> 1.1.17 in the version
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(tampered, signature, "stable")).Code);
        // Whitespace is part of the signed bytes too.
        var spaced = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes) + " ");
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(spaced, signature, "stable")).Code);
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(bytes, signature.AsSpan(0, 63), "stable")).Code);
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(bytes, new byte[64], "stable")).Code);
    }

    [Fact]
    public void A_signature_of_another_key_is_refused()
    {
        using var other = new TestRelease();
        var (bytes, signature) = other.Signed(TestRelease.Json());
        using var verifier = _release.Verifier();
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(bytes, signature, "stable")).Code);
    }

    [Fact]
    public void Pilot_release_cannot_be_served_to_a_stable_client()
    {
        Assert.Equal(UpdateErrorCodes.ChannelMismatch, Code(TestRelease.Json(channel: "pilot"), "stable"));
        Assert.Equal("pilot", Verify(TestRelease.Json(channel: "pilot"), "pilot").Channel);
        Assert.Equal(UpdateErrorCodes.ChannelInvalid, Code(TestRelease.Json(), "Stable"));
        Assert.Equal(UpdateErrorCodes.ChannelInvalid, Code(TestRelease.Json(), "../stable"));
    }

    [Theory]
    [InlineData(",\n  \"extra\": 1")]                                // unknown property
    [InlineData(",\n  \"notes\": []")]                                // duplicate property (second would win elsewhere)
    [InlineData(",\n  \"paused\": true")]                             // duplicate that would flip a decision
    public void Unknown_and_duplicate_properties_are_refused(string extra) =>
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(extra: extra)));

    [Fact]
    public void Duplicate_and_unknown_installer_properties_are_refused()
    {
        var json = TestRelease.Json().Replace("\"size\": 1000,", "\"size\": 1000, \"size\": 5,");
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(json));
        json = TestRelease.Json().Replace("\"size\": 1000,", "\"size\": 1000, \"url\": \"https://evil.example/x.exe\",");
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(json));
    }

    [Theory]
    [InlineData("PlatformConnector-1.1.15.exe")]          // another version's file
    [InlineData("../PlatformConnector-1.1.16.exe")]
    [InlineData("PlatformConnector-1.1.16.exe:ads")]
    [InlineData("platformconnector-1.1.16.exe")]
    [InlineData("PlatformConnector-1.1.16.msi")]
    [InlineData("C:\\\\Windows\\\\PlatformConnector-1.1.16.exe")]
    public void Installer_file_name_must_be_exactly_that_of_the_version(string fileName) =>
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(fileName: fileName)));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("2147483648000")]   // over the 2 GiB bound
    [InlineData("1000.0")]
    [InlineData("1e3")]
    [InlineData("\"1000\"")]
    public void Installer_size_is_a_bounded_plain_integer(string size) =>
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json().Replace("\"size\": 1000", "\"size\": " + size)));

    [Theory]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Installer_hash_is_sha256_hex(string sha) =>
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(sha256: sha)));

    [Theory]
    [InlineData("1.1")]
    [InlineData("1.1.16.0")]
    [InlineData("1.01.16")]
    [InlineData("1.1.16-beta")]
    [InlineData("v1.1.16")]
    public void Version_has_exactly_three_plain_parts(string version) =>
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(version: version, fileName: "PlatformConnector-" + version + ".exe")));

    [Fact]
    public void Other_schema_application_or_minimum_above_version_are_refused()
    {
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(schema: "1")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(applicationId: "Structura.Connector.Desktop")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(minimum: "1.1.17")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json().Replace("\"paused\": false", "\"paused\": \"false\"")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json().Replace("2026-11-25T10:00:00Z", "25.11.2026")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(notes: "[\"a\\u0007b\"]")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(notes: "[1]")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json(notes: "[\"\\ud800\"]")));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code("[" + TestRelease.Json() + "]"));
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(TestRelease.Json() + " // comment"));
    }

    [Fact]
    public void Missing_property_is_refused()
    {
        var json = TestRelease.Json().Replace("  \"paused\": false,\n", "");
        Assert.Equal(UpdateErrorCodes.DescriptionInvalid, Code(json));
    }

    [Fact]
    public void Oversized_description_is_refused_before_parsing()
    {
        var json = TestRelease.Json(notes: "[\"" + new string('x', 70_000) + "\"]");
        Assert.Equal(UpdateErrorCodes.DescriptionTooLarge, Code(json));
    }

    [Fact]
    public void Pinned_product_key_is_compiled_in_and_is_p256()
    {
        using var verifier = ReleaseDescriptionVerifier.CreatePinned();
        var (bytes, signature) = _release.Signed(TestRelease.Json());
        // A valid signature of any key but the product's is refused by the production verifier.
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, Assert.Throws<UpdateRejectedException>(() => verifier.Verify(bytes, signature, "stable")).Code);
    }

    public void Dispose() => _release.Dispose();
}

public sealed class ReleasePolicyTests
{
    private static readonly ReleaseVersion Installed = new(1, 1, 15);

    [Fact]
    public void Only_a_strictly_newer_release_is_installed()
    {
        Assert.Equal(ReleaseDecision.Install, ReleasePolicy.Decide(TestRelease.Description("1.1.16"), Installed, []));
        Assert.Equal(ReleaseDecision.UpToDate, ReleasePolicy.Decide(TestRelease.Description("1.1.15"), Installed, []));
        // An old, validly signed description replayed by the network cannot roll back.
        Assert.Equal(ReleaseDecision.UpToDate, ReleasePolicy.Decide(TestRelease.Description("1.1.14", minimum: "1.1.0"), Installed, []));
    }

    [Fact]
    public void Paused_minimum_and_problem_versions_stop_the_installation()
    {
        Assert.Equal(ReleaseDecision.Paused, ReleasePolicy.Decide(TestRelease.Description("1.1.16", paused: true), Installed, []));
        Assert.Equal(ReleaseDecision.BelowMinimumVersion, ReleasePolicy.Decide(TestRelease.Description("1.2.0", minimum: "1.1.20"), Installed, []));
        Assert.Equal(ReleaseDecision.MarkedProblem, ReleasePolicy.Decide(TestRelease.Description("1.1.16"), Installed, [new ReleaseVersion(1, 1, 16)]));
        Assert.Equal(ReleaseDecision.Install, ReleasePolicy.Decide(TestRelease.Description("1.1.17"), Installed, [new ReleaseVersion(1, 1, 16)]));
    }

    [Theory]
    [InlineData("1.1.15", true)]
    [InlineData("10.20.30", true)]
    [InlineData("0.0.0", true)]
    [InlineData("1.1", false)]
    [InlineData("1.1.15.0", false)]
    [InlineData("01.1.15", false)]
    [InlineData("1.1.+5", false)]
    [InlineData("1.1.70000", false)]
    [InlineData(" 1.1.15", false)]
    [InlineData("", false)]
    public void Version_spelling_is_strict(string text, bool valid) => Assert.Equal(valid, ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_numerically() => Assert.True(ReleaseVersion.Parse("1.1.10") > ReleaseVersion.Parse("1.1.9"));
}
