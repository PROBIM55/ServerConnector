using System.Security;
using Connector.Upgrade.MachineServiceOrigin;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachineServiceOrigin.Tests;

public sealed class MachineServiceOriginVerifierTests
{
    [Fact]
    public void Fixed_service_identity_is_not_caller_selected()
    {
        Assert.Equal("StructuraConnectorMachineService", MachineServiceOriginVerifier.ServiceName);
        Assert.Equal("S-1-5-18", MachineServiceOriginVerifier.LocalSystemSid);
    }

    [Fact]
    public void Client_side_verification_fails_closed_even_with_valid_handle_and_pin()
    {
        using var invalidPipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        Assert.Throws<SecurityException>(() => MachineServiceOriginVerifier.AssertTrustedServer(
            invalidPipe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "service.exe"),
            new string('A', 64)));
    }

    [Fact]
    public void Client_side_verification_fails_closed_even_when_pin_is_malformed()
    {
        using var invalidPipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        Assert.Throws<SecurityException>(() => MachineServiceOriginVerifier.AssertTrustedServer(
            invalidPipe,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "service.exe"),
            "abc"));
    }

    [Fact]
    public void Client_side_verification_fails_closed_without_trusting_an_unprotected_path()
    {
        using var invalidPipe = new SafePipeHandle(new IntPtr(1), ownsHandle: false);
        Assert.Throws<SecurityException>(() => MachineServiceOriginVerifier.AssertTrustedServer(
            invalidPipe,
            Path.Combine(Path.GetTempPath(), "service.exe"),
            new string('A', 64)));
    }

    [Theory]
    [InlineData(0x10u, 4u, 42u, 42u, true)]
    [InlineData(0x20u, 4u, 42u, 42u, false)]
    [InlineData(0x10u, 1u, 42u, 42u, false)]
    [InlineData(0x10u, 4u, 43u, 42u, false)]
    [InlineData(0x10u, 4u, 0u, 0u, false)]
    public void Service_status_must_be_running_own_process_with_exact_pipe_pid(
        uint serviceType, uint state, uint servicePid, uint pipePid, bool expected) =>
        Assert.Equal(expected, MachineServiceOriginPolicy.IsMatchingRunningService(serviceType, state, servicePid, pipePid));

    [Fact]
    public void Only_exact_local_system_sid_is_accepted() =>
        Assert.True(MachineServiceOriginPolicy.IsLocalSystem("S-1-5-18"));

    [Theory]
    [InlineData("S-1-5-32-544")]
    [InlineData(null)]
    public void Other_or_missing_process_sid_is_rejected(string? sid) =>
        Assert.False(MachineServiceOriginPolicy.IsLocalSystem(sid));

    [Fact]
    public void Image_and_pin_comparisons_are_exact_except_path_case()
    {
        Assert.True(MachineServiceOriginPolicy.PathsMatch(@"C:\Program Files\Structura\Service.exe", @"c:\program files\structura\service.exe"));
        Assert.False(MachineServiceOriginPolicy.PathsMatch(@"C:\Program Files\Structura\Other.exe", @"C:\Program Files\Structura\Service.exe"));
        Assert.True(MachineServiceOriginPolicy.HashesMatch(new string('A', 64), new string('a', 64)));
        Assert.False(MachineServiceOriginPolicy.HashesMatch(new string('A', 64), new string('B', 64)));
    }
}
