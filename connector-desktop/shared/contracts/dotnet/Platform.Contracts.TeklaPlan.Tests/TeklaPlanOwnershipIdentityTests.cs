using Platform.Contracts.TeklaPlan;
using Xunit;

namespace Platform.Contracts.TeklaPlan.Tests;

public sealed class TeklaPlanOwnershipIdentityTests
{
    [Fact]
    public void Identity_is_stable_across_revision_and_generation_changes()
    {
        var first = Plan("project", "scenario", "revision-a", "generation-a", "hash-a");
        var second = Plan("project", "scenario", "revision-b", "generation-b", "hash-b");

        Assert.Equal(
            TeklaPlanOwnershipIdentity.Create(first, first.Commands[0]),
            TeklaPlanOwnershipIdentity.Create(second, second.Commands[0]));
    }

    [Fact]
    public void Identity_changes_for_another_project_or_command()
    {
        var first = Plan("project-a", "scenario", "revision", "generation", "hash");
        var anotherProject = Plan("project-b", "scenario", "revision", "generation", "hash");
        var anotherCommand = Plan("project-a", "scenario", "revision", "generation", "hash");
        anotherCommand.Commands[0].CommandId = "element:beam-1:create-beam-2";

        var identity = TeklaPlanOwnershipIdentity.Create(first, first.Commands[0]);

        Assert.NotEqual(identity, TeklaPlanOwnershipIdentity.Create(anotherProject, anotherProject.Commands[0]));
        Assert.NotEqual(identity, TeklaPlanOwnershipIdentity.Create(anotherCommand, anotherCommand.Commands[0]));
    }

    [Fact]
    public void Length_prefixed_encoding_does_not_alias_delimiter_content()
    {
        var first = Plan("a|1:b", "c", "revision", "generation", "hash");
        var second = Plan("a", "1:b|c", "revision", "generation", "hash");

        Assert.NotEqual(
            TeklaPlanOwnershipIdentity.Create(first, first.Commands[0]),
            TeklaPlanOwnershipIdentity.Create(second, second.Commands[0]));
    }

    private static TeklaPlanDocument Plan(
        string projectId,
        string scenarioId,
        string revisionId,
        string generationId,
        string sourceHash)
    {
        return new TeklaPlanDocument
        {
            Source = new TeklaPlanSource
            {
                Address = new ConstructiveAddress
                {
                    ProjectId = projectId,
                    ScenarioId = scenarioId,
                    ModuleId = "bridge",
                    ModuleVariantId = "main",
                    RevisionId = revisionId,
                },
                GenerationId = generationId,
                SourceHash = sourceHash,
            },
            Commands = new[]
            {
                new TeklaPlanCommand
                {
                    CommandId = "element:beam-1:create-beam",
                    Source = new TeklaPlanSourceRef
                    {
                        Kind = "element",
                        Id = "beam-1",
                        StableKey = "bridge/beam-1",
                    },
                    Ownership = new TeklaOwnershipStamp
                    {
                        Namespace = TeklaPlanContract.OwnershipNamespace,
                    },
                },
            },
        };
    }
}
